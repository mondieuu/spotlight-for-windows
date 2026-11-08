using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Enumeration;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Odak;

/// <summary>Search result: full path, whether it is a folder, score.</summary>
public readonly record struct FileHit(string Path, bool IsDirectory, int Score);

/// <summary>
/// In-memory file index that scans every fixed drive on the PC.
/// - Each entry stores only its name + parent folder number (paths are assembled at search time) → low memory.
/// - The index is cached to disk; it loads instantly at startup and is refreshed in the background at low priority.
/// - While running, new/deleted files are picked up immediately via FileSystemWatcher.
/// - Search is a parallel, all-cores substring match that ignores Turkish/accented character differences.
/// </summary>
public static class FileIndex
{
    private const byte FlagDir = 1;
    private static readonly TimeSpan RescanEvery = TimeSpan.FromHours(3);

    // Immutable snapshot: swapped in one step when a scan finishes; searches read it without locks.
    private sealed class Snapshot
    {
        public string[] Names = Array.Empty<string>();
        public int[] Parent = Array.Empty<int>();
        public byte[] Flags = Array.Empty<byte>();
        public int Count;
    }

    private static volatile Snapshot _snap = new();
    private static readonly ConcurrentDictionary<string, bool> Added = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, byte> Removed = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<FileSystemWatcher> Watchers = new();
    private static int _scanning;
    private static Timer? _timer;
    private static DateTime _lastOverflowScan = DateTime.MinValue;

    public static bool IsReady => _snap.Count > 0;
    public static bool IsScanning => _scanning == 1;
    public static int Count => _snap.Count;

    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotlight");
    private static string CacheFile => Path.Combine(DataDir, "files.idx");

    // Large folders that are skipped: they contain only system copies/caches.
    private static readonly HashSet<string> SkipPaths = BuildSkipPaths();
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "$Recycle.Bin", "System Volume Information", "$WinREAgent", "Config.Msi", "node_modules", ".git",
        "__pycache__", "Cache", "Code Cache", "GPUCache", "ShaderCache", "DawnCache", "GrShaderCache", "Service Worker",
    };

    private static HashSet<string> BuildSkipPaths()
    {
        var win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(win, "WinSxS"), Path.Combine(win, "Installer"), Path.Combine(win, "servicing"),
            Path.Combine(win, "SoftwareDistribution"), Path.Combine(win, "assembly"), Path.Combine(win, "Prefetch"),
            Path.Combine(win, "Temp"), Path.Combine(win, "Logs"), Path.Combine(win, "WinSxS"),
            Path.Combine(local, "Temp"), Path.Combine(local, "Packages"), Path.Combine(local, "Microsoft", "Windows", "INetCache"),
            Path.Combine(local, "Microsoft", "Windows", "Explorer"), DataDir,
        };
    }

    /// <summary>Loads the cache, then starts scanning and watching in the background.</summary>
    public static void Start()
    {
        Task.Run(() =>
        {
            bool loaded = TryLoad();
            StartWatchers();
            var age = File.Exists(CacheFile) ? DateTime.Now - File.GetLastWriteTime(CacheFile) : TimeSpan.MaxValue;
            // If a cache exists, wait a little so startup is not slowed down; otherwise scan right away.
            var first = !loaded ? TimeSpan.Zero : age > TimeSpan.FromMinutes(30) ? TimeSpan.FromSeconds(20) : RescanEvery - age;
            if (first < TimeSpan.Zero) first = TimeSpan.Zero;
            _timer = new Timer(_ => Rescan(), null, first, RescanEvery);
        });
    }

    // ---- Scanning

    private static void Rescan()
    {
        if (Interlocked.Exchange(ref _scanning, 1) == 1) return;
        var thread = new Thread(() =>
        {
            try
            {
                // Changes from before the scan are in the new snapshot. Ones arriving during it may not be, so they stay.
                var addedBefore = Added.Keys.ToArray();
                var removedBefore = Removed.Keys.ToArray();
                var snap = Scan();
                _snap = snap;
                foreach (var key in addedBefore) Added.TryRemove(key, out _);
                foreach (var key in removedBefore) Removed.TryRemove(key, out _);
                Save(snap);
            }
            catch { }
            finally { Interlocked.Exchange(ref _scanning, 0); }
        })
        { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Spotlight dosya taraması" };
        thread.Start();
    }

    private readonly record struct Entry(string Name, bool Dir, FileAttributes Attr);

    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private const FileAttributes CloudAttrs = (FileAttributes)0x400000 | (FileAttributes)0x40000 | (FileAttributes)0x80000 | (FileAttributes)0x100000;

    private static Snapshot Scan()
    {
        var names = new List<string>(500_000);
        var parent = new List<int>(500_000);
        var flags = new List<byte>(500_000);
        var stack = new Stack<(string path, int index)>();
        // Most file names repeat (index.js, icon.png…): each distinct name is stored only once, which saves a lot of memory.
        var pool = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            names.Add(drive.RootDirectory.FullName); // "C:\"
            parent.Add(-1);
            flags.Add(FlagDir);
            stack.Push((drive.RootDirectory.FullName, names.Count - 1));
        }

        while (stack.Count > 0)
        {
            var (dir, index) = stack.Pop();
            List<Entry> entries;
            try
            {
                entries = new FileSystemEnumerable<Entry>(dir,
                    (ref FileSystemEntry e) => new Entry(e.FileName.ToString(), e.IsDirectory, e.Attributes), Options).ToList();
            }
            catch { continue; }

            foreach (var e in entries)
            {
                if (e.Dir)
                {
                    // Hidden+system folders (e.g. System Volume Information) and links (junctions) are skipped;
                    // cloud folders such as OneDrive do not count as links and are scanned.
                    if ((e.Attr & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System)) continue;
                    if (SkipNames.Contains(e.Name)) continue;
                    var full = Path.Join(dir, e.Name);
                    if (SkipPaths.Contains(full)) continue;
                    names.Add(Pooled(pool, e.Name));
                    parent.Add(index);
                    flags.Add(FlagDir);
                    bool link = (e.Attr & FileAttributes.ReparsePoint) != 0 && (e.Attr & CloudAttrs) == 0;
                    if (!link) stack.Push((full, names.Count - 1));
                }
                else
                {
                    names.Add(Pooled(pool, e.Name));
                    parent.Add(index);
                    flags.Add(0);
                }
            }
        }
        return new Snapshot { Names = names.ToArray(), Parent = parent.ToArray(), Flags = flags.ToArray(), Count = names.Count };
    }

    private static string Pooled(Dictionary<string, string> pool, string name)
    {
        if (pool.TryGetValue(name, out var existing)) return existing;
        pool[name] = name;
        return name;
    }

    private static bool Skipped(string fullPath)
    {
        foreach (var skip in SkipPaths)
            if (fullPath.Equals(skip, StringComparison.OrdinalIgnoreCase) ||
                (fullPath.Length > skip.Length && fullPath[skip.Length] == '\\' && fullPath.StartsWith(skip, StringComparison.OrdinalIgnoreCase))) return true;
        foreach (var part in fullPath.Split('\\'))
            if (SkipNames.Contains(part)) return true;
        return false;
    }

    // ---- Live changes

    private static void StartWatchers()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            try
            {
                var w = new FileSystemWatcher(drive.RootDirectory.FullName)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => OnAdded(e.FullPath);
                w.Renamed += (_, e) => { OnRemoved(e.OldFullPath); OnAdded(e.FullPath); };
                w.Deleted += (_, e) => OnRemoved(e.FullPath);
                // Too many changes at once overflow the watcher and some are lost: scan again, at most every 10 minutes.
                w.Error += (_, _) =>
                {
                    if (DateTime.UtcNow - _lastOverflowScan < TimeSpan.FromMinutes(10)) return;
                    _lastOverflowScan = DateTime.UtcNow;
                    Rescan();
                };
                w.EnableRaisingEvents = true;
                Watchers.Add(w);
            }
            catch { }
        }
    }

    private static void OnAdded(string path)
    {
        if (Skipped(path)) return;
        Removed.TryRemove(path, out _);
        if (Added.Count < 50_000) Added[path] = Directory.Exists(path);
    }

    private static void OnRemoved(string path)
    {
        if (Skipped(path)) return;
        if (!Added.TryRemove(path, out _) && Removed.Count < 50_000) Removed[path] = 0;
    }

    // ---- Search

    // Character folding table: lowercase + Turkish/accented letters → ASCII ("Şirket" → "sirket").
    private static readonly char[] FoldTable = BuildFoldTable();

    private static char[] BuildFoldTable()
    {
        var t = new char[65536];
        for (int i = 0; i < t.Length; i++)
        {
            var c = (char)i;
            if (char.IsSurrogate(c)) { t[i] = c; continue; }
            var lower = char.ToLowerInvariant(c);
            t[i] = lower switch
            {
                'ç' => 'c', 'ğ' => 'g', 'ı' => 'i', 'ö' => 'o', 'ş' => 's', 'ü' => 'u',
                'â' => 'a', 'î' => 'i', 'û' => 'u', 'é' => 'e', 'è' => 'e', 'á' => 'a', 'à' => 'a',
                _ => lower,
            };
        }
        t['İ'] = 'i';
        t['I'] = 'i';
        return t;
    }

    private static string FoldQuery(string q)
    {
        var chars = q.ToCharArray();
        for (int i = 0; i < chars.Length; i++) chars[i] = FoldTable[chars[i]];
        return new string(chars);
    }

    /// <summary>Position of the first occurrence of the term in the folded name (-1 = none).</summary>
    private static int Find(string name, string term)
    {
        int n = name.Length, m = term.Length;
        if (m > n) return -1;
        char first = term[0];
        var fold = FoldTable;
        for (int i = 0; i <= n - m; i++)
        {
            if (fold[name[i]] != first) continue;
            int k = 1;
            while (k < m && fold[name[i + k]] == term[k]) k++;
            if (k == m) return i;
        }
        return -1;
    }

    private static readonly HashSet<string> SystemExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll", ".mui", ".sys", ".cat", ".mum", ".manifest", ".pdb", ".pri", ".etl", ".tlb", ".ax", ".winmd",
    };

    private static int ScoreName(string name, string[] terms, bool isDir)
    {
        int pos = Find(name, terms[0]);
        if (pos < 0) return 0;
        for (int t = 1; t < terms.Length; t++) if (Find(name, terms[t]) < 0) return 0;

        var q = terms[0];
        int score;
        if (pos == 0 && name.Length == q.Length) score = 1000;                                   // exact name
        else if (pos == 0 && name.Length > q.Length && name[q.Length] == '.' && name.IndexOf('.', q.Length + 1) < 0) score = 900; // exact name without extension
        else if (pos == 0) score = 700;                                                          // starts with
        else if (!char.IsLetterOrDigit(name[pos - 1])) score = 500;                               // word start
        else score = 250;                                                                        // contains

        if (isDir) score += 15;
        else
        {
            var ext = Path.GetExtension(name);
            if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase) || ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) score += 40;
            else if (SystemExts.Contains(ext))
            {
                // System libraries are shown only when the extension is typed ("shell32.dll"); otherwise they clutter the results.
                if (!terms.Any(t => t.Contains('.'))) return 0;
                score -= 120;
            }
        }
        return score - Math.Min(name.Length, 60);
    }

    private static string FullPath(Snapshot s, int index)
    {
        var parts = new Stack<string>();
        for (int i = index; i >= 0; i = s.Parent[i]) parts.Push(s.Names[i]);
        var root = parts.Pop();
        return parts.Count == 0 ? root : Path.Join(root, string.Join('\\', parts));
    }

    private static int Penalty(string path)
    {
        // The user's own files come first; system/app-data folders rank lower.
        if (path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase)) return 140;
        if (path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase)) return 120;
        if (path.StartsWith(@"C:\ProgramData\", StringComparison.OrdinalIgnoreCase)) return 90;
        return Math.Min(path.Count(c => c == '\\') * 4, 40);
    }

    /// <summary>The path, or a folder containing it, was deleted since the last scan.</summary>
    private static bool IsRemoved(string path)
    {
        if (Removed.IsEmpty) return false;
        for (string? p = path; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if (Removed.ContainsKey(p)) return true;
        return false;
    }

    /// <summary>Searches file and folder names; returns the best <paramref name="max"/> results.</summary>
    public static List<FileHit> Search(string query, int max, CancellationToken ct)
    {
        var terms = FoldQuery(query.Trim()).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (terms.Length == 0) return new();
        var s = _snap;
        var bag = new ConcurrentBag<List<(int index, int score)>>();
        const int Keep = 64;

        Parallel.For(0, Environment.ProcessorCount, new ParallelOptions { CancellationToken = ct }, worker =>
        {
            int chunk = (s.Count + Environment.ProcessorCount - 1) / Environment.ProcessorCount;
            int from = worker * chunk, to = Math.Min(s.Count, from + chunk);
            var top = new List<(int index, int score)>(Keep + 1);
            int min = int.MinValue;
            for (int i = from; i < to; i++)
            {
                if ((i & 0x3FFF) == 0 && ct.IsCancellationRequested) return;
                if (s.Parent[i] < 0) continue;
                int score = ScoreName(s.Names[i], terms, s.Flags[i] == FlagDir);
                if (score <= 0 || (top.Count >= Keep && score <= min)) continue;
                top.Add((i, score));
                if (top.Count > Keep)
                {
                    top.Sort((a, b) => b.score.CompareTo(a.score));
                    top.RemoveAt(top.Count - 1);
                    min = top[^1].score;
                }
            }
            bag.Add(top);
        });
        ct.ThrowIfCancellationRequested();

        var hits = new List<FileHit>();
        foreach (var (index, score) in bag.SelectMany(b => b).OrderByDescending(x => x.score).Take(Keep * 2))
        {
            var path = FullPath(s, index);
            if (IsRemoved(path)) continue;
            hits.Add(new FileHit(path, s.Flags[index] == FlagDir, score - Penalty(path)));
        }
        foreach (var (path, isDir) in Added)
        {
            var name = Path.GetFileName(path);
            if (name.Length == 0) continue;
            int score = ScoreName(name, terms, isDir);
            if (score > 0 && !hits.Any(h => h.Path.Equals(path, StringComparison.OrdinalIgnoreCase)))
                hits.Add(new FileHit(path, isDir, score - Penalty(path) + 5));
        }
        return hits.OrderByDescending(h => h.Score).Take(max).ToList();
    }

    // ---- Cache

    private static void Save(Snapshot s)
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            var tmp = CacheFile + ".tmp";
            using (var w = new BinaryWriter(new BufferedStream(File.Create(tmp), 1 << 20)))
            {
                w.Write(1); // version
                w.Write(s.Count);
                for (int i = 0; i < s.Count; i++)
                {
                    w.Write(s.Names[i]);
                    w.Write(s.Parent[i]);
                    w.Write(s.Flags[i]);
                }
            }
            File.Move(tmp, CacheFile, overwrite: true);
        }
        catch { }
    }

    private static bool TryLoad()
    {
        try
        {
            if (!File.Exists(CacheFile)) return false;
            using var r = new BinaryReader(new BufferedStream(File.OpenRead(CacheFile), 1 << 20));
            if (r.ReadInt32() != 1) return false;
            int count = r.ReadInt32();
            var s = new Snapshot { Names = new string[count], Parent = new int[count], Flags = new byte[count], Count = count };
            var pool = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                s.Names[i] = Pooled(pool, r.ReadString());
                s.Parent[i] = r.ReadInt32();
                s.Flags[i] = r.ReadByte();
            }
            _snap = s;
            return true;
        }
        catch { return false; }
    }
}

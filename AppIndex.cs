using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Odak;

/// <summary>A searchable name (the main name, the name in the other language, or keywords).</summary>
public sealed class SearchKey
{
    public SearchKey(string text, int penalty, bool keyword)
    {
        Folded = Text.Fold(text);
        Words = Folded.Split(new[] { ' ', '-', '_', '.', '(', ')', '&', ',', '/' }, StringSplitOptions.RemoveEmptyEntries);
        Initials = string.Concat(Words.Select(w => w[0]));
        Penalty = penalty;
        IsKeyword = keyword;
    }

    public string Folded { get; }
    public string[] Words { get; }
    public string Initials { get; }
    public int Penalty { get; }
    public bool IsKeyword { get; }
}

/// <summary>A launchable result: a Start menu app, a Windows tool, a Settings page or a folder.</summary>
public sealed class AppEntry
{
    public AppEntry(string name, string parsingName, EntryKind kind = EntryKind.App)
    {
        Name = name;
        ParsingName = parsingName;
        Kind = kind;
        Keys.Add(new SearchKey(name, 0, keyword: false));
    }

    public string Name { get; }
    /// <summary>Shell parsing name (the AUMID for Store apps), or a unique key for a system entry.</summary>
    public string ParsingName { get; }
    public EntryKind Kind { get; }
    /// <summary>English and Turkish names from the catalog ("Device Manager" / "Aygıt Yöneticisi"), if known.</summary>
    public string? En { get; set; }
    public string? Tr { get; set; }

    /// <summary>The title in the language chosen in Settings (falls back to the Windows name).</summary>
    public string DisplayName => (AppSettings.Current.IsTurkish ? Tr : En) ?? Name;

    /// <summary>The name in the other language, shown under the title; null if it is the same.</summary>
    public string? AltDisplayName
    {
        get
        {
            var other = (AppSettings.Current.IsTurkish ? En : Tr) ?? (Name != DisplayName ? Name : null);
            return other != null && Text.Fold(other) != Text.Fold(DisplayName) ? other : null;
        }
    }
    public string? Command { get; init; }
    public string? Args { get; init; }
    /// <summary>Shell path to take the icon from; if null, a symbol is shown.</summary>
    public string? IconPath { get; init; }
    public List<SearchKey> Keys { get; } = new();
    public ImageSource? Icon { get; set; }

    public void AddName(string name, int penalty = 2)
    {
        var folded = Text.Fold(name);
        if (Keys.Any(k => !k.IsKeyword && k.Folded == folded)) return;
        Keys.Add(new SearchKey(name, penalty, keyword: false));
    }

    public void AddKeywords(string keywords)
    {
        if (!string.IsNullOrWhiteSpace(keywords)) Keys.Add(new SearchKey(keywords, 0, keyword: true));
    }
}

public static class Text
{
    private static readonly CultureInfo Tr = new("tr-TR");

    /// <summary>Lowercase + Turkish character folding: "Şirket Ağı" → "sirket agi".</summary>
    public static string Fold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.ToLower(Tr))
        {
            sb.Append(ch switch
            {
                'ç' => 'c', 'ğ' => 'g', 'ı' => 'i', 'ö' => 'o', 'ş' => 's', 'ü' => 'u',
                'â' => 'a', 'î' => 'i', 'û' => 'u', 'é' => 'e', 'è' => 'e', 'á' => 'a',
                _ => ch,
            });
        }
        return sb.ToString();
    }

    /// <summary>Edit distance for short words (transpositions included): "manger" ↔ "manager" = 1.</summary>
    public static int Distance(string a, string b)
    {
        int n = a.Length, m = b.Length;
        if (Math.Abs(n - m) > 2) return 3;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;
        for (int i = 1; i <= n; i++)
        for (int j = 1; j <= m; j++)
        {
            int cost = a[i - 1] == b[j - 1] ? 0 : 1;
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
        }
        return d[n, m];
    }
}

/// <summary>
/// App index: reads the "All apps" folder (shell:AppsFolder), merges it with the bilingual Windows
/// catalog, searches it and launches results.
/// </summary>
public static class AppIndex
{
    // Whole words only: "Help" is junk, but "HelpDesk Pro" or "Helper Studio" are real apps.
    private static readonly Regex Junk = new(@"\b(uninstall|kaldir|readme|release notes|documentation|website|help|yardim|lisans|license)\b", RegexOptions.CultureInvariant);

    private static List<AppEntry> _apps = new();
    private static DateTime _loadedAt = DateTime.MinValue;
    private static readonly Dictionary<string, int> Usage = LoadUsage();

    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotlight");
    private static string UsageFile => Path.Combine(DataDir, "usage.json");

    public static IReadOnlyList<AppEntry> Apps => _apps;

    /// <summary>Refreshes the index in the background (on an STA thread, for shell COM) every 5 minutes.</summary>
    public static Task RefreshAsync(bool force = false)
    {
        if (!force && DateTime.Now - _loadedAt < TimeSpan.FromMinutes(5) && _apps.Count > 0) return Task.CompletedTask;
        var tcs = new TaskCompletionSource();
        var thread = new Thread(() =>
        {
            try
            {
                var list = Load();
                if (list.Count > 0)
                {
                    // Keep icons that were already loaded.
                    var old = _apps.GroupBy(a => a.ParsingName).ToDictionary(g => g.Key, g => g.First().Icon);
                    foreach (var a in list) if (old.TryGetValue(a.ParsingName, out var icon)) a.Icon = icon;
                    _apps = list;
                    _loadedAt = DateTime.Now;
                }
            }
            catch { /* if the folder cannot be read, the old list stays */ }
            tcs.TrySetResult();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    private static List<AppEntry> Load()
    {
        var list = new List<AppEntry>();
        var shellType = Type.GetTypeFromProgID("Shell.Application");
        if (shellType != null)
        {
            dynamic shell = Activator.CreateInstance(shellType)!;
            // shell:AppsFolder = "All apps" in the Start menu
            dynamic folder = shell.NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                var folded = Text.Fold(name);
                if (Junk.IsMatch(folded)) continue;
                list.Add(new AppEntry(name, path) { IconPath = "shell:AppsFolder\\" + path });
            }
        }
        list = list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        MergeCatalog(list);
        return list.OrderBy(a => a.Name, StringComparer.Create(new CultureInfo("tr-TR"), true)).ToList();
    }

    /// <summary>
    /// For each catalog entry: if a Start menu app has the same name, the other-language name and keywords
    /// are added to it; otherwise (if it exists on this PC) it becomes a separate result with its own command.
    /// </summary>
    private static void MergeCatalog(List<AppEntry> list)
    {
        var byName = new Dictionary<string, AppEntry>();
        foreach (var a in list) byName.TryAdd(Text.Fold(a.Name), a);

        foreach (var item in Catalog.Items)
        {
            string tr = Text.Fold(item.Tr), en = Text.Fold(item.En);
            if (byName.TryGetValue(tr, out var app) || byName.TryGetValue(en, out app))
            {
                app.AddName(item.Tr);
                app.AddName(item.En);
                app.AddKeywords(item.Keywords);
                var folded = Text.Fold(app.Name);
                if (folded == tr || folded == en)
                {
                    app.En ??= item.En;
                    app.Tr ??= item.Tr;
                }
                continue;
            }
            if (item.Kind == EntryKind.App || !Catalog.IsAvailable(item)) continue;

            var cmd = item.Command!.Contains(':') ? item.Command : Catalog.ResolvePath(item.Command) ?? item.Command;
            var entry = new AppEntry(item.En, $"sys:{item.Command} {item.Args}".Trim(), item.Kind)
            {
                Command = cmd,
                Args = item.Args,
                IconPath = Catalog.IconSource(item),
                En = item.En,
                Tr = item.Tr,
            };
            entry.AddName(item.Tr);
            entry.AddKeywords(item.Keywords);
            list.Add(entry);
            byName.TryAdd(Text.Fold(item.En), entry);
            byName.TryAdd(Text.Fold(item.Tr), entry);
        }
    }

    private static int ScoreKey(SearchKey k, string q, string[] qWords)
    {
        if (!k.IsKeyword)
        {
            if (k.Folded == q) return 120;
            if (k.Folded.StartsWith(q)) return 100;
            if (qWords.All(w => k.Words.Any(x => x.StartsWith(w)))) return 80;
            if (q.Length >= 2 && !q.Contains(' ') && k.Initials.StartsWith(q)) return 70;
            if (q.Length >= 2 && k.Folded.Contains(q)) return 45;
            if (Fuzzy(qWords, k.Words)) return 35;
            return 0;
        }
        // Keywords: word-start and typo-tolerant matches only, with a lower score.
        if (qWords.All(w => w.Length >= 2 && k.Words.Any(x => x.StartsWith(w)))) return 50;
        if (Fuzzy(qWords, k.Words)) return 22;
        return 0;
    }

    /// <summary>Whether every query word is the start of a word or matches one within 1–2 typos ("devise manger" → "device manager").</summary>
    private static bool Fuzzy(string[] qWords, string[] words)
    {
        bool anyTypo = false;
        foreach (var w in qWords)
        {
            if (words.Any(x => x.StartsWith(w))) continue;
            if (w.Length < 4) return false;
            int tol = w.Length >= 8 ? 2 : 1;
            if (!words.Any(x => Text.Distance(w, x) <= tol || (x.Length > w.Length && Text.Distance(w, x[..w.Length]) <= tol))) return false;
            anyTypo = true;
        }
        return anyTypo;
    }

    /// <summary>Scored search (in both languages): prefix &gt; word start &gt; initials &gt; contains &gt; typo; frequently opened items rank higher.</summary>
    public static List<AppEntry> Search(string query, int max = 8)
    {
        var q = Text.Fold(query.Trim());
        if (q.Length == 0) return new();
        var qWords = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var scored = new List<(AppEntry app, int score)>();
        foreach (var a in _apps)
        {
            int score = 0;
            foreach (var k in a.Keys)
            {
                int s = ScoreKey(k, q, qWords);
                if (s > 0) score = Math.Max(score, s - k.Penalty);
            }
            if (score <= 0) continue;
            Usage.TryGetValue(a.ParsingName, out var used);
            score += Math.Min(used * 4, 40);
            scored.Add((a, score));
        }
        return StalinSort.Descending(scored, s => s.score).Take(max).Select(s => s.app).ToList();
    }

    public static void Launch(AppEntry app)
    {
        if (app.Command != null)
            Process.Start(new ProcessStartInfo(app.Command, app.Args ?? "") { UseShellExecute = true });
        else
            Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.ParsingName}") { UseShellExecute = true });
        Usage[app.ParsingName] = (Usage.TryGetValue(app.ParsingName, out var n) ? n : 0) + 1;
        SaveUsage();
    }

    private static Dictionary<string, int> LoadUsage()
    {
        try
        {
            if (File.Exists(UsageFile))
                return JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(UsageFile)) ?? new();
        }
        catch { }
        return new();
    }

    private static void SaveUsage()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            File.WriteAllText(UsageFile, JsonSerializer.Serialize(Usage));
        }
        catch { }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace Odak;

/// <summary>Aranabilir bir ad (asıl ad, diğer dildeki adı ya da anahtar kelimeler).</summary>
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

/// <summary>Başlatılabilir bir sonuç: Başlat menüsündeki uygulama, Windows aracı, Ayarlar sayfası ya da klasör.</summary>
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
    /// <summary>Kabuk adı (Store uygulamasında AUMID) ya da sistem kaydı için benzersiz anahtar.</summary>
    public string ParsingName { get; }
    public EntryKind Kind { get; }
    /// <summary>Diğer dildeki adı ("Aygıt Yöneticisi" ↔ "Device Manager").</summary>
    public string? AltName { get; set; }
    public string? Command { get; init; }
    public string? Args { get; init; }
    /// <summary>Simgenin alınacağı kabuk yolu; null ise sembol gösterilir.</summary>
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

    /// <summary>Küçük harf + Türkçe karakter katlama: "Şirket Ağı" → "sirket agi".</summary>
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

    /// <summary>Kısa kelimeler için düzenleme uzaklığı (yer değiştirme dahil): "manger" ↔ "manager" = 1.</summary>
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
/// Uygulama dizini: "Tüm uygulamalar" klasörünü (shell:AppsFolder) okur, iki dilli Windows
/// kataloğuyla birleştirir, arar ve başlatır.
/// </summary>
public static class AppIndex
{
    private static readonly string[] Junk =
        { "uninstall", "kaldir", "readme", "release notes", "documentation", "website", "help", "yardim", "lisans", "license" };

    private static List<AppEntry> _apps = new();
    private static DateTime _loadedAt = DateTime.MinValue;
    private static readonly Dictionary<string, int> Usage = LoadUsage();

    /// <summary>Windows arayüz dili Türkçe mi (sistem kayıtlarının başlık dili buna göre seçilir).</summary>
    public static bool UiTurkish { get; } = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "tr";

    private static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotlight");
    private static string UsageFile => Path.Combine(DataDir, "usage.json");

    public static IReadOnlyList<AppEntry> Apps => _apps;

    /// <summary>Dizini arka planda (kabuk COM'u için STA iş parçacığında) yeniler; 5 dakikada bir.</summary>
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
                    // Önceden yüklenmiş simgeleri koru.
                    var old = _apps.GroupBy(a => a.ParsingName).ToDictionary(g => g.Key, g => g.First().Icon);
                    foreach (var a in list) if (old.TryGetValue(a.ParsingName, out var icon)) a.Icon = icon;
                    _apps = list;
                    _loadedAt = DateTime.Now;
                }
            }
            catch { /* dizin okunamazsa eski liste kalır */ }
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
            // shell:AppsFolder = Başlat menüsündeki "Tüm uygulamalar"
            dynamic folder = shell.NameSpace("shell:::{4234d49b-0245-4df3-b780-3893943456e1}");
            foreach (dynamic item in folder.Items())
            {
                string name = item.Name;
                string path = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) continue;
                var folded = Text.Fold(name);
                if (Junk.Any(j => folded.Contains(j))) continue;
                list.Add(new AppEntry(name, path) { IconPath = "shell:AppsFolder\\" + path });
            }
        }
        list = list.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        MergeCatalog(list);
        return list.OrderBy(a => a.Name, StringComparer.Create(new CultureInfo("tr-TR"), true)).ToList();
    }

    /// <summary>
    /// Katalogdaki her kayıt: Başlat menüsünde aynı adlı uygulama varsa ona diğer dildeki adı ve anahtar
    /// kelimeleri eklenir; yoksa (ve bu bilgisayarda varsa) kendi komutuyla ayrı bir sonuç olur.
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
                var other = folded == tr ? item.En : folded == en ? item.Tr : null;
                if (other != null && Text.Fold(other) != folded) app.AltName ??= other;
                continue;
            }
            if (item.Kind == EntryKind.App || !Catalog.IsAvailable(item)) continue;

            var title = UiTurkish ? item.Tr : item.En;
            var alt = UiTurkish ? item.En : item.Tr;
            var cmd = item.Command!.Contains(':') ? item.Command : Catalog.ResolvePath(item.Command) ?? item.Command;
            var entry = new AppEntry(title, $"sys:{item.Command} {item.Args}".Trim(), item.Kind)
            {
                Command = cmd,
                Args = item.Args,
                IconPath = Catalog.IconSource(item),
                AltName = Text.Fold(alt) != Text.Fold(title) ? alt : null,
            };
            entry.AddName(alt);
            entry.AddKeywords(item.Keywords);
            list.Add(entry);
            byName.TryAdd(Text.Fold(title), entry);
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
        // Anahtar kelimeler: yalnızca kelime başı ve yazım hatası toleransı, daha düşük puan.
        if (qWords.All(w => w.Length >= 2 && k.Words.Any(x => x.StartsWith(w)))) return 50;
        if (Fuzzy(qWords, k.Words)) return 22;
        return 0;
    }

    /// <summary>Her sorgu kelimesi bir kelimenin başı ya da 1–2 harf hatasıyla aynısı mı ("devise manger" → "device manager").</summary>
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

    /// <summary>Puanlı arama (iki dilde): baştan eşleşme &gt; kelime başı &gt; baş harfler &gt; içerir &gt; yazım hatası; sık açılanlar öne.</summary>
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
        return scored.OrderByDescending(s => s.score).ThenBy(s => s.app.Name.Length).Take(max).Select(s => s.app).ToList();
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

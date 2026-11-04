using System;
using System.IO;
using System.Text.Json;

namespace Odak;

/// <summary>
/// Kullanıcı tercihleri ve web araması ayarları.
/// %LOCALAPPDATA%\Spotlight\settings.json dosyasında saklanır.
/// </summary>
public sealed class AppSettings
{
    private static readonly string SettingsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Spotlight"
    );

    private static readonly string SettingsFile = Path.Combine(SettingsDir, "settings.json");

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>Web'de arama önerilerini etkinleştir.</summary>
    public bool EnableWebSearch { get; set; } = true;

    /// <summary>Varsayılan arama motoru: Google, DuckDuckGo, Bing, Yandex, Ecosia.</summary>
    public string SearchEngine { get; set; } = "Google";

    /// <summary>true ise yerel sonuç çıksa bile listenin en altına her zaman web araması eklenir.</summary>
    public bool AlwaysShowWebSearch { get; set; } = true;

    /// <summary>Web araması kartının listenin en üstte gösterilmesi tercihidir.</summary>
    public bool AlwaysShowWebSearchAtTop { get; set; } = true;

    /// <summary>Hotkey string, e.g. "Alt+Space".</summary>
    public string Hotkey { get; set; } = "Alt+Space";

    public static event Action? OnSettingsChanged;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) return loaded;
            }
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(SettingsDir);
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsFile, json);
            Current = this;
            OnSettingsChanged?.Invoke();
        }
        catch { }
    }

    public static string GetSearchUrl(string engine, string query)
    {
        var q = Uri.EscapeDataString(query);
        return (engine ?? "").ToLowerInvariant() switch
        {
            "bing" => $"https://www.bing.com/search?q={q}",
            "duckduckgo" => $"https://duckduckgo.com/?q={q}",
            "yandex" => $"https://yandex.com.tr/search/?text={q}",
            "ecosia" => $"https://www.ecosia.org/search?q={q}",
            _ => $"https://www.google.com/search?q={q}",
        };
    }
}

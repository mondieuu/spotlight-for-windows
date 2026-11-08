using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace Odak;

public enum ThemeMode { System, Light, Dark }

public enum SearchEngine { Google, Bing, DuckDuckGo }

/// <summary>
/// User preferences, stored in %LOCALAPPDATA%\Spotlight\settings.json.
/// English is the default language; Turkish is fully supported.
/// </summary>
public sealed class AppSettings
{
    public ThemeMode Theme { get; set; } = ThemeMode.System;
    /// <summary>"en" (default) or "tr".</summary>
    public string Language { get; set; } = "en";
    public SearchEngine WebSearch { get; set; } = SearchEngine.Google;
    /// <summary>Global shortcut: modifier flags (Alt = 1, Ctrl = 2, Shift = 4, Win = 8) and virtual-key code.</summary>
    public uint HotkeyModifiers { get; set; } = 0x1;
    public uint HotkeyKey { get; set; } = 0x20;
    /// <summary>A tap of the Windows key opens Spotlight instead of the Start menu.</summary>
    public bool UseWindowsKey { get; set; }
    /// <summary>Check GitHub for a new version automatically (shortly after start, then every 6 hours).</summary>
    public bool AutoCheckUpdates { get; set; } = true;

    public static AppSettings Current { get; private set; } = Load();

    /// <summary>Raised after any setting changes so open windows can update live.</summary>
    public static event Action? Changed;

    private static string File_ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Spotlight", "settings.json");

    public bool IsTurkish => Language == "tr";

    /// <summary>Resolves "System" to the Windows app theme.</summary>
    public bool IsDark
    {
        get
        {
            if (Theme == ThemeMode.Dark) return true;
            if (Theme == ThemeMode.Light) return false;
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int v || v == 0;
        }
    }

    public string WebSearchUrl(string query)
    {
        var q = Uri.EscapeDataString(query);
        return WebSearch switch
        {
            SearchEngine.Bing => $"https://www.bing.com/search?q={q}",
            SearchEngine.DuckDuckGo => $"https://duckduckgo.com/?q={q}",
            _ => $"https://www.google.com/search?q={q}",
        };
    }

    public string WebSearchName => WebSearch switch
    {
        SearchEngine.Bing => "Bing",
        SearchEngine.DuckDuckGo => "DuckDuckGo",
        _ => "Google",
    };

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
        Changed?.Invoke();
    }

    private static AppSettings Load()
    {
        try
        {
            if (File.Exists(File_))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(File_)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }
}

/// <summary>Localization: <c>L.T("English", "Türkçe")</c> picks the text for the current language.</summary>
public static class L
{
    public static string T(string en, string tr) => AppSettings.Current.IsTurkish ? tr : en;
}

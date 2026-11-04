using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace Odak;

public partial class SearchWindow : Window
{
    private const int HotkeyId = 0x0D4A;
    private readonly ObservableCollection<ResultItem> _items = new();
    private CancellationTokenSource? _fileSearch;
    private Backdrop? _backdrop;
    private ResultItem? _webRow;
    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Shows a tray notification when a download finishes (wired up by App).</summary>
    public Action<string, string, string?>? Notify { get; set; }

    /// <summary>The registered shortcut: "Alt + Space", or "Ctrl + Space" as a fallback.</summary>
    public string HotkeyText { get; private set; } = "Alt + Space";

    // Spring animations (Apple style: response + damping). All critically damped: no bounce, a calm settle.
    private readonly Spring _open;    // 0 = closed, 1 = open (opacity + scale)
    private readonly Spring _height;  // height of the glass panel
    private bool _closing;

    /// <summary>When Windows "Animation effects" is off, motion is replaced by a short fade.</summary>
    private static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;

    public SearchWindow()
    {
        InitializeComponent();
        Results.ItemsSource = _items;
        _open = new Spring(0, ApplyOpen);
        _height = new Spring(68, h => Panel.Height = Math.Max(68, h));
        // Like macOS: clicking anywhere else closes it.
        Deactivated += (_, _) => HideSpotlight();
        // Theme, language and live-glass changes from the Settings window apply live.
        AppSettings.Changed += () => Dispatcher.Invoke(() =>
        {
            ApplyAppearance();
            if (!IsVisible || _closing) return;
            OnQueryChanged(this, null!);
            if (AppSettings.Current.LiveGlass) StartLiveGlass();
            else _liveGlass?.Cancel();
        });
        ApplyAppearance();
    }

    /// <summary>Open progress: the glass grows slightly from the top centre as it appears and leaves along the same path.</summary>
    private void ApplyOpen(double p)
    {
        Panel.Opacity = Math.Clamp(p, 0, 1);
        double s = ReduceMotion ? 1 : 0.94 + 0.06 * p;
        PanelScale.ScaleX = PanelScale.ScaleY = s;
    }

    // Where the glass sits inside the window (the XAML margins leave room for the shadow).
    private const double GlassLeft = 40, GlassTop = 24, GlassWidth = 720, GlassMaxHeight = 640, GlassRadius = 28;

    /// <summary>Creates the window without showing it, registers the hotkey and preloads the app and file indexes.</summary>
    public void InitializeHidden()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        var s = AppSettings.Current;
        if (Native.RegisterHotKey(hwnd, HotkeyId, s.HotkeyModifiers | Native.MOD_NOREPEAT, s.HotkeyKey))
        {
            HotkeyText = FormatHotkey(s.HotkeyModifiers, s.HotkeyKey);
        }
        else if (Native.RegisterHotKey(hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_NOREPEAT, Native.VK_SPACE))
        {
            // The chosen shortcut is taken by another app (e.g. PowerToys Run): fall back to Ctrl+Space.
            HotkeyText = FormatHotkey(Native.MOD_CONTROL, Native.VK_SPACE);
        }
        _ = AppIndex.RefreshAsync(force: true);
        FileIndex.Start();
    }

    public void UnregisterHotkey() => Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);

    /// <summary>Raised after the keyboard shortcut changes (the tray updates its text and menu).</summary>
    public event Action? HotkeyChanged;

    /// <summary>
    /// Switches to a new global shortcut. Returns false (and keeps the old one) if another app already uses it.
    /// </summary>
    public bool TrySetHotkey(uint modifiers, uint key)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.UnregisterHotKey(hwnd, HotkeyId);
        if (!Native.RegisterHotKey(hwnd, HotkeyId, modifiers | Native.MOD_NOREPEAT, key))
        {
            var s0 = AppSettings.Current;
            Native.RegisterHotKey(hwnd, HotkeyId, s0.HotkeyModifiers | Native.MOD_NOREPEAT, s0.HotkeyKey);
            return false;
        }
        var s = AppSettings.Current;
        s.HotkeyModifiers = modifiers;
        s.HotkeyKey = key;
        HotkeyText = FormatHotkey(modifiers, key);
        s.Save();
        HotkeyChanged?.Invoke();
        return true;
    }

    /// <summary>Frees the shortcut while the Settings window records a new one.</summary>
    public void SuspendHotkey() => Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);

    public void ResumeHotkey()
    {
        var s = AppSettings.Current;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!Native.RegisterHotKey(hwnd, HotkeyId, s.HotkeyModifiers | Native.MOD_NOREPEAT, s.HotkeyKey))
            Native.RegisterHotKey(hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_NOREPEAT, Native.VK_SPACE);
    }

    /// <summary>"Alt + Space", "Ctrl + Shift + K"…</summary>
    public static string FormatHotkey(uint modifiers, uint key)
    {
        var parts = new List<string>();
        if ((modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
        var k = KeyInterop.KeyFromVirtualKey((int)key);
        parts.Add(k switch
        {
            Key.Space => "Space",
            Key.OemPeriod => ".",
            Key.OemComma => ",",
            Key.OemQuestion => "/",
            Key.Oem1 => ";",
            Key.OemQuotes => "'",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            >= Key.D0 and <= Key.D9 => ((char)('0' + (k - Key.D0))).ToString(),
            _ => k.ToString(),
        });
        return string.Join(" + ", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_HOTKEY && wParam.ToInt32() == HotkeyId)
        {
            Toggle();
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Toggle()
    {
        if (IsVisible && !_closing) HideSpotlight();
        else ShowSpotlight();
    }

    public void ShowSpotlight()
    {
        if (IsVisible)
        {
            // Called again while closing: reverse from the current value (continuous, no new capture).
            _closing = false;
            _open.To(1, ReduceMotion ? 0.2 : 0.32, 1.0);
            Activate();
            Query.Focus();
            StartLiveGlass();
            return;
        }
        ApplyAppearance();
        // Like macOS: centred, about a fifth of the way down the screen.
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + area.Height * 0.2 - GlassTop;

        // Capture the screen behind the window before it appears; the glass is built from it and then kept live.
        // The window is excluded from capture first: otherwise a lingering frame of its last showing could be
        // captured, and the tint would stack up on every reopen (darker each time in dark mode, brighter in light).
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _captureRect = new System.Drawing.Rectangle(
            (int)Math.Round((Left + GlassLeft) * scale), (int)Math.Round((Top + GlassTop) * scale),
            (int)Math.Round(GlassWidth * scale), (int)Math.Round(GlassMaxHeight * scale));
        _captureScale = scale;
        _backdrop = CaptureBehind(new WindowInteropHelper(this).Handle);
        _height.Snap(ResultsContent.ActualHeight > 0 ? ResultsContent.ActualHeight : 68);
        UpdateGlass();
        _closing = false;
        _open.Snap(0);
        Show();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Query.Focus();
        Query.SelectAll();
        // Accepts typing from the first frame; the animation never makes you wait.
        _open.To(1, ReduceMotion ? 0.2 : 0.32, 1.0);
        StartLiveGlass();
        _ = AppIndex.RefreshAsync();
    }

    public void HideSpotlight()
    {
        if (!IsVisible || _closing) return;
        _fileSearch?.Cancel();
        _liveGlass?.Cancel();
        _closing = true;
        // Leaves along the path it came in on (same scale axis), a little faster.
        _open.To(0, ReduceMotion ? 0.15 : 0.22, 1.0, settled: () =>
        {
            if (!_closing) return;
            _closing = false;
            Hide();
            // Start clean next time; running downloads continue in the background.
            if (!_items.Any(i => i.IsBusy)) Query.Text = "";
        });
    }

    // ---- Panel height (springs as the content changes)

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double target = e.NewSize.Height;
        if (!IsVisible || ReduceMotion) _height.Snap(target);
        else _height.To(target, 0.35, 1.0);
    }

    private void OnContentHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        ContentHost.Clip = new RectangleGeometry(new Rect(e.NewSize), GlassRadius, GlassRadius);

    // ---- Press feedback (instant on mouse down, springs back on release)

    private System.Windows.Controls.ListBoxItem? _pressed;
    private readonly Dictionary<System.Windows.Controls.ListBoxItem, Spring> _pressSprings = new();

    private void OnResultPressed(object sender, MouseButtonEventArgs e)
    {
        if (Results.ContainerFromElement((DependencyObject)e.OriginalSource) is not System.Windows.Controls.ListBoxItem { DataContext: ResultItem { IsHeader: false } } container) return;
        _pressed = container;
        PressSpring(container).To(ReduceMotion ? 1 : 0.975, 0.12, 1.0);
    }

    private void OnResultReleased(object sender, MouseEventArgs e)
    {
        if (_pressed == null) return;
        PressSpring(_pressed).To(1, 0.3, 1.0);
        _pressed = null;
    }

    private Spring PressSpring(System.Windows.Controls.ListBoxItem item)
    {
        if (_pressSprings.TryGetValue(item, out var spring)) return spring;
        var st = new ScaleTransform(1, 1);
        item.RenderTransformOrigin = new Point(0.5, 0.5);
        item.RenderTransform = st;
        spring = new Spring(1, v => st.ScaleX = st.ScaleY = v);
        _pressSprings[item] = spring;
        return spring;
    }

    // ---- Glass

    private System.Drawing.Rectangle _captureRect;
    private double _captureScale = 1;
    private CancellationTokenSource? _liveGlass;
    private volatile float _glassHeight = 68;

    private void OnGlassSizeChanged(object sender, SizeChangedEventArgs e)
    {
        _glassHeight = (float)e.NewSize.Height;
        UpdateGlass();
    }

    /// <summary>Edge refraction is computed for the real size of the glass and refreshed as the result list grows.</summary>
    private void UpdateGlass()
    {
        if (_backdrop == null) return;
        double h = Glass.ActualHeight > 0 ? Glass.ActualHeight : 68;
        SetGlass(_backdrop.Render(GlassWidth, h, GlassRadius));
    }

    private void SetGlass(ImageSource image) =>
        Glass.Background = new ImageBrush(image)
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };

    /// <summary>Sets display affinity from any thread (falls back to the UI thread if Windows requires it).</summary>
    private void Affinity(IntPtr hwnd, uint value)
    {
        if (!Native.SetWindowDisplayAffinity(hwnd, value))
            Dispatcher.Invoke(() => Native.SetWindowDisplayAffinity(hwnd, value));
    }

    /// <summary>
    /// Captures only what is behind the glass, never the glass itself: the window is excluded from screen
    /// capture, two compositor frames pass so the exclusion is surely applied, then the region is copied.
    /// The window stays fully visible on screen the whole time.
    /// </summary>
    private Backdrop? CaptureBehind(IntPtr hwnd)
    {
        var rect = _captureRect;
        try
        {
            Affinity(hwnd, Native.WDA_EXCLUDEFROMCAPTURE);
            Native.DwmFlush();
            Native.DwmFlush();
            return Backdrop.Capture(rect.X, rect.Y, rect.Width, rect.Height, _captureScale);
        }
        finally
        {
            Affinity(hwnd, Native.WDA_NONE);
        }
    }

    /// <summary>
    /// Keeps the glass live (twice a second) so it always shows what is really behind it: videos,
    /// scrolling pages, windows that move. The refracted image is built off the UI thread so typing never stutters.
    /// </summary>
    private void StartLiveGlass()
    {
        _liveGlass?.Cancel();
        // Off in Settings (e.g. while screen sharing): the glass keeps the snapshot taken when it opened.
        if (!AppSettings.Current.LiveGlass) return;
        var cts = _liveGlass = new CancellationTokenSource();
        var hwnd = new WindowInteropHelper(this).Handle;
        Task.Run(async () =>
        {
            while (!cts.IsCancellationRequested)
            {
                try { await Task.Delay(500, cts.Token); } catch (OperationCanceledException) { return; }
                if (_closing) continue;
                var fresh = CaptureBehind(hwnd);
                if (fresh == null || cts.IsCancellationRequested) continue;
                var image = fresh.Render(GlassWidth, _glassHeight, GlassRadius);
                await Dispatcher.InvokeAsync(() =>
                {
                    if (cts.IsCancellationRequested) return;
                    _backdrop = fresh;
                    SetGlass(image);
                });
            }
        });
    }

    // ---- Appearance (Light / Dark / follow Windows) and language

    private void ApplyAppearance()
    {
        bool dark = AppSettings.Current.IsDark;
        Resources["Fg"] = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        Resources["FgDim"] = new SolidColorBrush(dark ? Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x3C, 0x3C, 0x43));
        // Clear Liquid Glass tint: the background shows through clearly, with just enough light/dark for legible text.
        Resources["Tint"] = new SolidColorBrush(dark ? Color.FromArgb(0x48, 0x1C, 0x1C, 0x1E) : Color.FromArgb(0x5C, 0xF7, 0xF7, 0xFA));
        Resources["Hairline"] = new SolidColorBrush(dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        Resources["Hover"] = new SolidColorBrush(dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        var placeholder = L.T("Spotlight Search", "Spotlight Araması");
        Placeholder.Text = placeholder;
        System.Windows.Automation.AutomationProperties.SetName(Query, placeholder);
    }

    // ---- Search

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _fileSearch?.Cancel();
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var text = Query.Text.Trim();
        // Running downloads stay in the list.
        var busy = _items.Where(i => i.IsBusy).ToList();
        _items.Clear();
        _webRow = null;
        foreach (var b in busy) _items.Add(b);
        var built = BuildResults(text, out bool searchFiles);
        foreach (var item in built) _items.Add(item);
        RefreshList(selectFirst: true);
        if (searchFiles) SearchFilesAsync(text);
    }

    private void RefreshList(bool selectFirst)
    {
        var visible = _items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        Results.Visibility = visible;
        Divider.Visibility = visible;
        if (selectFirst || Results.SelectedItem is not ResultItem { IsHeader: false })
        {
            var first = _items.FirstOrDefault(i => !i.IsHeader && !i.IsBusy) ?? _items.FirstOrDefault(i => !i.IsHeader);
            Results.SelectedItem = first;
        }
        LoadIconsAsync();
    }

    private List<ResultItem> BuildResults(string text, out bool searchFiles)
    {
        searchFiles = false;
        var list = new List<ResultItem>();
        if (text.Length == 0) return list;

        // 1) Link: YouTube / Instagram → MP4 / MP3
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            if (Converter.IsSupported(uri))
            {
                var site = uri.Host.Contains("instagram") ? "Instagram" : "YouTube";
                list.Add(DownloadItem(uri.ToString(), audio: false, site));
                list.Add(DownloadItem(uri.ToString(), audio: true, site));
            }
            list.Add(new ResultItem
            {
                Title = L.T("Open in browser", "Tarayıcıda aç"),
                Subtitle = uri.Host,
                Glyph = "",
                GlyphBackground = Solid(0x8E, 0x8E, 0x93),
                Run = _ => { Open(uri.ToString()); return Task.FromResult(false); },
            });
            return list;
        }

        // 2) Disk path: file → select it in File Explorer, folder → open it
        var path = AsPath(text);
        if (path != null)
        {
            list.Add(PathItem(path));
            return list;
        }

        // 3) Apps and Windows tools/settings in two sections; the section with the best match comes first.
        //    Files are added afterwards in the background.
        var found = AppIndex.Search(text, 8);
        var sections = found
            .GroupBy(a => a.Kind == EntryKind.App)
            .Select(g => (apps: g.Key, items: g.Take(5).ToList()));
        foreach (var (isApp, items) in sections)
        {
            list.Add(Header(isApp ? L.T("Applications", "Uygulamalar") : L.T("System & Settings", "Sistem ve Ayarlar")));
            foreach (var app in items) list.Add(AppItem(app));
        }

        // Spotlight's own settings: "spotlight settings", "theme", "dark mode", "language", "tema", "dil"…
        var q = Text.Fold(text);
        string[] settingsWords = { "settings", "ayarlar", "spotlight settings", "spotlight ayarlari", "preferences", "tercihler", "theme", "tema", "dark mode", "light mode", "karanlik", "language", "dil " };
        if (q.Length >= 3 && settingsWords.Any(w => w.StartsWith(q) || q == w.Trim()))
        {
            list.Add(Header("Spotlight"));
            list.Add(new ResultItem
            {
                Title = L.T("Spotlight Settings", "Spotlight Ayarları"),
                Subtitle = L.T("Theme, language and web search", "Tema, dil ve web araması"),
                Glyph = "",
                GlyphBackground = Solid(0x5E, 0x5C, 0xE6),
                Run = _ => { SettingsWindow.Open(this); return Task.FromResult(false); },
            });
        }

        // 4) Always last: search the web in the default browser. When nothing else matches, it is the top hit.
        _webRow = WebItem(text);
        list.Add(_webRow);

        // Priority: apps → web search → files. Plain words ("youtube") stay apps + web only; files are
        // listed (below the web row) when the query looks like a file name, i.e. it has an extension: "chrome.exe".
        // SPOTLIGHT_DEMO=1: keep personal file paths out of promotional screenshots.
        searchFiles = text.Length >= 2 && LooksLikeFileName(text) && Environment.GetEnvironmentVariable("SPOTLIGHT_DEMO") != "1";
        return list;
    }

    /// <summary>"report.xlsx", "chrome.exe", ".pdf" → true; "youtube", "device manager" → false.</summary>
    private static bool LooksLikeFileName(string text)
    {
        int dot = text.LastIndexOf('.');
        return dot >= 0 && dot < text.Length - 1 && !text.Contains(' ', StringComparison.Ordinal) || text.Contains('\\');
    }

    private ResultItem WebItem(string text)
    {
        var s = AppSettings.Current;
        return new ResultItem
        {
            Title = L.T($"Search the web for \"{text}\"", $"\"{text}\" için webde ara"),
            Subtitle = L.T($"{s.WebSearchName} · opens in your default browser", $"{s.WebSearchName} · varsayılan tarayıcında açılır"),
            Glyph = "",
            GlyphBackground = Solid(0x0A, 0x84, 0xFF),
            Run = _ => { Open(s.WebSearchUrl(text)); return Task.FromResult(false); },
        };
    }

    /// <summary>Searches files and folders in the background; a short delay skips needless searches while typing.</summary>
    private async void SearchFilesAsync(string text)
    {
        var cts = _fileSearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(60, cts.Token);
            var hits = await Task.Run(() => FileIndex.Search(text, 8, cts.Token), cts.Token);
            if (cts.IsCancellationRequested) return;

            var rows = new List<ResultItem>();
            if (hits.Count > 0)
            {
                rows.Add(Header(L.T("Files & Folders", "Dosyalar ve Klasörler")));
                rows.AddRange(hits.Select(FileItem));
            }
            else if (!FileIndex.IsReady)
            {
                rows.Add(Header(FileIndex.IsScanning
                    ? L.T("Indexing files… (the first scan takes a few minutes)", "Dosyalar dizinleniyor… (ilk tarama birkaç dakika sürer)")
                    : L.T("Preparing the file index…", "Dosya dizini hazırlanıyor…")));
            }
            if (rows.Count == 0) return;

            // Files come last, below the web search row, and never take the selection away from it.
            foreach (var row in rows) _items.Add(row);
            RefreshList(selectFirst: false);
        }
        catch (OperationCanceledException) { }
        catch { }
    }

    private static ResultItem Header(string title) => new() { Title = title, IsHeader = true };

    private ResultItem AppItem(AppEntry app)
    {
        var kind = app.Kind switch
        {
            EntryKind.Tool => L.T("Windows tool", "Windows aracı"),
            EntryKind.Setting => L.T("Settings", "Ayarlar"),
            EntryKind.Folder => L.T("Folder", "Klasör"),
            _ => L.T("Application", "Uygulama"),
        };
        var alt = app.AltDisplayName;
        return new ResultItem
        {
            Title = app.DisplayName,
            // Also show the name in the other language: "Windows tool · Aygıt Yöneticisi"
            Subtitle = alt != null ? $"{kind} · {alt}" : kind,
            App = app,
            Icon = app.Icon,
            Glyph = app.Kind switch { EntryKind.Setting => "", EntryKind.Folder => "", _ => "" },
            GlyphBackground = app.Kind == EntryKind.Folder ? Solid(0x0A, 0x84, 0xFF) : Solid(0x8E, 0x8E, 0x93),
            Run = _ => { AppIndex.Launch(app); return Task.FromResult(false); },
        };
    }

    private ResultItem FileItem(FileHit hit)
    {
        var path = hit.Path;
        var name = Path.GetFileName(path.TrimEnd('\\'));
        var folder = Path.GetDirectoryName(path) ?? path;
        return new ResultItem
        {
            Title = name.Length > 0 ? name : path,
            // Like macOS: where it lives, under its name.
            Subtitle = folder,
            FilePath = path,
            Hint = L.T("Ctrl+Enter · Show in folder", "Ctrl+Enter · Konumu göster"),
            // Grey document/folder symbol until the icon loads (or if it cannot).
            Glyph = hit.IsDirectory ? "" : "",
            GlyphBackground = Solid(0x8E, 0x8E, 0x93),
            Icon = IconCache.TryGetValue(path, out var cached) ? cached : null,
            Run = _ =>
            {
                if (hit.IsDirectory) Explorer($"\"{path}\"");
                else if (File.Exists(path))
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, WorkingDirectory = folder });
                else Explorer($"\"{FirstExisting(folder) ?? folder}\"");
                return Task.FromResult(false);
            },
        };
    }

    private static string? FirstExisting(string? dir)
    {
        while (dir != null && !Directory.Exists(dir)) dir = Path.GetDirectoryName(dir);
        return dir;
    }

    /// <summary>If the text looks like a disk path, returns it normalised.</summary>
    private static string? AsPath(string text)
    {
        var t = text.Trim().Trim('"', '\'');
        if (t.StartsWith("file:///", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(t, UriKind.Absolute, out var fileUri)) t = fileUri.LocalPath;
        t = Environment.ExpandEnvironmentVariables(t);
        if (t.StartsWith("~")) t = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + t[1..];
        bool looksLikePath = Regex.IsMatch(t, @"^[A-Za-z]:([\\/]|$)") || t.StartsWith(@"\\");
        return looksLikePath ? t.Replace('/', '\\') : null;
    }

    private ResultItem PathItem(string path)
    {
        if (File.Exists(path))
        {
            return new ResultItem
            {
                Title = Path.GetFileName(path),
                Subtitle = L.T("Show in File Explorer · ", "Dosya Gezgini'nde göster · ") + Path.GetDirectoryName(path),
                Icon = Native.ShellIcon(path, 64),
                Run = _ => { Explorer($"/select,\"{path}\""); return Task.FromResult(false); },
            };
        }
        if (Directory.Exists(path))
        {
            return new ResultItem
            {
                Title = new DirectoryInfo(path).Name is { Length: > 0 } n ? n : path,
                Subtitle = L.T("Open in File Explorer · ", "Dosya Gezgini'nde aç · ") + path,
                Icon = Native.ShellIcon(path, 64),
                Run = _ => { Explorer($"\"{path}\""); return Task.FromResult(false); },
            };
        }
        // Not found: open the nearest folder that exists, if any.
        var parent = FirstExisting(Path.GetDirectoryName(path));
        return new ResultItem
        {
            Title = L.T("Not found", "Bulunamadı"),
            Subtitle = parent != null ? L.T($"Open the nearest existing folder · {parent}", $"Var olan en yakın klasörü aç · {parent}") : path,
            Glyph = "",
            GlyphBackground = Solid(0xFF, 0x45, 0x3A),
            Run = parent == null ? null : _ => { Explorer($"\"{parent}\""); return Task.FromResult(false); },
        };
    }

    private ResultItem DownloadItem(string url, bool audio, string site) => new()
    {
        Title = audio ? L.T("Download as MP3", "MP3 olarak indir") : L.T("Download as MP4", "MP4 olarak indir"),
        Subtitle = $"{site} · {(audio ? L.T("audio only", "yalnızca ses") : "video")} · {L.T("Downloads", "İndirilenler")}\\Spotlight",
        Glyph = audio ? "" : "",
        GlyphBackground = audio ? Solid(0xFF, 0x2D, 0x55) : Solid(0xFF, 0x9F, 0x0A),
        Run = row => StartDownload(row, url, audio),
    };

    private async Task<bool> StartDownload(ResultItem row, string url, bool audio)
    {
        if (row.IsBusy) return true;
        row.IsBusy = true;
        row.Progress = 0;
        var progress = new Progress<(double fraction, string status)>(p =>
        {
            row.Progress = p.fraction;
            row.Subtitle = p.status;
        });
        try
        {
            var file = await Converter.DownloadAsync(url, audio, progress);
            row.Progress = -1;
            row.Title = L.T("Downloaded: ", "İndirildi: ") + Path.GetFileName(file);
            row.Subtitle = L.T("Press Enter to show it in File Explorer", "Dosya Gezgini'nde göstermek için Enter");
            row.IsBusy = false;
            Notify?.Invoke(L.T("Download complete", "İndirme tamamlandı"), Path.GetFileName(file), file);
            ReplaceRun(row, file);
        }
        catch (Exception ex)
        {
            row.Progress = -1;
            row.IsBusy = false;
            row.Title = L.T("Download failed", "İndirilemedi");
            row.Subtitle = ex.Message;
            Notify?.Invoke(L.T("Download failed", "İndirilemedi"), ex.Message, null);
        }
        return true;
    }

    /// <summary>A finished download row: Enter → select the file in File Explorer.</summary>
    private void ReplaceRun(ResultItem row, string file)
    {
        var index = _items.IndexOf(row);
        if (index < 0) return;
        _items[index] = new ResultItem
        {
            Title = row.Title,
            Subtitle = row.Subtitle,
            Glyph = "",
            GlyphBackground = Solid(0x30, 0xD1, 0x58),
            FilePath = file,
            Run = _ => { Explorer(File.Exists(file) ? $"/select,\"{file}\"" : $"\"{file}\""); return Task.FromResult(false); },
        };
        Results.SelectedIndex = index;
    }

    /// <summary>Loads icons for the visible rows in the background (and caches them).</summary>
    private void LoadIconsAsync()
    {
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        foreach (var row in _items.Where(i => i.Icon == null && !i.IsHeader).ToList())
        {
            if (row.App is { } app)
            {
                if (app.Icon != null) { row.Icon = app.Icon; continue; }
                if (app.IconPath == null) continue; // no icon: the coloured symbol stays
                Native.ShellIconAsync(app.IconPath, 64).ContinueWith(t =>
                {
                    if (t.Result == null) return;
                    app.Icon = t.Result;
                    row.Icon = t.Result;
                }, ui);
            }
            else if (row.FilePath is { } path)
            {
                if (IconCache.TryGetValue(path, out var cached)) { if (cached != null) row.Icon = cached; continue; }
                Native.ShellIconAsync(path, 64).ContinueWith(t =>
                {
                    IconCache[path] = t.Result;
                    if (t.Result != null) row.Icon = t.Result;
                }, ui);
            }
        }
    }

    // ---- Keyboard and mouse

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                HideSpotlight();
                e.Handled = true;
                break;
            case Key.Down:
                Move(+1);
                e.Handled = true;
                break;
            case Key.Up:
                Move(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0 && Results.SelectedItem is ResultItem { FilePath: { } p })
                {
                    // Ctrl+Enter: select the file in File Explorer (⌘↵ on macOS).
                    Explorer(File.Exists(p) || Directory.Exists(p) ? $"/select,\"{p}\"" : $"\"{FirstExisting(Path.GetDirectoryName(p))}\"");
                    HideSpotlight();
                }
                else _ = RunSelected();
                e.Handled = true;
                break;
        }
    }

    private void Move(int delta)
    {
        if (_items.Count == 0) return;
        int i = Results.SelectedIndex;
        do { i += delta; } while (i >= 0 && i < _items.Count && _items[i].IsHeader);
        if (i < 0 || i >= _items.Count) return;
        Results.SelectedIndex = i;
        Results.ScrollIntoView(Results.SelectedItem);
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e) => _ = RunSelected();

    private async Task RunSelected()
    {
        if (Results.SelectedItem is not ResultItem item || item.IsHeader || item.Run == null) return;
        try
        {
            bool keepOpen = await item.Run(item);
            if (!keepOpen) HideSpotlight();
        }
        catch (Exception ex)
        {
            item.Subtitle = ex.Message;
        }
    }

    // ---- Helpers

    private static SolidColorBrush Solid(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static void Explorer(string args) =>
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });

    private static void Open(string target) =>
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
}

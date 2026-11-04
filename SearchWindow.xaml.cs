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
using Microsoft.Win32;

namespace Odak;

public partial class SearchWindow : Window
{
    private const int HotkeyId = 0x0D4A;
    private readonly ObservableCollection<ResultItem> _items = new();
    private CancellationTokenSource? _fileSearch;
    private Backdrop? _backdrop;
    private static readonly ConcurrentDictionary<string, ImageSource?> IconCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>İndirme bittiğinde tepsi bildirimi göstermek için (App bağlar).</summary>
    public Action<string, string, string?>? Notify { get; set; }

    /// <summary>Kayıtlı kısayol: "Alt + Boşluk" ya da yedek "Ctrl + Boşluk".</summary>
    public string HotkeyText { get; private set; } = "Alt + Boşluk";

    // Yay animasyonları (Apple: tepki + sönüm). Hepsi kritik sönümlü: sıçrama yok, sakin yerleşme.
    private readonly Spring _open;    // 0 = kapalı, 1 = açık (saydamlık + ölçek)
    private readonly Spring _height;  // cam panelinin yüksekliği
    private bool _closing;

    /// <summary>Windows "Animasyon efektleri" kapalıysa hareket yerine kısa bir saydamlık geçişi kullanılır.</summary>
    private static bool ReduceMotion => !SystemParameters.ClientAreaAnimation;

    public SearchWindow()
    {
        InitializeComponent();
        Results.ItemsSource = _items;
        _open = new Spring(0, ApplyOpen);
        _height = new Spring(68, h => Panel.Height = Math.Max(68, h));
        // Başka bir yere tıklanınca macOS'taki gibi kapanır.
        Deactivated += (_, _) => HideSpotlight();
    }

    /// <summary>Açılış ilerlemesi: cam üst-ortadan hafifçe büyüyerek "belirir", aynı yoldan geri çekilir.</summary>
    private void ApplyOpen(double p)
    {
        Panel.Opacity = Math.Clamp(p, 0, 1);
        double s = ReduceMotion ? 1 : 0.94 + 0.06 * p;
        PanelScale.ScaleX = PanelScale.ScaleY = s;
    }

    // Camın pencere içindeki yeri (XAML'deki kenar boşlukları; gölgeye yer açar).
    private const double GlassLeft = 40, GlassTop = 24, GlassWidth = 720, GlassMaxHeight = 640, GlassRadius = 28;

    /// <summary>Pencereyi göstermeden oluşturur, kısayolu kaydeder, uygulama ve dosya dizinlerini önceden yükler.</summary>
    public void InitializeHidden()
    {
        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        if (!Native.RegisterHotKey(hwnd, HotkeyId, Native.MOD_ALT | Native.MOD_NOREPEAT, Native.VK_SPACE))
        {
            // Alt+Boşluk başka bir uygulamada (ör. PowerToys Run) kayıtlıysa Ctrl+Boşluk.
            Native.RegisterHotKey(hwnd, HotkeyId, Native.MOD_CONTROL | Native.MOD_NOREPEAT, Native.VK_SPACE);
            HotkeyText = "Ctrl + Boşluk";
        }
        _ = AppIndex.RefreshAsync(force: true);
        FileIndex.Start();
    }

    public void UnregisterHotkey() => Native.UnregisterHotKey(new WindowInteropHelper(this).Handle, HotkeyId);

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
            // Kapanırken yeniden çağrıldı: anlık değerden geri dön (kesintisiz, yeniden yakalama yok).
            _closing = false;
            _open.To(1, ReduceMotion ? 0.2 : 0.32, 1.0);
            Activate();
            Query.Focus();
            return;
        }
        ApplyTheme();
        // macOS'taki gibi: ekranın ortasında, üstten yaklaşık beşte bir aşağıda.
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + area.Height * 0.2 - GlassTop;

        // Pencere görünmeden arkasındaki ekranı yakala; cam zemini (bulanık + kenar kırılması) bundan üretilir.
        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        _backdrop = Backdrop.Capture(
            (int)Math.Round((Left + GlassLeft) * scale), (int)Math.Round((Top + GlassTop) * scale),
            (int)Math.Round(GlassWidth * scale), (int)Math.Round(GlassMaxHeight * scale), scale);
        _height.Snap(ResultsContent.ActualHeight > 0 ? ResultsContent.ActualHeight : 68);
        UpdateGlass();
        _closing = false;
        _open.Snap(0);
        Show();
        Activate();
        Native.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        Query.Focus();
        Query.SelectAll();
        // İlk kareden itibaren girişe açık; animasyon yazmayı hiç bekletmez.
        _open.To(1, ReduceMotion ? 0.2 : 0.32, 1.0);
        _ = AppIndex.RefreshAsync();
    }

    public void HideSpotlight()
    {
        if (!IsVisible || _closing) return;
        _fileSearch?.Cancel();
        _closing = true;
        // Geldiği yoldan geri çekilir (aynı ölçek ekseni), biraz daha hızlı.
        _open.To(0, ReduceMotion ? 0.15 : 0.22, 1.0, settled: () =>
        {
            if (!_closing) return;
            _closing = false;
            Hide();
            // Bir sonraki açılışta temiz başlasın; süren indirmeler arka planda devam eder.
            if (!_items.Any(i => i.IsBusy)) Query.Text = "";
        });
    }

    // ---- Panel yüksekliği (içerik değiştikçe yaylanır)

    private void OnContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        double target = e.NewSize.Height;
        if (!IsVisible || ReduceMotion) _height.Snap(target);
        else _height.To(target, 0.35, 1.0);
    }

    private void OnContentHostSizeChanged(object sender, SizeChangedEventArgs e) =>
        ContentHost.Clip = new RectangleGeometry(new Rect(e.NewSize), GlassRadius, GlassRadius);

    // ---- Basma geri bildirimi (fare basılır basılmaz, bırakınca yaylanarak geri)

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

    // ---- Cam

    private void OnGlassSizeChanged(object sender, SizeChangedEventArgs e) => UpdateGlass();

    /// <summary>Kenar kırılması camın gerçek boyutuna göre hesaplanır; sonuç listesi büyüdükçe yenilenir.</summary>
    private void UpdateGlass()
    {
        if (_backdrop == null) return;
        double h = Glass.ActualHeight > 0 ? Glass.ActualHeight : 68;
        Glass.Background = new ImageBrush(_backdrop.Render(GlassWidth, h, GlassRadius))
        {
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
    }

    // ---- Tema (Windows açık/koyu moduna uyar)

    private static bool IsDarkTheme()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    private void ApplyTheme()
    {
        bool dark = IsDarkTheme();
        Resources["Fg"] = new SolidColorBrush(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        Resources["FgDim"] = new SolidColorBrush(dark ? Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x3C, 0x3C, 0x43));
        // Liquid Glass tonu: arkası belirgin görünecek kadar ince; yazı okunabilirliği için hafif koyu/açık.
        Resources["Tint"] = new SolidColorBrush(dark ? Color.FromArgb(0x7A, 0x1C, 0x1C, 0x1E) : Color.FromArgb(0x8C, 0xF7, 0xF7, 0xFA));
        Resources["Hairline"] = new SolidColorBrush(dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1F, 0x00, 0x00, 0x00));
        Resources["Hover"] = new SolidColorBrush(dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
    }

    // ---- Arama

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        _fileSearch?.Cancel();
        Placeholder.Visibility = Query.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var text = Query.Text.Trim();
        // Süren indirmeler listede kalsın.
        var busy = _items.Where(i => i.IsBusy).ToList();
        _items.Clear();
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

        // 1) Bağlantı: YouTube / Instagram → MP4 / MP3
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
                Title = "Tarayıcıda aç",
                Subtitle = uri.Host,
                Glyph = "",
                GlyphBackground = Solid(0x8E, 0x8E, 0x93),
                Run = _ => { Open(uri.ToString()); return Task.FromResult(false); },
            });
            return list;
        }

        // 2) Disk yolu: dosya → Dosya Gezgini'nde seçili göster, klasör → aç
        var path = AsPath(text);
        if (path != null)
        {
            list.Add(PathItem(path));
            return list;
        }

        // 3) Uygulamalar (dosyalar ardından arka planda eklenir)
        // Uygulamalar ve Windows araçları/ayarları iki bölümde; en iyi sonucu içeren bölüm üstte.
        var found = AppIndex.Search(text, 8);
        var sections = found
            .GroupBy(a => a.Kind == EntryKind.App)
            .Select(g => (apps: g.Key, items: g.Take(5).ToList()));
        foreach (var (isApp, items) in sections)
        {
            list.Add(Header(isApp ? "Uygulamalar" : "Sistem ve Ayarlar"));
            foreach (var app in items) list.Add(AppItem(app));
        }
        // SPOTLIGHT_DEMO=1: tanıtım ekran görüntülerinde kişisel dosya yolları görünmesin.
        searchFiles = text.Length >= 2 && Environment.GetEnvironmentVariable("SPOTLIGHT_DEMO") != "1";
        return list;
    }

    /// <summary>Dosya ve klasörleri arka planda arar; kısa bir beklemeyle yazarken gereksiz aramaları atlar.</summary>
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
                rows.Add(Header("Dosyalar ve Klasörler"));
                rows.AddRange(hits.Select(FileItem));
            }
            else if (!FileIndex.IsReady)
            {
                rows.Add(Header(FileIndex.IsScanning ? "Dosyalar dizinleniyor… (ilk tarama birkaç dakika sürer)" : "Dosya dizini hazırlanıyor…"));
            }
            if (rows.Count == 0) return;
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
            EntryKind.Tool => "Windows aracı",
            EntryKind.Setting => "Ayarlar",
            EntryKind.Folder => "Klasör",
            _ => "Uygulama",
        };
        return new ResultItem
        {
            Title = app.Name,
            // Diğer dildeki adı da göster: "Windows aracı · Device Manager"
            Subtitle = app.AltName != null ? $"{kind} · {app.AltName}" : kind,
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
            // macOS'taki gibi: adın altında nereden açıldığı (bulunduğu klasör).
            Subtitle = folder,
            FilePath = path,
            Hint = "Ctrl+Enter · Konumu göster",
            // Simge yüklenene kadar (ya da yüklenemezse) gri belge/klasör sembolü.
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

    /// <summary>Metin bir disk yolu gibi görünüyorsa normalleştirilmiş yolu döndürür.</summary>
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
                Subtitle = "Dosya Gezgini'nde göster · " + Path.GetDirectoryName(path),
                Icon = Native.ShellIcon(path, 64),
                Run = _ => { Explorer($"/select,\"{path}\""); return Task.FromResult(false); },
            };
        }
        if (Directory.Exists(path))
        {
            return new ResultItem
            {
                Title = new DirectoryInfo(path).Name is { Length: > 0 } n ? n : path,
                Subtitle = "Dosya Gezgini'nde aç · " + path,
                Icon = Native.ShellIcon(path, 64),
                Run = _ => { Explorer($"\"{path}\""); return Task.FromResult(false); },
            };
        }
        // Bulunamadı: varsa üst klasörü aç.
        var parent = FirstExisting(Path.GetDirectoryName(path));
        return new ResultItem
        {
            Title = "Bulunamadı",
            Subtitle = parent != null ? $"Var olan en yakın klasörü aç · {parent}" : path,
            Glyph = "",
            GlyphBackground = Solid(0xFF, 0x45, 0x3A),
            Run = parent == null ? null : _ => { Explorer($"\"{parent}\""); return Task.FromResult(false); },
        };
    }

    private ResultItem DownloadItem(string url, bool audio, string site) => new()
    {
        Title = audio ? "MP3 olarak indir" : "MP4 olarak indir",
        Subtitle = $"{site} · {(audio ? "yalnızca ses" : "video")} · İndirilenler\\Spotlight",
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
            row.Title = "İndirildi: " + Path.GetFileName(file);
            row.Subtitle = "Dosya Gezgini'nde göstermek için Enter";
            row.IsBusy = false;
            Notify?.Invoke("İndirme tamamlandı", Path.GetFileName(file), file);
            ReplaceRun(row, file);
        }
        catch (Exception ex)
        {
            row.Progress = -1;
            row.IsBusy = false;
            row.Title = "İndirilemedi";
            row.Subtitle = ex.Message;
            Notify?.Invoke("İndirilemedi", ex.Message, null);
        }
        return true;
    }

    /// <summary>Biten indirme satırı: Enter → dosyayı Dosya Gezgini'nde seçili göster.</summary>
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

    /// <summary>Görünen satırların simgelerini arka planda yükler (önbelleğe alır).</summary>
    private void LoadIconsAsync()
    {
        var ui = TaskScheduler.FromCurrentSynchronizationContext();
        foreach (var row in _items.Where(i => i.Icon == null && !i.IsHeader).ToList())
        {
            if (row.App is { } app)
            {
                if (app.Icon != null) { row.Icon = app.Icon; continue; }
                if (app.IconPath == null) continue; // simge yoksa renkli sembol kalır
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

    // ---- Klavye ve fare

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
                    // Ctrl+Enter: dosyanın konumunu Dosya Gezgini'nde seçili göster (macOS'ta ⌘↵).
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

    // ---- Yardımcılar

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

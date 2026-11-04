using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Odak;

/// <summary>Kurulum ve kaldırma penceresi (Apple tarzı: sade, tek odaklı, koyu cam kart).</summary>
public sealed class SetupWindow : Window
{
    private readonly bool _uninstall;
    private readonly TextBlock _title = new(), _body = new(), _note = new();
    private readonly CheckBox _startup = Check("Windows ile başlat (arka planda, Alt+Space ile açılır)", true);
    private readonly CheckBox _desktop = Check("Masaüstüne kısayol ekle", false);
    private readonly CheckBox _downloads = Check("İndirdiğim MP3/MP4 dosyalarını da sil (İndirilenler\\Spotlight)", false);
    private readonly Button _primary, _secondary;
    private readonly StackPanel _options = new() { Margin = new Thickness(0, 22, 0, 0) };
    private Action? _primaryAction;

    private static readonly Brush Fg = Frozen(Colors.White);
    private static readonly Brush FgDim = Frozen(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly Brush Destructive = Frozen(Color.FromRgb(0xFF, 0x45, 0x3A));
    private static readonly Brush Secondary = Frozen(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

    public SetupWindow(bool uninstall)
    {
        _uninstall = uninstall;
        Title = uninstall ? "Spotlight'ı Kaldır" : "Spotlight Kurulumu";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/odak.ico")); } catch { }

        _primary = PillButton(uninstall ? "Kaldır" : "Kur", uninstall ? Destructive : Accent);
        _secondary = PillButton("Vazgeç", Secondary);
        _primary.Click += (_, _) => _primaryAction?.Invoke();
        _secondary.Click += (_, _) => Close();

        var icon = new Image { Width = 84, Height = 84, Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Center };
        try
        {
            // .ico içindeki en büyük (256 px) kareyi kullan; küçüğünü büyütmek bulanık görünür.
            var decoder = BitmapDecoder.Create(new Uri("pack://application:,,,/Assets/odak.ico"), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapFrame? best = null;
            foreach (var f in decoder.Frames) if (best == null || f.PixelWidth > best.PixelWidth) best = f;
            icon.Source = best;
        }
        catch { }
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);

        _title.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        _title.FontSize = 26;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = Fg;
        _title.HorizontalAlignment = HorizontalAlignment.Center;
        _title.TextAlignment = TextAlignment.Center;

        _body.FontSize = 14;
        _body.Foreground = FgDim;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.TextAlignment = TextAlignment.Center;
        _body.LineHeight = 20;
        _body.Margin = new Thickness(0, 8, 0, 0);

        _note.FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI");
        _note.FontSize = 11.5;
        _note.Foreground = FgDim;
        _note.TextWrapping = TextWrapping.Wrap;
        _note.TextAlignment = TextAlignment.Center;
        _note.Margin = new Thickness(0, 18, 0, 0);

        var buttons = new Grid { Margin = new Thickness(0, 26, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_primary, 2);
        buttons.Children.Add(_secondary);
        buttons.Children.Add(_primary);

        var content = new StackPanel { Margin = new Thickness(36, 34, 36, 30) };
        content.Children.Add(icon);
        content.Children.Add(_title);
        content.Children.Add(_body);
        content.Children.Add(_options);
        content.Children.Add(buttons);
        content.Children.Add(_note);

        var card = new Border
        {
            CornerRadius = new CornerRadius(26),
            Background = new LinearGradientBrush(Color.FromRgb(0x2C, 0x2C, 0x30), Color.FromRgb(0x1C, 0x1C, 0x1E), 90),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 60),
            BorderThickness = new Thickness(1.2),
            Child = content,
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.45 },
        };
        Content = new Border { Padding = new Thickness(30, 20, 30, 40), Child = card };
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        if (uninstall) ShowUninstallStart(); else ShowInstallStart();
    }

    // ---- Kurulum

    private void ShowInstallStart()
    {
        bool update = Installer.IsInstalled;
        _title.Text = update ? "Spotlight'ı Güncelle" : "Spotlight";
        _body.Text = "Uygulamaları, dosyaları ve Windows ayarlarını Türkçe ve İngilizce ara; " +
                     "YouTube ve Instagram bağlantılarını MP3/MP4 olarak indir. Alt+Space ile aç.";
        _options.Children.Clear();
        _options.Children.Add(_startup);
        _options.Children.Add(_desktop);
        _primary.Content = update ? "Güncelle" : "Kur";
        _note.Text = $"Yalnızca bu kullanıcıya kurulur, yönetici izni gerekmez.\n{Installer.InstallDir}";
        _primaryAction = RunInstall;
    }

    private async void RunInstall()
    {
        SetBusy("Kuruluyor…");
        try
        {
            bool startup = _startup.IsChecked == true, desktop = _desktop.IsChecked == true;
            await Task.Run(() => Installer.Install(startup, desktop));
            _title.Text = "Spotlight kuruldu";
            _body.Text = "Başlat menüsünden ya da her yerden Alt+Space ile açabilirsin. " +
                         "Kaldırmak için: Ayarlar › Uygulamalar › Yüklü uygulamalar › Spotlight.";
            _options.Children.Clear();
            _note.Text = "";
            Done("Spotlight'ı Aç", () => { Installer.LaunchInstalled(); Close(); });
        }
        catch (Exception ex)
        {
            ShowError("Kurulum tamamlanamadı", ex.Message);
        }
    }

    // ---- Kaldırma

    private void ShowUninstallStart()
    {
        _title.Text = "Spotlight kaldırılsın mı?";
        _body.Text = "Program, dosya dizini, ayarlar, indirme araçları, kısayollar ve kayıt defteri girdileri silinir. " +
                     "Bilgisayarda Spotlight'tan iz kalmaz.";
        _options.Children.Clear();
        _options.Children.Add(_downloads);
        _note.Text = "İndirdiğin dosyalar sen işaretlemedikçe silinmez.";
        _primaryAction = RunUninstall;
    }

    private async void RunUninstall()
    {
        SetBusy("Kaldırılıyor…");
        try
        {
            bool downloads = _downloads.IsChecked == true;
            await Task.Run(() => Installer.Uninstall(downloads));
            _title.Text = "Spotlight kaldırıldı";
            _body.Text = "Tüm dosyaları ve ayarları silindi. Bu pencereyi kapatınca program klasörü de kaldırılacak.";
            _options.Children.Clear();
            _note.Text = "";
            Done("Kapat", Close);
        }
        catch (Exception ex)
        {
            ShowError("Kaldırma tamamlanamadı", ex.Message);
        }
    }

    // ---- Yardımcılar

    private void SetBusy(string text)
    {
        _primary.IsEnabled = _secondary.IsEnabled = false;
        foreach (var child in _options.Children) ((UIElement)child).IsEnabled = false;
        _primary.Content = text;
    }

    private void Done(string label, Action action)
    {
        _secondary.Visibility = Visibility.Collapsed;
        Grid.SetColumn(_primary, 0);
        Grid.SetColumnSpan(_primary, 3);
        _primary.Background = Accent;
        _primary.Content = label;
        _primary.IsEnabled = true;
        _primaryAction = action;
        _primary.Focus();
    }

    private void ShowError(string title, string message)
    {
        _title.Text = title;
        _body.Text = message;
        _primary.IsEnabled = _secondary.IsEnabled = true;
        _primary.Content = "Tekrar dene";
    }

    private static CheckBox Check(string text, bool on) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5 },
        IsChecked = on,
        Foreground = Fg,
        Margin = new Thickness(4, 6, 0, 6),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>Hap biçimli düğme; basılınca anında hafifçe kararır.</summary>
    private static Button PillButton(string text, Brush background)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border)) { Name = "Bg" };
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(12));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(OpacityProperty, 0.75));
        var disabled = new Trigger { Property = IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(OpacityProperty, 0.6));
        template.Triggers.Add(pressed);
        template.Triggers.Add(disabled);

        return new Button
        {
            Content = text,
            Height = 44,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
            Background = background,
            Template = template,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
        };
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

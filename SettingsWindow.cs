using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Odak;

/// <summary>
/// Settings: appearance (System / Light / Dark), language (English / Türkçe), web search engine and
/// start with Windows. Changes apply immediately, macOS-style; there is no Save button.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _instance;
    private readonly string _hotkey;
    private readonly StackPanel _content = new() { Margin = new Thickness(30, 26, 30, 26) };
    private readonly Border _card = new() { CornerRadius = new CornerRadius(24), BorderThickness = new Thickness(1.2) };

    private Brush _fg = Brushes.White, _fgDim = Brushes.Gray, _groupBg = Brushes.Transparent, _segBg = Brushes.Transparent, _segOn = Brushes.Transparent, _hair = Brushes.Transparent;

    /// <summary>Opens the window, or brings the open one to the front.</summary>
    public static void Open(string hotkey)
    {
        if (_instance != null)
        {
            _instance.Activate();
            return;
        }
        _instance = new SettingsWindow(hotkey);
        _instance.Closed += (_, _) => _instance = null;
        _instance.Show();
        _instance.Activate();
    }

    private SettingsWindow(string hotkey)
    {
        _hotkey = hotkey;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        Topmost = true;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/odak.ico")); } catch { }

        _card.Child = _content;
        _card.Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.4 };
        _card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        Content = new Border { Padding = new Thickness(30, 20, 30, 40), Child = _card };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };

        AppSettings.Changed += Rebuild;
        Closed += (_, _) => AppSettings.Changed -= Rebuild;
        Rebuild();
    }

    /// <summary>Redraws everything; used on open and after each change (theme and language apply live).</summary>
    private void Rebuild()
    {
        var s = AppSettings.Current;
        bool dark = s.IsDark;
        _fg = Frozen(dark ? Colors.White : Color.FromRgb(0x1C, 0x1C, 0x1E));
        _fgDim = Frozen(dark ? Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x99, 0x3C, 0x3C, 0x43));
        _groupBg = Frozen(dark ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        _segBg = Frozen(dark ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));
        _segOn = Frozen(dark ? Color.FromArgb(0xFF, 0x63, 0x63, 0x66) : Colors.White);
        _hair = Frozen(dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0x00, 0x00, 0x00));

        _card.Background = dark
            ? new LinearGradientBrush(Color.FromRgb(0x2C, 0x2C, 0x30), Color.FromRgb(0x1C, 0x1C, 0x1E), 90)
            : new LinearGradientBrush(Color.FromRgb(0xF7, 0xF7, 0xFA), Color.FromRgb(0xEC, 0xEC, 0xF0), 90);
        _card.BorderBrush = dark
            ? new LinearGradientBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 60)
            : new LinearGradientBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x26, 0x00, 0x00, 0x00), 60);
        Title = L.T("Spotlight Settings", "Spotlight Ayarları");

        _content.Children.Clear();

        // Title row with a close button
        var head = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        head.Children.Add(new TextBlock
        {
            Text = L.T("Settings", "Ayarlar"),
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 26,
            FontWeight = FontWeights.SemiBold,
            Foreground = _fg,
        });
        var close = new Button
        {
            Content = "",
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 12,
            Width = 30,
            Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = _fg,
            Background = _segBg,
            Template = RoundTemplate(15),
            Cursor = Cursors.Hand,
            ToolTip = L.T("Close", "Kapat"),
        };
        close.Click += (_, _) => Close();
        head.Children.Add(close);
        _content.Children.Add(head);

        // Appearance
        _content.Children.Add(SectionLabel(L.T("Appearance", "Görünüm")));
        _content.Children.Add(Group(
            Row(L.T("Theme", "Tema"), Segmented(
                new[] { L.T("System", "Sistem"), L.T("Light", "Açık"), L.T("Dark", "Koyu") },
                (int)s.Theme,
                i => { s.Theme = (ThemeMode)i; s.Save(); }))));

        var live = new CheckBox { IsChecked = s.LiveGlass, VerticalAlignment = VerticalAlignment.Center };
        live.Checked += (_, _) => { s.LiveGlass = true; s.Save(); };
        live.Unchecked += (_, _) => { s.LiveGlass = false; s.Save(); };
        _content.Children.Add(Group(
            Row(L.T("Live glass", "Canlı cam"), live),
            Note(L.T("Keeps what is behind the glass up to date. Turn it off while screen sharing so viewers never see the window blink.",
                     "Camın arkasındakini güncel tutar. Ekran paylaşırken kapat; izleyenler pencerenin göz kırptığını görmez."))));

        // Language
        _content.Children.Add(SectionLabel(L.T("Language", "Dil")));
        _content.Children.Add(Group(
            Row(L.T("Display language", "Arayüz dili"), Segmented(
                new[] { "English", "Türkçe" },
                s.IsTurkish ? 1 : 0,
                i => { s.Language = i == 1 ? "tr" : "en"; s.Save(); })),
            Note(L.T("Search always understands both English and Turkish names.", "Arama her zaman hem İngilizce hem Türkçe adları anlar."))));

        // Search
        _content.Children.Add(SectionLabel(L.T("Search", "Arama")));
        _content.Children.Add(Group(
            Row(L.T("Web search", "Web araması"), Segmented(
                new[] { "Google", "Bing", "DuckDuckGo" },
                (int)s.WebSearch,
                i => { s.WebSearch = (SearchEngine)i; s.Save(); })),
            Note(L.T("Used when nothing on your PC matches. Opens in your default browser.",
                     "Bilgisayarında eşleşen bir şey yoksa kullanılır. Varsayılan tarayıcında açılır."))));

        // General
        _content.Children.Add(SectionLabel(L.T("General", "Genel")));
        var startup = new CheckBox
        {
            IsChecked = App.IsStartupEnabled(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        startup.Checked += (_, _) => App.SetStartup(true);
        startup.Unchecked += (_, _) => App.SetStartup(false);
        _content.Children.Add(Group(
            Row(L.T("Start with Windows", "Windows ile başlat"), startup),
            Row(L.T("Keyboard shortcut", "Klavye kısayolu"), new TextBlock { Text = _hotkey, Foreground = _fgDim, FontSize = 13.5, VerticalAlignment = VerticalAlignment.Center })));

        _content.Children.Add(new TextBlock
        {
            Text = $"Spotlight {Installer.Version}",
            Foreground = _fgDim,
            FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI"),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 18, 0, 0),
        });
    }

    // ---- Building blocks (grouped rows, like macOS System Settings)

    private TextBlock SectionLabel(string text) => new()
    {
        Text = text,
        Foreground = _fgDim,
        FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI"),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(6, 14, 0, 6),
    };

    private Border Group(params UIElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0 && rows[i] is not TextBlock) stack.Children.Add(new Border { Height = 1, Background = _hair, Margin = new Thickness(14, 0, 0, 0) });
            stack.Children.Add(rows[i]);
        }
        return new Border { CornerRadius = new CornerRadius(12), Background = _groupBg, Child = stack };
    }

    private Grid Row(string label, UIElement control)
    {
        var g = new Grid { MinHeight = 48, Margin = new Thickness(14, 4, 10, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(new TextBlock { Text = label, Foreground = _fg, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        g.Children.Add(control);
        return g;
    }

    private TextBlock Note(string text) => new()
    {
        Text = text,
        Foreground = _fgDim,
        FontFamily = new FontFamily("Segoe UI Variable Small, Segoe UI"),
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(14, 0, 14, 10),
    };

    /// <summary>A macOS-style segmented control.</summary>
    private Border Segmented(string[] options, int selected, Action<int> onSelect)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2) };
        for (int i = 0; i < options.Length; i++)
        {
            int index = i;
            bool on = i == selected;
            var b = new Button
            {
                Content = options[i],
                MinWidth = 64,
                Height = 28,
                Padding = new Thickness(12, 0, 12, 0),
                FontSize = 12.5,
                FontWeight = on ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = _fg,
                Background = on ? _segOn : Brushes.Transparent,
                Template = RoundTemplate(7),
                Cursor = Cursors.Hand,
            };
            if (on) b.Effect = new DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Direction = 270, Opacity = 0.18 };
            b.Click += (_, _) => { if (!on) onSelect(index); };
            panel.Children.Add(b);
        }
        return new Border { CornerRadius = new CornerRadius(9), Background = _segBg, Child = panel, VerticalAlignment = VerticalAlignment.Center };
    }

    private static ControlTemplate RoundTemplate(double radius)
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(radius));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(OpacityProperty, 0.7));
        template.Triggers.Add(pressed);
        return template;
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

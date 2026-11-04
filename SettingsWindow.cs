using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Odak;

/// <summary>
/// Spotlight ayarları penceresi (Liquid Glass / Apple tarzı sade karanlık kart).
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _currentInstance;

    private readonly CheckBox _webSearch;
    private readonly CheckBox _alwaysShowWebSearch;
    private readonly CheckBox _startup;
    private readonly List<RadioButton> _engineRadios = new();
    private readonly StackPanel _engineOptions;

    private static readonly Brush Fg = Frozen(Colors.White);
    private static readonly Brush FgDim = Frozen(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF));
    private static readonly Brush FgSub = Frozen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x0A, 0x84, 0xFF));
    private static readonly Brush Secondary = Frozen(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CardBg = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly Brush CardBorder = Frozen(Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF));

    public static void Open()
    {
        if (_currentInstance != null)
        {
            if (_currentInstance.WindowState == WindowState.Minimized)
                _currentInstance.WindowState = WindowState.Normal;
            _currentInstance.Activate();
            return;
        }

        var win = new SettingsWindow();
        _currentInstance = win;
        win.Closed += (_, _) => _currentInstance = null;
        win.Show();
        win.Activate();
    }

    public SettingsWindow()
    {
        Title = "Spotlight Ayarları";
        Width = 540;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/odak.ico")); } catch { }

        var settings = AppSettings.Current;

        // ---- Başlık alanı
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconBorder = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            Background = Accent,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "", // Segoe Fluent Icons: Dişli / Ayarlar
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 22,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            }
        };

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock
        {
            Text = "Spotlight Ayarları",
            FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg
        };
        var subtitle = new TextBlock
        {
            Text = "Web araması ve genel tercihler",
            FontSize = 13,
            Foreground = FgDim,
            Margin = new Thickness(0, 2, 0, 0)
        };
        titleStack.Children.Add(title);
        titleStack.Children.Add(subtitle);

        Grid.SetColumn(iconBorder, 0);
        Grid.SetColumn(titleStack, 1);
        headerGrid.Children.Add(iconBorder);
        headerGrid.Children.Add(titleStack);

        // ---- Bölüm 1: Web'de Arama Kutusu
        var webSection = CreateSectionCard("WEB'DE ARAMA");

        _webSearch = Check("Web'de arama önerilerini etkinleştir", settings.EnableWebSearch);
        var webHint = Hint("Dosya veya uygulama bulunamadığında aranan terimi tarayıcıda doğrudan açmanızı sağlar.");

        _alwaysShowWebSearch = Check("Her zaman listenin sonunda da göster", settings.AlwaysShowWebSearch);
        var alwaysHint = Hint("Bilgisayarda eşleşen sonuçlar olsa bile listenin en altına 'Web'de Ara' seçeneğini ekler.");

        // Arama Motorları
        var engineLabel = new TextBlock
        {
            Text = "Arama Motoru:",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = Fg,
            Margin = new Thickness(6, 12, 0, 6)
        };

        _engineOptions = new StackPanel { Margin = new Thickness(6, 0, 0, 6) };
        var engines = new[] { "Google", "DuckDuckGo", "Bing", "Yandex", "Ecosia" };
        var wrap = new WrapPanel();

        foreach (var eng in engines)
        {
            var rb = new RadioButton
            {
                Content = eng,
                GroupName = "SearchEngines",
                IsChecked = string.Equals(settings.SearchEngine, eng, StringComparison.OrdinalIgnoreCase),
                Foreground = Fg,
                FontSize = 13,
                Margin = new Thickness(0, 4, 16, 4),
                Cursor = Cursors.Hand
            };
            _engineRadios.Add(rb);
            wrap.Children.Add(rb);
        }
        _engineOptions.Children.Add(wrap);

        // Web araması kapalıysa alt seçenekleri devre dışı bırak
        void UpdateWebOptionsEnabled()
        {
            bool on = _webSearch.IsChecked == true;
            _alwaysShowWebSearch.IsEnabled = on;
            _engineOptions.IsEnabled = on;
            _alwaysShowWebSearch.Opacity = on ? 1.0 : 0.5;
            _engineOptions.Opacity = on ? 1.0 : 0.5;
        }
        _webSearch.Checked += (_, _) => UpdateWebOptionsEnabled();
        _webSearch.Unchecked += (_, _) => UpdateWebOptionsEnabled();
        UpdateWebOptionsEnabled();

        webSection.Children.Add(_webSearch);
        webSection.Children.Add(webHint);
        webSection.Children.Add(engineLabel);
        webSection.Children.Add(_engineOptions);
        webSection.Children.Add(new Border { Height = 1, Background = CardBorder, Margin = new Thickness(0, 10, 0, 8) });
        webSection.Children.Add(_alwaysShowWebSearch);
        webSection.Children.Add(alwaysHint);

        // ---- Bölüm 2: Genel Ayarlar
        var genSection = CreateSectionCard("GENEL");
        _startup = Check("Windows ile başlat", App.IsStartupEnabled());
        var startupHint = Hint("Bilgisayar açıldığında Spotlight arka planda hazır bekler (Alt + Boşluk).");
        genSection.Children.Add(_startup);
        genSection.Children.Add(startupHint);

        // ---- Alt Butonlar
        var buttons = new Grid { Margin = new Thickness(0, 22, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition());
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition());

        var cancelBtn = PillButton("Vazgeç", Secondary);
        var saveBtn = PillButton("Kaydet", Accent);

        cancelBtn.Click += (_, _) => Close();
        saveBtn.Click += (_, _) => SaveAndClose();

        Grid.SetColumn(cancelBtn, 0);
        Grid.SetColumn(saveBtn, 2);
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(saveBtn);

        // ---- Ana Düzen
        var content = new StackPanel { Margin = new Thickness(32, 28, 32, 28) };
        content.Children.Add(headerGrid);
        content.Children.Add(webSection);
        content.Children.Add(new FrameworkElement { Height = 14 });
        content.Children.Add(genSection);
        content.Children.Add(buttons);

        var card = new Border
        {
            CornerRadius = new CornerRadius(24),
            Background = new LinearGradientBrush(Color.FromRgb(0x2C, 0x2C, 0x30), Color.FromRgb(0x1C, 0x1C, 0x1E), 90),
            BorderBrush = new LinearGradientBrush(Color.FromArgb(0x60, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 60),
            BorderThickness = new Thickness(1.2),
            Child = content,
            Effect = new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.45 },
        };

        Content = new Border { Padding = new Thickness(24, 20, 24, 30), Child = card };
        card.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private void SaveAndClose()
    {
        var settings = AppSettings.Current;
        settings.EnableWebSearch = _webSearch.IsChecked == true;
        settings.AlwaysShowWebSearch = _alwaysShowWebSearch.IsChecked == true;

        foreach (var rb in _engineRadios)
        {
            if (rb.IsChecked == true && rb.Content is string eng)
            {
                settings.SearchEngine = eng;
                break;
            }
        }

        settings.Save();

        // Startup ayarını uygula
        bool startup = _startup.IsChecked == true;
        if (startup != App.IsStartupEnabled())
        {
            App.SetStartup(startup);
        }

        Close();
    }

    private static StackPanel CreateSectionCard(string header)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = header,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = FgSub,
            Margin = new Thickness(6, 0, 0, 6)
        });

        var cardContent = new StackPanel { Margin = new Thickness(14, 12, 14, 12) };
        var cardBorder = new Border
        {
            CornerRadius = new CornerRadius(14),
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            Child = cardContent
        };

        panel.Children.Add(cardBorder);
        return cardContent;
    }

    private static CheckBox Check(string text, bool on) => new()
    {
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 13.5, FontWeight = FontWeights.Medium },
        IsChecked = on,
        Foreground = Fg,
        Margin = new Thickness(2, 4, 0, 2),
        VerticalContentAlignment = VerticalAlignment.Center,
        Cursor = Cursors.Hand
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 12,
        Foreground = FgDim,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(26, 0, 0, 6)
    };

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
        template.Triggers.Add(pressed);

        return new Button
        {
            Content = text,
            Height = 40,
            FontSize = 14,
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

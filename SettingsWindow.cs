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
/// macOS / iOS Ayarlar tarzında minimalist lüks karanlık tema ayar penceresi.
/// Gerçek Apple Switch'leri ve interaktif tuş rozeti içerir.
/// </summary>
public sealed class SettingsWindow : Window
{
    private static SettingsWindow? _currentInstance;

    // ----- Tercih Verileri
    private bool _enableWebSearch;
    private bool _webSearchAtTop;
    private bool _launchAtStartup;
    private string _currentHotkey;

    // ----- UI Elemanları
    private Border _switchStartup = null!;
    private Border _switchWeb = null!;
    private Border _switchTop = null!;
    private Border _webTopRow = null!;

    // Kısayol Rozeti Elemanları
    private Border _hotkeyBadge = null!;
    private TextBlock _hotkeyBadgeText = null!;
    private bool _isRecording = false;

    // Renk Paleti (Apple Dark System Palette)
    private static readonly Color AppleBg = Color.FromRgb(0x1C, 0x1C, 0x1E);
    private static readonly Color CardBg = Color.FromRgb(0x2C, 0x2C, 0x2E);
    private static readonly Color CardBorder = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
    private static readonly Color SeparatorColor = Color.FromArgb(0x18, 0xFF, 0xFF, 0xFF);
    private static readonly Color AppleGreen = Color.FromRgb(0x34, 0xC7, 0x59);
    private static readonly Color AppleBlue = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly Color AppleOrange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color ApplePurple = Color.FromRgb(0xBF, 0x5A, 0xF2);
    private static readonly Color SwitchOff = Color.FromRgb(0x39, 0x39, 0x3D);

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
        Width = 480;
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI, -apple-system, sans-serif");
        UseLayoutRounding = true;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/odak.ico")); } catch { }

        // Ayarları yükle
        var s = AppSettings.Current;
        _enableWebSearch = s.EnableWebSearch;
        _webSearchAtTop = s.AlwaysShowWebSearchAtTop;
        _launchAtStartup = App.IsStartupEnabled();
        _currentHotkey = string.IsNullOrEmpty(s.Hotkey) ? "Alt+Space" : s.Hotkey;

        BuildUI();

        // Klavye dinleyici (kısayolu kaydetmek için)
        PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    private void BuildUI()
    {
        var mainStack = new StackPanel { Margin = new Thickness(24, 22, 24, 24) };

        // ════════════ 1. BAŞLIK ÇUBUĞU ════════════
        var headerGrid = new Grid { Margin = new Thickness(0, 0, 0, 18) };
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var appIcon = new Border
        {
            Width = 42,
            Height = 42,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(AppleBlue),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = "", // Segoe Fluent Icons: Dişli
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 20,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleText = new TextBlock
        {
            Text = "Spotlight Ayarları",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White
        };
        var subText = new TextBlock
        {
            Text = "Kısayol ve genel tercihler",
            FontSize = 12.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 1, 0, 0)
        };
        titleStack.Children.Add(titleText);
        titleStack.Children.Add(subText);

        var closeButton = new Button
        {
            Width = 28,
            Height = 28,
            Content = "✕",
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xAA, 0xFF, 0xFF, 0xFF)),
            Background = new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Top
        };
        var closeTmpl = new ControlTemplate(typeof(Button));
        var closeBorder = new FrameworkElementFactory(typeof(Border));
        closeBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        closeBorder.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var closeCp = new FrameworkElementFactory(typeof(ContentPresenter));
        closeCp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        closeCp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        closeBorder.AppendChild(closeCp);
        closeTmpl.VisualTree = closeBorder;
        closeButton.Template = closeTmpl;
        closeButton.Click += (_, _) => Close();

        Grid.SetColumn(appIcon, 0);
        Grid.SetColumn(titleStack, 1);
        Grid.SetColumn(closeButton, 2);
        headerGrid.Children.Add(appIcon);
        headerGrid.Children.Add(titleStack);
        headerGrid.Children.Add(closeButton);
        mainStack.Children.Add(headerGrid);

        // ════════════ 2. GENEL VE KISAYOL BÖLÜMÜ ════════════
        mainStack.Children.Add(CreateSectionHeader("GENEL & KISAYOL"));

        var genCard = CreateGroupCard();
        var genPanel = (StackPanel)genCard.Child;

        // Satır 1: Başlangıçta Çalıştır
        _switchStartup = CreateAppleSwitch(_launchAtStartup, on => _launchAtStartup = on);
        genPanel.Children.Add(CreateSettingRow(
            iconGlyph: "",
            iconBg: AppleBlue,
            title: "Windows ile başlat",
            subtitle: "Bilgisayar açıldığında Spotlight arka planda hazır bekler",
            control: _switchStartup
        ));

        genPanel.Children.Add(CreateDivider(leftIndent: 52));

        // Satır 2: Spotlight Açma Kısayolu (Apple Mac tarzı tıklanabilir Key Cap Rozeti)
        var hotkeyRow = CreateHotkeySettingRow();
        genPanel.Children.Add(hotkeyRow);

        mainStack.Children.Add(genCard);

        // ════════════ 3. WEB'DE ARAMA BÖLÜMÜ ════════════
        mainStack.Children.Add(CreateSectionHeader("WEB'DE ARAMA"));

        var webCard = CreateGroupCard();
        var webPanel = (StackPanel)webCard.Child;

        // Satır 1: Web Aramasını Etkinleştir Switch
        _switchWeb = CreateAppleSwitch(_enableWebSearch, on =>
        {
            _enableWebSearch = on;
            UpdateWebSectionState();
        });
        webPanel.Children.Add(CreateSettingRow(
            iconGlyph: "", // Dünya simgesi
            iconBg: ApplePurple,
            title: "Web'de arama",
            subtitle: "Sonuç bulunamadığında tarayıcıda aratma imkanı sunar",
            control: _switchWeb
        ));

        // Satır 2: Her zaman en üstte göster Switch
        _switchTop = CreateAppleSwitch(_webSearchAtTop, on => _webSearchAtTop = on);
        _webTopRow = CreateSettingRow(
            iconGlyph: "", // Yukarı ok
            iconBg: AppleOrange,
            title: "Aramayı en üstte göster",
            subtitle: "'Web'de Ara' seçeneği listenin ilk sırasında yer alır",
            control: _switchTop
        );

        webPanel.Children.Add(CreateDivider(leftIndent: 52));
        webPanel.Children.Add(_webTopRow);

        mainStack.Children.Add(webCard);

        // ════════════ 4. ALT EYLEM BUTONLARI (KAYDET & VAZGEÇ) ════════════
        var footerGrid = new Grid { Margin = new Thickness(0, 24, 0, 0) };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.3, GridUnitType.Star) });

        var cancelBtn = CreateAppleButton("Vazgeç", isPrimary: false);
        cancelBtn.Click += (_, _) => Close();

        var saveBtn = CreateAppleButton("Değişiklikleri Kaydet", isPrimary: true);
        saveBtn.Click += (_, _) => SaveAndClose();

        Grid.SetColumn(cancelBtn, 0);
        Grid.SetColumn(saveBtn, 2);
        footerGrid.Children.Add(cancelBtn);
        footerGrid.Children.Add(saveBtn);
        mainStack.Children.Add(footerGrid);

        // Ana Pencere Kabuğu
        var mainBorder = new Border
        {
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(AppleBg),
            BorderBrush = new SolidColorBrush(CardBorder),
            BorderThickness = new Thickness(1),
            Child = mainStack,
            Effect = new DropShadowEffect
            {
                BlurRadius = 50,
                ShadowDepth = 15,
                Direction = 270,
                Opacity = 0.55
            }
        };

        Content = new Border { Padding = new Thickness(16), Child = mainBorder };
        mainBorder.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        KeyDown += (_, e) => { if (e.Key == Key.Escape && !_isRecording) Close(); };

        UpdateWebSectionState();
    }

    // ════════════ KISAYOL SATIRI (Apple Key Badge Tarzı) ════════════
    private Border CreateHotkeySettingRow()
    {
        var rowGrid = new Grid { Margin = new Thickness(14, 11, 14, 11) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // İkon
        var iconBorder = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(AppleGreen),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = "⌨",
                FontSize = 16,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        // Başlıklar
        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = "Spotlight Kısayolu",
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = Brushes.White
        });

        var hintText = new TextBlock
        {
            Text = "Değiştirmek için tuş rozetine tıklayın",
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 2, 0, 0)
        };
        textStack.Children.Add(hintText);

        // Apple Mac Klavye Tuşu Rozeti (Key Cap)
        _hotkeyBadgeText = new TextBlock
        {
            Text = FormatBadgeText(_currentHotkey),
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _hotkeyBadge = new Border
        {
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14, 7, 14, 7),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = _hotkeyBadgeText,
            Effect = new DropShadowEffect
            {
                BlurRadius = 8,
                ShadowDepth = 2,
                Direction = 270,
                Opacity = 0.25
            }
        };

        _hotkeyBadge.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            StartRecordingHotkey();
        };

        Grid.SetColumn(iconBorder, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(_hotkeyBadge, 2);
        rowGrid.Children.Add(iconBorder);
        rowGrid.Children.Add(textStack);
        rowGrid.Children.Add(_hotkeyBadge);

        return new Border { Child = rowGrid };
    }

    private void StartRecordingHotkey()
    {
        _isRecording = true;
        _hotkeyBadge.Background = new SolidColorBrush(AppleOrange);
        _hotkeyBadge.BorderBrush = Brushes.Transparent;
        _hotkeyBadgeText.Text = "⌨ Tuşa Basın...";
        _hotkeyBadgeText.Foreground = Brushes.White;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_isRecording) return;

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Yalnızca modifier tuşuna basıldıysa henüz bitirme
        if (key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return;
        }

        // Escape basıldıysa iptal et
        if (key == Key.Escape)
        {
            _isRecording = false;
            ResetBadgeToNormal();
            return;
        }

        // Modifiers topla
        var mods = new List<string>();
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0 || e.Key == Key.System) mods.Add("Alt");
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods.Add("Ctrl");
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods.Add("Shift");
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods.Add("Win");

        // Kullanıcı hiç modifier basmadıysa varsayılan olarak Alt ekleyelim
        if (mods.Count == 0) mods.Add("Alt");

        string keyName = key switch
        {
            Key.Space => "Space",
            Key.Return => "Enter",
            _ => key.ToString()
        };

        _currentHotkey = string.Join("+", mods) + "+" + keyName;
        _isRecording = false;

        _hotkeyBadge.Background = new SolidColorBrush(AppleGreen);
        _hotkeyBadge.BorderBrush = Brushes.Transparent;
        _hotkeyBadgeText.Text = FormatBadgeText(_currentHotkey);
    }

    private void ResetBadgeToNormal()
    {
        _hotkeyBadge.Background = new SolidColorBrush(Color.FromArgb(0x35, 0xFF, 0xFF, 0xFF));
        _hotkeyBadge.BorderBrush = new SolidColorBrush(Color.FromArgb(0x45, 0xFF, 0xFF, 0xFF));
        _hotkeyBadgeText.Text = FormatBadgeText(_currentHotkey);
        _hotkeyBadgeText.Foreground = Brushes.White;
    }

    private static string FormatBadgeText(string hotkey)
    {
        // Apple tarzı sembol ve tuş gösterimi
        return hotkey.Replace("Space", "Boşluk")
                     .Replace("+", " + ");
    }

    // ════════════ GERÇEK APPLE iOS SWITCH (TOGGLE) ════════════
    private Border CreateAppleSwitch(bool initialValue, Action<bool> onChanged)
    {
        bool state = initialValue;

        var knob = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(state ? 24 : 2, 0, 0, 0),
            Effect = new DropShadowEffect
            {
                BlurRadius = 4,
                ShadowDepth = 1,
                Direction = 270,
                Opacity = 0.3
            }
        };

        var track = new Border
        {
            Width = 48,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(state ? AppleGreen : SwitchOff),
            Cursor = Cursors.Hand,
            Child = knob
        };

        track.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            state = !state;
            track.Background = new SolidColorBrush(state ? AppleGreen : SwitchOff);
            knob.Margin = new Thickness(state ? 24 : 2, 0, 0, 0);
            onChanged(state);
        };

        return track;
    }

    // ════════════ YARDIMCI GÖRSEL ELEMANLAR ════════════
    private static TextBlock CreateSectionHeader(string text) => new()
    {
        Text = text,
        FontSize = 11,
        FontWeight = FontWeights.Bold,
        Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
        Margin = new Thickness(6, 14, 0, 6)
    };

    private static Border CreateGroupCard() => new()
    {
        CornerRadius = new CornerRadius(14),
        Background = new SolidColorBrush(CardBg),
        BorderBrush = new SolidColorBrush(CardBorder),
        BorderThickness = new Thickness(1),
        ClipToBounds = true,
        Child = new StackPanel()
    };

    private static Border CreateSettingRow(string iconGlyph, Color iconBg, string title, string subtitle, UIElement control)
    {
        var rowGrid = new Grid { Margin = new Thickness(14, 11, 14, 11) };
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBorder = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(iconBg),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = iconGlyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets, Segoe UI Symbol"),
                FontSize = 16,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };

        var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textStack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 13.5,
            FontWeight = FontWeights.Medium,
            Foreground = Brushes.White
        });
        textStack.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 2, 0, 0),
            TextWrapping = TextWrapping.Wrap
        });

        Grid.SetColumn(iconBorder, 0);
        Grid.SetColumn(textStack, 1);
        Grid.SetColumn(control, 2);
        rowGrid.Children.Add(iconBorder);
        rowGrid.Children.Add(textStack);
        rowGrid.Children.Add(control);

        return new Border { Child = rowGrid };
    }

    private static Border CreateDivider(double leftIndent) => new()
    {
        Height = 1,
        Background = new SolidColorBrush(SeparatorColor),
        Margin = new Thickness(leftIndent, 0, 0, 0)
    };

    private static Button CreateAppleButton(string text, bool isPrimary)
    {
        var btn = new Button
        {
            Content = text,
            Height = 40,
            FontSize = 13.5,
            FontWeight = isPrimary ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = Brushes.White,
            Background = isPrimary ? new SolidColorBrush(AppleBlue) : new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand
        };

        var tmpl = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(cp);
        tmpl.VisualTree = border;

        var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(OpacityProperty, 0.75));
        tmpl.Triggers.Add(pressed);

        btn.Template = tmpl;
        return btn;
    }

    private void UpdateWebSectionState()
    {
        _webTopRow.IsEnabled = _enableWebSearch;
        _webTopRow.Opacity = _enableWebSearch ? 1.0 : 0.4;
    }

    // ════════════ AYARLARI KAYDET ════════════
    private void SaveAndClose()
    {
        var s = AppSettings.Current;
        s.EnableWebSearch = _enableWebSearch;
        s.AlwaysShowWebSearchAtTop = _webSearchAtTop;
        s.AlwaysShowWebSearch = true;
        s.Hotkey = _currentHotkey;
        s.Save();

        // Kısayolu anında uygula
        App.UpdateHotkey(s.Hotkey);

        // Windows ile başlat ayarını uygula
        if (_launchAtStartup != App.IsStartupEnabled())
        {
            App.SetStartup(_launchAtStartup);
        }

        Close();
    }
}

using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Odak;

/// <summary>Arama listesindeki bir satır: uygulama, dosya yolu ya da indirme eylemi.</summary>
public sealed class ResultItem : INotifyPropertyChanged
{
    private string _title = "";
    private string _subtitle = "";
    private ImageSource? _icon;
    private double _progress = -1;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }

    /// <summary>Uygulama simgesi; yoksa <see cref="Glyph"/> (Segoe Fluent Icons) gösterilir.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set { Set(ref _icon, value); OnChanged(nameof(GlyphVisibility)); OnChanged(nameof(IconVisibility)); }
    }

    public string Glyph { get; init; } = "";
    /// <summary>Sembol arka planının rengi (iOS tarzı renkli kare).</summary>
    public Brush GlyphBackground { get; init; } = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

    /// <summary>İlerleme 0..1; -1 = çubuk gizli.</summary>
    public double Progress
    {
        get => _progress;
        set { Set(ref _progress, value); OnChanged(nameof(ProgressVisibility)); }
    }

    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => Progress >= 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Enter'a basılınca çalışır. true dönerse pencere açık kalır (ör. indirme sürerken).</summary>
    public Func<ResultItem, Task<bool>>? Run { get; init; }

    public AppEntry? App { get; init; }

    /// <summary>Bölüm başlığı satırı ("Uygulamalar", "Dosyalar ve Klasörler"); seçilemez.</summary>
    public bool IsHeader { get; init; }
    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RowVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Dosya/klasör sonucuysa tam yolu (Ctrl+Enter → konumu göster).</summary>
    public string? FilePath { get; init; }
    /// <summary>Seçili satırın sağında görünen kısayol ipucu.</summary>
    public string Hint { get; init; } = "";
    public bool IsBusy { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        OnChanged(name!);
    }

    private void OnChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

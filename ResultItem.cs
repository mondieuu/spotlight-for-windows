using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;

namespace Odak;

/// <summary>A row in the search list: an app, a file path or a download action.</summary>
public sealed class ResultItem : INotifyPropertyChanged
{
    private string _title = "";
    private string _subtitle = "";
    private ImageSource? _icon;
    private double _progress = -1;

    public string Title { get => _title; set => Set(ref _title, value); }
    public string Subtitle { get => _subtitle; set => Set(ref _subtitle, value); }

    /// <summary>App icon; if missing, <see cref="Glyph"/> (Segoe Fluent Icons) is shown.</summary>
    public ImageSource? Icon
    {
        get => _icon;
        set { Set(ref _icon, value); OnChanged(nameof(GlyphVisibility)); OnChanged(nameof(IconVisibility)); }
    }

    public string Glyph { get; init; } = "";
    /// <summary>Background colour of the symbol (an iOS-style coloured square).</summary>
    public Brush GlyphBackground { get; init; } = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF));

    /// <summary>Progress 0..1; -1 = bar hidden.</summary>
    public double Progress
    {
        get => _progress;
        set { Set(ref _progress, value); OnChanged(nameof(ProgressVisibility)); }
    }

    public Visibility IconVisibility => Icon != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressVisibility => Progress >= 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Runs when Enter is pressed. If it returns true, the window stays open (e.g. while a download runs).</summary>
    public Func<ResultItem, Task<bool>>? Run { get; init; }

    public AppEntry? App { get; init; }

    /// <summary>Section header row ("Applications", "Files and Folders"); not selectable.</summary>
    public bool IsHeader { get; init; }
    public Visibility HeaderVisibility => IsHeader ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RowVisibility => IsHeader ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Full path, if this is a file/folder result (Ctrl+Enter → show its location).</summary>
    public string? FilePath { get; init; }
    /// <summary>Shortcut hint shown on the right of the selected row.</summary>
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

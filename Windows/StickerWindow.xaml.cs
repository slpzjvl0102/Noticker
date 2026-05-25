using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Noticker.Data;
using Noticker.Models;
using Noticker.Sync;

namespace Noticker.Windows;

public partial class StickerWindow : Window, INotifyPropertyChanged
{
    private readonly Sticker _sticker;
    private readonly StickerRepository _repo;
    private readonly DebouncedSyncService _debounce;
    private bool _loading = true;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public StickerWindow(Sticker sticker, StickerRepository repo, SyncQueue queue)
    {
        _sticker = sticker;
        _repo = repo;
        _debounce = new DebouncedSyncService(queue);

        InitializeComponent();
        DataContext = this;

        // Bind color swap from AppSettings
        AppSettings.Instance.PropertyChanged += OnAppSettingsChanged;

        // Populate category list
        RefreshCategoryOptions();
        CategoryBox.SelectedValue = _sticker.Category;

        _loading = false;
        UpdateSyncIndicator();
    }

    // ── Data bindings ──────────────────────────────────────────────────────────

    public string StickerTitle
    {
        get => _sticker.Title;
        set { _sticker.Title = value; Notify(nameof(StickerTitle)); }
    }

    public string Body
    {
        get => _sticker.Body;
        set { _sticker.Body = value; Notify(nameof(Body)); UpdateCharCounter(); }
    }

    public string? Category
    {
        get => _sticker.Category;
        set { _sticker.Category = value; Notify(nameof(Category)); }
    }

    public List<string?> CategoryOptions
    {
        get
        {
            var opts = new List<string?> { null };
            opts.AddRange(AppSettings.Instance.CategoryOptions);
            return opts;
        }
    }

    public bool HasCategoryOptions => AppSettings.Instance.CategoryOptions.Count > 0;

    // ── Colors (driven by AppSettings.ColorSwapped + category) ───────────────

    private bool Swapped => AppSettings.Instance.ColorSwapped;

    private static readonly SolidColorBrush _darkGray = new(Color.FromRgb(0x33, 0x33, 0x33));

    private static readonly Dictionary<string, Color> _notionColors = new()
    {
        ["default"] = Color.FromRgb(0x33, 0x33, 0x33),
        ["gray"]    = Color.FromRgb(0x4A, 0x4A, 0x4A),
        ["brown"]   = Color.FromRgb(0x60, 0x36, 0x1A),
        ["orange"]  = Color.FromRgb(0x99, 0x4A, 0x00),
        ["yellow"]  = Color.FromRgb(0x7B, 0x56, 0x0E),
        ["green"]   = Color.FromRgb(0x1A, 0x5C, 0x3A),
        ["blue"]    = Color.FromRgb(0x1A, 0x44, 0x80),
        ["purple"]  = Color.FromRgb(0x4D, 0x21, 0x7A),
        ["pink"]    = Color.FromRgb(0x80, 0x1D, 0x5A),
        ["red"]     = Color.FromRgb(0x80, 0x1C, 0x1C),
    };

    private SolidColorBrush? CategoryBarBrush()
    {
        var cat = _sticker.Category;
        if (cat is null) return null;
        var colors = AppSettings.Instance.CategoryColors;
        if (colors.TryGetValue(cat, out var notionColor) &&
            _notionColors.TryGetValue(notionColor, out var wpfColor))
            return new SolidColorBrush(wpfColor);
        return null;
    }

    public Brush TitleBackground => CategoryBarBrush() ?? (Swapped ? Brushes.White : _darkGray);
    public Brush TitleForeground => CategoryBarBrush() is not null ? Brushes.White
                                    : (Swapped ? Brushes.Black : Brushes.White);
    public Brush BodyBackground => Swapped ? _darkGray : Brushes.White;
    public Brush BodyForeground => Swapped ? Brushes.White : Brushes.Black;

    // ── Sync indicator ─────────────────────────────────────────────────────────

    public Brush SyncDotColor => _sticker.SyncState switch
    {
        "synced"  => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
        "failed"  => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        _         => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
    };

    public string SyncTooltip => _sticker.SyncState switch
    {
        "synced"  => $"동기화됨 ({_sticker.LastSyncedAt?[..10] ?? ""})",
        "failed"  => "동기화 실패 (수동 Sync로 재시도)",
        _         => "동기화 대기 중…",
    };

    // ── Char counter ───────────────────────────────────────────────────────────

    public string CharCounterText => $"{_sticker.Body.Length:N0}자";
    public Visibility CharCounterVisibility =>
        _sticker.Body.Length >= 1800 ? Visibility.Visible : Visibility.Collapsed;

    // ── Event handlers ─────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        DragMove();
        SavePosition();
    }

    private void TitleBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = false; // allow focus without triggering DragMove
    }

    private void TitleBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loading) return;
        _sticker.Title = TitleBox.Text;
        Notify(nameof(StickerTitle));
        SaveContent();
        _debounce.OnChanged(_sticker);
    }

    private void BodyBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loading) return;
        _sticker.Body = BodyBox.Text;
        SaveContent();
        UpdateCharCounter();
        _debounce.OnChanged(_sticker);
    }

    private void CategoryBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _sticker.Category = CategoryBox.SelectedValue as string;
        SaveContent();
        UpdateBarColors();
        _debounce.OnChanged(_sticker);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "이 스티커를 삭제할까요?\nNotion에 동기화된 내용은 그대로 유지됩니다.",
            "스티커 삭제",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (result == MessageBoxResult.OK)
        {
            _debounce.Cancel();
            _repo.Delete(_sticker.Id);
            Close();
        }
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newW = Math.Max(MinWidth, Width + e.HorizontalChange);
        double newH = Math.Max(MinHeight, Height + e.VerticalChange);
        Width = newW;
        Height = newH;
        SaveSize();
    }

    protected override void OnClosed(EventArgs e)
    {
        _debounce.Cancel();
        AppSettings.Instance.PropertyChanged -= OnAppSettingsChanged;
        base.OnClosed(e);
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!_loading) SavePosition();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private void SaveContent()
    {
        _sticker.UpdatedAt = DateTime.UtcNow.ToString("O");
        _sticker.SyncState = "pending";
        try { _repo.Update(_sticker); }
        catch { /* logged inside repo */ }
        UpdateSyncIndicator();
    }

    private void SavePosition()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(
            new System.Windows.Interop.WindowInteropHelper(this).Handle)
            ?? System.Windows.Forms.Screen.PrimaryScreen!;

        _sticker.MonitorDeviceName = screen.DeviceName;
        _sticker.PositionX = (int)(Left - screen.WorkingArea.Left);
        _sticker.PositionY = (int)(Top - screen.WorkingArea.Top);
        try { _repo.Update(_sticker); }
        catch { /* logged inside repo */ }
    }

    private void SaveSize()
    {
        _sticker.Width = (int)Width;
        _sticker.Height = (int)Height;
        try { _repo.Update(_sticker); }
        catch { /* logged inside repo */ }
    }

    private void UpdateSyncIndicator()
    {
        Notify(nameof(SyncDotColor));
        Notify(nameof(SyncTooltip));
    }

    private void UpdateBarColors()
    {
        Notify(nameof(TitleBackground));
        Notify(nameof(TitleForeground));
    }

    private void UpdateCharCounter()
    {
        Notify(nameof(CharCounterText));
        Notify(nameof(CharCounterVisibility));
    }

    private void OnAppSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ColorSwapped))
        {
            Notify(nameof(TitleBackground));
            Notify(nameof(TitleForeground));
            Notify(nameof(BodyBackground));
            Notify(nameof(BodyForeground));
        }
    }

    public void RefreshCategoryOptions()
    {
        Notify(nameof(CategoryOptions));
        Notify(nameof(HasCategoryOptions));
        UpdateBarColors();
    }
}

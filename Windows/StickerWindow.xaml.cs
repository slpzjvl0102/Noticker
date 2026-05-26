using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
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
    private bool _formattingBarVisible = false;

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

        AppSettings.Instance.PropertyChanged += OnAppSettingsChanged;

        RefreshCategoryOptions();
        CategoryBox.SelectedValue = _sticker.Category;

        LoadBody();

        _loading = false;
        UpdateSyncIndicator();
    }

    // ── Formatting bar visibility ──────────────────────────────────────────────

    public bool IsFormattingBarVisible
    {
        get => _formattingBarVisible;
        private set { _formattingBarVisible = value; Notify(nameof(IsFormattingBarVisible)); }
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        IsFormattingBarVisible = true;
    }

    protected override void OnDeactivated(EventArgs e)
    {
        base.OnDeactivated(e);
        IsFormattingBarVisible = false;
    }

    // ── Data bindings ──────────────────────────────────────────────────────────

    public string StickerTitle
    {
        get => _sticker.Title;
        set { _sticker.Title = value; Notify(nameof(StickerTitle)); }
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

    // ── Font / formatting ──────────────────────────────────────────────────────

    private static readonly string[] _iconFontPrefixes =
        ["Wingdings", "Symbol", "Marlett", "Webdings", "MT Extra", "MS Outlook"];

    private static readonly IReadOnlyList<string> _availableFonts = Fonts.SystemFontFamilies
        .Select(f => f.Source)
        .Where(name => !_iconFontPrefixes
            .Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        .OrderBy(n => n)
        .ToList();

    public IReadOnlyList<string> AvailableFonts => _availableFonts;

    public string StickerFontFamily
    {
        get => _sticker.FontFamily;
        set
        {
            _sticker.FontFamily = value;
            Notify(nameof(StickerFontFamily));
            ApplyFontToDocument();
        }
    }

    private void ApplyFontToDocument()
    {
        if (string.IsNullOrEmpty(_sticker.FontFamily)) return;
        var range = new TextRange(BodyBox.Document.ContentStart, BodyBox.Document.ContentEnd);
        range.ApplyPropertyValue(TextElement.FontFamilyProperty, new System.Windows.Media.FontFamily(_sticker.FontFamily));
    }

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

    public string CharCounterText
    {
        get
        {
            if (BodyBox == null) return "0자";
            var t = new TextRange(BodyBox.Document.ContentStart, BodyBox.Document.ContentEnd).Text;
            return $"{t.Length:N0}자";
        }
    }

    public Visibility CharCounterVisibility
    {
        get
        {
            if (BodyBox == null) return Visibility.Collapsed;
            var t = new TextRange(BodyBox.Document.ContentStart, BodyBox.Document.ContentEnd).Text;
            return t.Length >= 1800 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ── Event handlers ─────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        DragMove();
        SavePosition();
    }

    private void TitleBox_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = false;
    }

    private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _sticker.Title = TitleBox.Text;
        Notify(nameof(StickerTitle));
        SaveContent();
        _debounce.OnChanged(_sticker);
    }

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        SaveBodyContent();
        UpdateCharCounter();
        _debounce.OnChanged(_sticker);
        BodyPlaceholder.Visibility = IsBodyEmpty() ? Visibility.Visible : Visibility.Collapsed;
        SyncFormattingButtons();
    }

    private void BodyBox_SelectionChanged(object sender, RoutedEventArgs e) =>
        SyncFormattingButtons();

    private void BoldButton_Click(object sender, RoutedEventArgs e)
    {
        var newWeight = BoldButton.IsChecked == true ? FontWeights.Bold : FontWeights.Normal;
        BodyBox.Selection.ApplyPropertyValue(TextElement.FontWeightProperty, newWeight);
        BodyBox.Focus();
    }

    private void UnderlineButton_Click(object sender, RoutedEventArgs e)
    {
        var newDeco = UnderlineButton.IsChecked == true ? TextDecorations.Underline : null;
        BodyBox.Selection.ApplyPropertyValue(Inline.TextDecorationsProperty, newDeco);
        BodyBox.Focus();
    }

    private void FontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _sticker.FontFamily = FontFamilyBox.SelectedValue as string ?? "";
        SaveContent();
        _debounce.OnChanged(_sticker);
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
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

    private void LoadBody()
    {
        _loading = true;
        try
        {
            if (!string.IsNullOrEmpty(_sticker.BodyRtf))
            {
                using var ms = new System.IO.MemoryStream(Encoding.ASCII.GetBytes(_sticker.BodyRtf));
                new TextRange(BodyBox.Document.ContentStart, BodyBox.Document.ContentEnd)
                    .Load(ms, DataFormats.Rtf);
            }
            else if (!string.IsNullOrEmpty(_sticker.Body))
            {
                BodyBox.Document.Blocks.Clear();
                BodyBox.Document.Blocks.Add(new Paragraph(new Run(_sticker.Body)));
            }

            if (!string.IsNullOrEmpty(_sticker.FontFamily))
                FontFamilyBox.SelectedValue = _sticker.FontFamily;
        }
        finally
        {
            _loading = false;
        }
        BodyPlaceholder.Visibility = IsBodyEmpty() ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveBodyContent()
    {
        var range = new TextRange(BodyBox.Document.ContentStart, BodyBox.Document.ContentEnd);

        using var rtfMs = new System.IO.MemoryStream();
        range.Save(rtfMs, DataFormats.Rtf);
        _sticker.BodyRtf = Encoding.ASCII.GetString(rtfMs.ToArray());

        _sticker.Body = range.Text;
    }

    private bool IsBodyEmpty()
    {
        var range = new TextRange(BodyBox.Document.ContentStart, BodyBox.Document.ContentEnd);
        return string.IsNullOrWhiteSpace(range.Text);
    }

    private void SaveContent()
    {
        SaveBodyContent();
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

    private void SyncFormattingButtons()
    {
        var weight = BodyBox.Selection.GetPropertyValue(TextElement.FontWeightProperty);
        BoldButton.IsChecked = weight is FontWeight fw && fw == FontWeights.Bold;

        var deco = BodyBox.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        UnderlineButton.IsChecked = deco is TextDecorationCollection tdc &&
                                    tdc.Count > 0 &&
                                    tdc[0].Location == TextDecorationLocation.Underline;
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

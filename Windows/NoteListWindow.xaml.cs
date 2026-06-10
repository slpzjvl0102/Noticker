using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Noticker.Data;
using Noticker.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace Noticker.Windows;

public partial class NoteListWindow : Window
{
    private readonly StickerRepository _repo;
    private readonly System.ComponentModel.PropertyChangedEventHandler _themeHandler;
    private List<NoteItem> _allItems = [];
    private bool _needsRefresh = false;

    // 카드 색은 NoteItem에 브러시로 박아 바인딩으로 그린다 — 테마 전환 시 Refresh()로
    // 아이템을 재생성하므로 디스패처 후처리(구 ApplyRowColors)가 필요 없다.
    private static readonly ThemePalette _lightPalette = new(
        WinBg: new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
        CardBg: Brushes.White,
        CardBorder: new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)),
        HoverBorder: new SolidColorBrush(Color.FromRgb(0xD5, 0xD5, 0xD5)),
        TitleFg: new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
        MutedFg: new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
        BadgeBg: new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
        BadgeFg: new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        LineBorder: new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));

    private static readonly ThemePalette _darkPalette = new(
        WinBg: new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
        CardBg: new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
        CardBorder: new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
        HoverBorder: new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)),
        TitleFg: Brushes.White,
        MutedFg: new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
        BadgeBg: new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
        BadgeFg: new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
        LineBorder: new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)));

    private static readonly DropShadowEffect _hoverShadow = new()
    {
        BlurRadius = 4,
        ShadowDepth = 1,
        Opacity = 0.12
    };

    static NoteListWindow()
    {
        foreach (var p in new[] { _lightPalette, _darkPalette })
            foreach (var b in new[] { p.WinBg, p.CardBg, p.CardBorder, p.HoverBorder,
                                      p.TitleFg, p.MutedFg, p.BadgeBg, p.BadgeFg, p.LineBorder })
                if (b.CanFreeze) b.Freeze();
        if (_hoverShadow.CanFreeze) _hoverShadow.Freeze();
    }

    private static ThemePalette Palette =>
        AppSettings.Instance.ColorSwapped ? _darkPalette : _lightPalette;

    public NoteListWindow(StickerRepository repo)
    {
        _repo = repo;
        InitializeComponent();
        ApplyTheme();
        _themeHandler = (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ColorSwapped))
                ApplyTheme();
        };
        AppSettings.Instance.PropertyChanged += _themeHandler;
        Refresh();
    }

    private void ApplyTheme()
    {
        var p = Palette;

        RootPanel.Background = p.WinBg;
        NoteList.Background = p.WinBg;
        NoteList.Foreground = p.TitleFg;

        SearchBorder.BorderBrush = p.LineBorder;
        ImportBorder.BorderBrush = p.LineBorder;
        SearchBox.Foreground = p.TitleFg;
        SearchBox.BorderBrush = p.LineBorder;
        SearchBox.CaretBrush = p.TitleFg;
        SearchPlaceholder.Foreground = p.MutedFg;
        EmptyLabel.Foreground = p.MutedFg;
        ImportButton.Foreground = p.MutedFg;
        ImportButton.BorderBrush = p.CardBorder;

        // 카드 브러시는 아이템에 박혀 있어 재생성 필요
        if (_allItems.Count > 0) Refresh();
    }

    private void Refresh()
    {
        var p = Palette;
        _allItems = _repo.GetAllSummary()
            .Select(t => NoteItem.From(t.Id, t.Title, t.Body, t.UpdatedAt, t.IsHidden, p))
            .ToList();
        _needsRefresh = false;
        ApplyFilter();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_needsRefresh) Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        AppSettings.Instance.PropertyChanged -= _themeHandler;
        base.OnClosed(e);
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allItems
            : _allItems.Where(i =>
                i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        NoteList.ItemsSource = filtered;

        if (filtered.Count == 0)
        {
            EmptyLabel.Text = q.Length > 0
                ? "검색 결과가 없습니다."
                : "메모가 없습니다. 트레이를 우클릭해 새 스티커를 만드세요.";
            EmptyLabel.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyLabel.Visibility = Visibility.Collapsed;
        }
    }

    // hover 비주얼(테두리/그림자/✕)은 코드로 — DataTemplate 트리거의 Setter에는
    // Binding을 쓸 수 없어 테마별 브러시를 줄 수 없다.
    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Border card || card.DataContext is not NoteItem item) return;
        card.BorderBrush = item.HoverBorderBrush;
        card.Effect = _hoverShadow;
        if (FindVisualChild<Button>(card, "DeleteX") is { } del)
            del.Visibility = Visibility.Visible;
    }

    // 주의: 로컬 BorderBrush 대입은 XAML 바인딩을 덮는다 — Standard 가상화(컨테이너 재생성)
    // 전제. VirtualizationMode=Recycling을 켜려면 ClearValue(BorderBrushProperty)로 복원할 것.
    private void Card_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Border card || card.DataContext is not NoteItem item) return;
        card.BorderBrush = item.CardBorderBrush;
        card.Effect = null;
        if (FindVisualChild<Button>(card, "DeleteX") is { } del)
            del.Visibility = Visibility.Hidden;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var found = FindVisualChild<T>(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NoteList.SelectedItem is NoteItem item)
        {
            App.Current.ShowSticker(item.Id);
            _needsRefresh = true;
            NoteList.SelectedItem = null;
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.OpenNotionImport();
        _needsRefresh = true;   // 가져온 스티커가 목록에 보이도록
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var result = MessageBox.Show(
                "이 메모를 삭제할까요?\nNotion에 동기화된 내용은 그대로 유지됩니다.",
                "메모 삭제",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                App.Current.DeleteSticker(id);
                Refresh();
            }
        }
    }

    private record ThemePalette(
        Brush WinBg, Brush CardBg, Brush CardBorder, Brush HoverBorder,
        Brush TitleFg, Brush MutedFg, Brush BadgeBg, Brush BadgeFg, Brush LineBorder);

    private record NoteItem(
        string Id, string Title, string DateLabel, string IsHiddenBadge,
        Brush CardBg, Brush CardBorderBrush, Brush HoverBorderBrush,
        Brush TitleFg, Brush MutedFg, Brush BadgeBg, Brush BadgeFg)
    {
        public static NoteItem From(string id, string title, string body, string updatedAt,
            bool isHidden, ThemePalette p)
        {
            var displayTitle = !string.IsNullOrWhiteSpace(title) ? title : "(제목 없음)";
            var dateLabel = DateTime.TryParse(updatedAt, out var dt)
                ? dt.ToLocalTime().ToString("yyyy년 M월 d일")
                : "";
            return new NoteItem(id, displayTitle, dateLabel,
                isHidden ? "Visible" : "Collapsed",
                p.CardBg, p.CardBorder, p.HoverBorder,
                p.TitleFg, p.MutedFg, p.BadgeBg, p.BadgeFg);
        }
    }
}

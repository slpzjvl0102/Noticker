using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Noticker.Data;
using Noticker.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using ListViewItem = System.Windows.Controls.ListViewItem;
using MessageBox = System.Windows.MessageBox;

namespace Noticker.Windows;

public partial class NoteListWindow : Window
{
    private readonly StickerRepository _repo;
    private List<NoteItem> _allItems = [];
    private bool _needsRefresh = false;

    private static readonly SolidColorBrush _dark = new(Color.FromRgb(0x33, 0x33, 0x33));
    private static readonly SolidColorBrush _darkBorder = new(Color.FromRgb(0x50, 0x50, 0x50));
    private static readonly SolidColorBrush _darkRow = new(Color.FromRgb(0x44, 0x44, 0x44));
    private static readonly SolidColorBrush _mutedDark = new(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly SolidColorBrush _mutedLight = new(Color.FromRgb(0xAA, 0xAA, 0xAA));

    public NoteListWindow(StickerRepository repo)
    {
        _repo = repo;
        InitializeComponent();
        ApplyTheme();
        AppSettings.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ColorSwapped))
                ApplyTheme();
        };
        Refresh();
    }

    private void ApplyTheme()
    {
        bool dark = AppSettings.Instance.ColorSwapped;

        var bg = dark ? _dark : Brushes.White;
        var fg = dark ? Brushes.White : Brushes.Black;
        var borderColor = dark ? _darkBorder : new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));
        var rowBorderColor = dark ? _darkRow : new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
        var mutedFg = dark ? _mutedDark : _mutedLight;

        RootPanel.Background = bg;
        NoteList.Background = bg;
        NoteList.Foreground = fg;

        SearchBorder.BorderBrush = borderColor;
        ImportBorder.BorderBrush = borderColor;
        SearchBox.Background = dark ? new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)) : Brushes.White;
        SearchBox.Foreground = fg;
        SearchBox.BorderBrush = borderColor;
        SearchBox.CaretBrush = fg;
        SearchPlaceholder.Foreground = mutedFg;
        EmptyLabel.Foreground = mutedFg;

        // Re-apply row colors via item template tag
        _rowBorderColor = rowBorderColor;
        _textFg = fg;
        _mutedFg = mutedFg;

        // Refresh displayed items to pick up new colors
        if (_allItems.Count > 0) ApplyFilter();
    }

    private Brush _rowBorderColor = new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));
    private Brush _textFg = Brushes.Black;
    private Brush _mutedFg = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

    private void Refresh()
    {
        _allItems = _repo.GetAllSummary()
            .Select(t => NoteItem.From(t.Id, t.Title, t.Body, t.UpdatedAt, t.IsHidden))
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
        AppSettings.Instance.PropertyChanged -= null;
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

        // Apply row colors after binding
        Dispatcher.InvokeAsync(() => ApplyRowColors(), System.Windows.Threading.DispatcherPriority.Loaded);

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

    private void ApplyRowColors()
    {
        bool dark = AppSettings.Instance.ColorSwapped;
        foreach (var item in NoteList.Items)
        {
            var container = NoteList.ItemContainerGenerator.ContainerFromItem(item) as ListViewItem;
            if (container == null) continue;

            var rowBorder = FindVisualChild<Border>(container, "RowBorder");
            if (rowBorder != null)
            {
                rowBorder.BorderBrush = _rowBorderColor;
            }

            // Title label (주 내용)
            var titleLabel = FindVisualChild<TextBlock>(container, "TitleLabel");
            if (titleLabel != null) titleLabel.Foreground = _textFg;

            // Date label
            var dateLabel = FindVisualChild<TextBlock>(container, "DateLabel");
            if (dateLabel != null) dateLabel.Foreground = _mutedFg;
        }
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

    private record NoteItem(string Id, string Title, string DateLabel, string IsHiddenBadge)
    {
        public static NoteItem From(string id, string title, string body, string updatedAt, bool isHidden)
        {
            var displayTitle = !string.IsNullOrWhiteSpace(title) ? title : "(제목 없음)";
            var dateLabel = DateTime.TryParse(updatedAt, out var dt)
                ? dt.ToLocalTime().ToString("yyyy년 M월 d일")
                : "";
            return new NoteItem(id, displayTitle, dateLabel, isHidden ? "Visible" : "Collapsed");
        }
    }
}

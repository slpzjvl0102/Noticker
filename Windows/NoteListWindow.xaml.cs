using System.Windows;
using System.Windows.Controls;
using Noticker.Data;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace Noticker.Windows;

public partial class NoteListWindow : Window
{
    private readonly StickerRepository _repo;
    private List<NoteItem> _allItems = [];
    private bool _needsRefresh = false;

    public NoteListWindow(StickerRepository repo)
    {
        _repo = repo;
        InitializeComponent();
        Refresh();
    }

    private void Refresh()
    {
        _allItems = _repo.GetAllSummary()
            .Select(t => NoteItem.From(t.Id, t.Body, t.UpdatedAt, t.IsHidden))
            .ToList();
        _needsRefresh = false;
        ApplyFilter();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_needsRefresh) Refresh();
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allItems
            : _allItems.Where(i => i.Body.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        NoteList.ItemsSource = filtered;

        if (filtered.Count == 0)
        {
            EmptyLabel.Text = q.Length > 0 ? "검색 결과가 없습니다." : "메모가 없습니다. 트레이를 우클릭해 새 스티커를 만드세요.";
            EmptyLabel.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyLabel.Visibility = Visibility.Collapsed;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        ApplyFilter();

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NoteList.SelectedItem is NoteItem item)
        {
            App.Current.ShowSticker(item.Id);
            _needsRefresh = true;
            NoteList.SelectedItem = null;
        }
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

    private record NoteItem(string Id, string Body, string DateLabel, string IsHiddenBadge)
    {
        public static NoteItem From(string id, string body, string updatedAt, bool isHidden) => new(
            id,
            string.IsNullOrWhiteSpace(body) ? "(빈 메모)" : body,
            DateTime.TryParse(updatedAt, out var dt)
                ? dt.ToLocalTime().ToString("yyyy년 M월 d일") : "",
            isHidden ? "Visible" : "Collapsed");
    }
}

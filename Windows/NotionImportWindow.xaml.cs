using System.Windows;
using System.Windows.Controls;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Noticker.Data;
using Noticker.Models;
using Noticker.Sync;

namespace Noticker.Windows;

// 미연결 Notion 페이지 → 스티커 가져오기 (opt-in — 전체 자동 생성 금지가 설계 핵심).
// 어휘 검사는 선택 시점에 1회 blocks GET (목록 단계의 페이지당 GET은 비용 과다)
public partial class NotionImportWindow : Window
{
    private readonly NotionClient _client;
    private readonly StickerRepository _repo;
    private bool _importing;

    public NotionImportWindow(NotionClient client, StickerRepository repo)
    {
        _client = client;
        _repo = repo;
        InitializeComponent();
        ApplyTheme();
        Loaded += async (_, _) => await LoadPagesAsync();
    }

    private void ApplyTheme()
    {
        bool dark = AppSettings.Instance.ColorSwapped;
        var bg = dark ? new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)) : Brushes.White;
        var fg = dark ? Brushes.White : Brushes.Black;
        var muted = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
        var border = dark
            ? new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50))
            : new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD));

        RootPanel.Background = bg;
        PageList.Background = bg;
        PageList.Foreground = fg;
        HeaderBorder.BorderBrush = border;
        HeaderLabel.Foreground = muted;
        StatusLabel.Foreground = muted;
    }

    private async Task LoadPagesAsync()
    {
        try
        {
            var linked = _repo.GetAll()
                .Where(s => s.NotionPageId is not null)
                .Select(s => s.NotionPageId!)
                .ToHashSet();

            // cursor null = 필터 없는 전체 쿼리 (최대 300 — client 캡, 설계 수용 한계)
            var pages = await _client.QueryUpdatedPagesAsync(null, CancellationToken.None);
            var items = pages
                .Where(p => !linked.Contains(p.PageId))
                .OrderByDescending(p => p.LastEditedTime)   // 최근 수정 순
                .Select(p => ImportItem.From(p.PageId, p.LastEditedTime, p.LastEditedById, p.Title))
                .ToList();

            PageList.ItemsSource = items;
            StatusLabel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            StatusLabel.Text = "가져올 페이지가 없습니다 — 모든 페이지가 이미 스티커와 연결되어 있습니다.";
        }
        catch
        {
            StatusLabel.Visibility = Visibility.Visible;
            StatusLabel.Text = "Notion에서 페이지 목록을 불러오지 못했습니다. 연결 상태를 확인하세요.";
        }
    }

    private async void PageList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_importing || PageList.SelectedItem is not ImportItem item) return;
        PageList.SelectedItem = null;
        _importing = true;
        try
        {
            await ImportAsync(item);
        }
        finally
        {
            _importing = false;
        }
    }

    private async Task ImportAsync(ImportItem item)
    {
        var blocks = await _client.GetPageBlocksAsync(item.PageId, CancellationToken.None);
        if (blocks is null)
        {
            MessageBox.Show("페이지를 읽을 수 없습니다 (삭제되었거나 권한 없음).",
                "가져오기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var (supported, unsupportedType) = NotionBlockConverter.CheckVocabulary(blocks.Value);
        bool pullDisabled = false;
        if (!supported)
        {
            // 범위 밖 서식 경고 — 동의해야 진행 (설계 premise 4, 사용자 확정 요구사항)
            var result = MessageBox.Show(
                $"이 페이지에는 지원되지 않는 서식이 있습니다 ({unsupportedType}).\n" +
                "지원되는 텍스트만 가져오며, 이후 스티커를 수정하는 순간 " +
                "Notion의 원본 서식이 삭제됩니다.\n\n계속할까요?",
                "서식 경고", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            pullDisabled = true;   // 이후 pull로 재훼손 방지
        }

        var lines = NotionBlockConverter.ToLines(blocks.Value);
        var plain = NotionBlockConverter.ToPlainText(lines);
        var rtf = RtfComposer.Compose(lines);

        App.Current.CreateImportedSticker(
            item.Title, plain, rtf, item.PageId, item.LastEditedTime, item.LastEditedById, pullDisabled);

        // 목록에서 제거
        if (PageList.ItemsSource is List<ImportItem> list)
        {
            list.Remove(item);
            PageList.Items.Refresh();
            if (list.Count == 0)
            {
                StatusLabel.Visibility = Visibility.Visible;
                StatusLabel.Text = "가져올 페이지가 없습니다.";
            }
        }
    }

    private record ImportItem(string PageId, string LastEditedTime, string LastEditedById,
        string Title, string DateLabel)
    {
        public static ImportItem From(string pageId, string lastEditedTime, string lastEditedBy, string title)
        {
            var displayTitle = string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : title;
            var dateLabel = DateTime.TryParse(lastEditedTime, out var dt)
                ? dt.ToLocalTime().ToString("yyyy년 M월 d일 HH:mm")
                : "";
            return new ImportItem(pageId, lastEditedTime, lastEditedBy, displayTitle, dateLabel);
        }
    }
}

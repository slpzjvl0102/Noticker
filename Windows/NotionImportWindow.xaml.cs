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
        if (_importing)
        {
            // 가져오기 진행 중의 클릭도 선택을 비워야 같은 행을 다시 클릭할 수 있음
            PageList.SelectedItem = null;
            return;
        }
        if (PageList.SelectedItem is not ImportItem item) return;
        PageList.SelectedItem = null;
        _importing = true;
        try
        {
            await ImportAsync(item);
        }
        catch (Exception)
        {
            // async void에서 새는 예외는 프로세스를 죽임 (오프라인 HttpRequestException 등)
            MessageBox.Show("가져오기 실패 — 연결 상태를 확인하세요.",
                "가져오기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _importing = false;
        }
    }

    private async Task ImportAsync(ImportItem item)
    {
        var result0 = await _client.GetPageBlocksAsync(item.PageId, CancellationToken.None);
        if (result0 is null)
        {
            MessageBox.Show("페이지를 읽을 수 없습니다 (삭제되었거나 권한 없음).",
                "가져오기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var (blocks, hasMore) = result0.Value;

        var (supported, unsupportedType) = NotionBlockConverter.CheckVocabulary(blocks);
        bool pullDisabled = false;
        if (!supported || hasMore)
        {
            // 범위 밖 서식/100블록 초과 경고 — 동의해야 진행 (설계 premise 4).
            // 100블록 초과는 잘린 본문을 push할 때 Notion의 초과 블록이 파괴되는 비대칭
            var reason = hasMore ? "100블록 초과" : unsupportedType;
            var result = MessageBox.Show(
                $"이 페이지에는 지원되지 않는 내용이 있습니다 ({reason}).\n" +
                "지원되는 텍스트만 가져오며, 이후 스티커를 수정하는 순간 " +
                "Notion의 원본 내용이 스티커 내용으로 대체됩니다.\n\n계속할까요?",
                "서식 경고", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            pullDisabled = true;   // 이후 pull로 재훼손 방지
        }
        else if (NotionBlockConverter.HasUnsupportedAnnotations(blocks))
        {
            // 블록 종류는 지원 범위지만 왕복 불가 서식이 있음 — 굵게/밑줄은 이제
            // annotation push로 보존되므로 그 외 서식만 동의 게이트
            var result = MessageBox.Show(
                "이 페이지에는 스티커가 지원하지 않는 글자 서식(기울임/취소선/코드/색상)이 있습니다.\n" +
                "가져온 뒤 스티커를 수정하면 해당 서식이 사라질 수 있습니다. " +
                "(굵게/밑줄은 유지됩니다)\n\n계속할까요?",
                "서식 경고", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }

        var lines = NotionBlockConverter.ToLines(blocks);
        var plain = NotionBlockConverter.ToPlainText(lines);
        var rtf = RtfComposer.Compose(lines);
        var runsJson = NoteLineSerializer.Serialize(lines);

        // RawTitle 사용 — DisplayTitle("(제목 없음)")을 저장하면 첫 push가 Notion 페이지
        // 제목을 placeholder로 바꿔버린다 (검증 리뷰 F4)
        App.Current.CreateImportedSticker(
            item.RawTitle, plain, rtf, runsJson, item.PageId, item.LastEditedTime, item.LastEditedById, pullDisabled);

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

    // Title은 표시용, RawTitle은 저장용 — placeholder가 데이터로 새지 않도록 분리
    private record ImportItem(string PageId, string LastEditedTime, string LastEditedById,
        string RawTitle, string Title, string DateLabel)
    {
        public static ImportItem From(string pageId, string lastEditedTime, string lastEditedBy, string title)
        {
            var displayTitle = string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : title;
            var dateLabel = DateTime.TryParse(lastEditedTime, out var dt)
                ? dt.ToLocalTime().ToString("yyyy년 M월 d일 HH:mm")
                : "";
            return new ImportItem(pageId, lastEditedTime, lastEditedBy, title, displayTitle, dateLabel);
        }
    }
}

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
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Noticker.Data;
using Noticker.Infrastructure;
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
    private bool _realClose = false;

    public Sticker Sticker => _sticker;
    public event EventHandler? RealClosed;

    // pull의 dirty 가드용 — debounce 대기 중이면 pull이 이 스티커를 건너뜀
    public bool IsSyncPending => _debounce.IsPending;

    // 백그라운드 sync 전이(conflict 등) 후 App이 dispatcher에서 점/툴팁 갱신용으로 호출
    public void RefreshSyncIndicator() => UpdateSyncIndicator();

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

        // IME 조합 상태 추적 — 조합 중엔 문서 직렬화/셀렉션 읽기를 차단한다(아래 핸들러 참고).
        // RichTextBox가 내부에서 이벤트를 handled 처리하므로 handledEventsToo=true로 받는다.
        BodyBox.AddHandler(TextCompositionManager.TextInputStartEvent,
            new TextCompositionEventHandler(BodyBox_TextInputStart), true);
        BodyBox.AddHandler(TextCompositionManager.TextInputUpdateEvent,
            new TextCompositionEventHandler(BodyBox_TextInputUpdate), true);
        BodyBox.AddHandler(TextCompositionManager.TextInputEvent,
            new TextCompositionEventHandler(BodyBox_TextInput), true);
        BodyBox.LostKeyboardFocus += (_, _) => { _imeComposing = false; FlushPendingBodySave(); };

        // Fix paragraph spacing via FlowDocument.Resources (more reliable than RichTextBox.Resources)
        var paraStyle = new Style(typeof(Paragraph));
        paraStyle.Setters.Add(new Setter(Paragraph.MarginProperty, new Thickness(0)));
        BodyBox.Document.Resources[typeof(Paragraph)] = paraStyle;

        // Fix bullet list indent — Document.Resources style covers newly created lists
        var listStyle = new Style(typeof(System.Windows.Documents.List));
        listStyle.Setters.Add(new Setter(System.Windows.Documents.List.MarginProperty, new Thickness(20, 0, 0, 0)));
        listStyle.Setters.Add(new Setter(System.Windows.Documents.List.PaddingProperty, new Thickness(0)));
        BodyBox.Document.Resources[typeof(System.Windows.Documents.List)] = listStyle;

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
        .Select(f => f.FamilyNames.TryGetValue(
            System.Windows.Markup.XmlLanguage.GetLanguage("en-US"), out var n) ? n : f.Source)
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
        }
    }

    // ── Colors (driven by AppSettings.ColorSwapped + category) ───────────────

    private bool Swapped => AppSettings.Instance.ColorSwapped;

    private static readonly SolidColorBrush _darkGray = new(Color.FromRgb(0x33, 0x33, 0x33));

    private SolidColorBrush? CategoryBarBrush()
    {
        var cat = _sticker.Category;
        if (cat is null) return null;
        var colors = AppSettings.Instance.CategoryColors;
        if (colors.TryGetValue(cat, out var notionColor) &&
            NotionColorPalette.Bar(notionColor) is Color wpfColor)
            return new SolidColorBrush(wpfColor);
        return null;
    }

    public Brush TitleBackground => CategoryBarBrush() ?? (Swapped ? Brushes.White : _darkGray);
    public Brush TitleForeground => CategoryBarBrush() is not null ? Brushes.White
                                    : (Swapped ? Brushes.Black : Brushes.White);
    public Brush BodyBackground => Swapped ? _darkGray : Brushes.White;
    public Brush BodyForeground => Swapped ? Brushes.White : Brushes.Black;

    // ── Sync indicator ─────────────────────────────────────────────────────────

    // 빈 메모는 push 대상이 아니라 'pending' 주황이 영원히 남는다 — 회색으로 사실을 표시 (D9).
    // SyncQueue.ProcessAsync skip 조건(Title/Body 빈)의 부분집합: NotionPageId가 있는
    // 빈 스티커(동기화 후 내용 삭제)는 기존 상태색을 유지한다 — 원격에 내용이 남아 있으므로
    private bool IsEmptyUnsynced =>
        _sticker.NotionPageId is null &&
        string.IsNullOrEmpty(_sticker.Title) && string.IsNullOrEmpty(_sticker.Body);

    public Brush SyncDotColor => IsEmptyUnsynced
        ? new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF))
        : _sticker.SyncState switch
        {
            "synced"   => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
            "failed"   => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            "conflict" => new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            _          => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        };

    public string SyncTooltip => IsEmptyUnsynced
        ? "빈 메모 — 동기화 대상 아님"
        : _sticker.SyncState switch
        {
            "synced"   => $"동기화됨 ({_sticker.LastSyncedAt?[..10] ?? ""})"
                          + (_sticker.PullDisabled ? "\nNotion 서식 미지원 — 가져오기 중단됨" : ""),
            "failed"   => "동기화 실패 (수동 Sync로 재시도)",
            "conflict" => "Notion과 충돌 — 수정하면 스티커 버전이 push됩니다",
            _          => "동기화 대기 중…",
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

    private bool _imeComposing;
    private bool _bodyUpdatePending;

    private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        // IME 조합 진행 중에는 문서를 만지지 않는다. SaveContent의 range.Save(Rtf)·TextRange 읽기가
        // 조합을 강제 확정시켜 복합 음절(쌍모음·겹받침)을 깨뜨린다(공백 뒤 글자 탈락 포함).
        // 조합이 commit(TextInput)될 때 일괄 반영한다. (dotnet/wpf#7397 환경에서의 우회)
        if (_imeComposing) { _bodyUpdatePending = true; return; }
        RunBodyUpdate();
    }

    // 조합이 끝났거나 일반(비-IME) 입력일 때만 실제 문서 작업 수행 — 조합 중엔 절대 호출 안 함
    private void RunBodyUpdate()
    {
        _bodyUpdatePending = false;
        // Normalize any newly created List blocks (Document.Resources may not catch all cases)
        foreach (var block in BodyBox.Document.Blocks)
            if (block is System.Windows.Documents.List lst && lst.Margin.Left != 20)
            {
                lst.Margin = new Thickness(20, 0, 0, 0);
                lst.Padding = new Thickness(0);
            }
        SaveContent();
        UpdateCharCounter();
        _debounce.OnChanged(_sticker);
        BodyPlaceholder.Visibility = IsBodyEmpty() ? Visibility.Visible : Visibility.Collapsed;
        SyncFormattingButtons();
    }

    // ── IME 조합 상태 추적 ──
    // 모던 한글 IME는 자모 입력마다 조합을 갱신한다. 조합 중 문서 직렬화/셀렉션 읽기는 조합을
    // 깨므로, 조합 시작~갱신 동안 부수효과를 차단(_imeComposing)하고 commit 시 일괄 반영한다.
    private void BodyBox_TextInputStart(object sender, TextCompositionEventArgs e) => _imeComposing = true;
    private void BodyBox_TextInputUpdate(object sender, TextCompositionEventArgs e) => _imeComposing = true;
    private void BodyBox_TextInput(object sender, TextCompositionEventArgs e)
    {
        _imeComposing = false;
        // commit 직후 트레일링 TextChanged가 보통 따라오지만, 없을 수도 있으니 pending이면 직접 실행
        if (_bodyUpdatePending) RunBodyUpdate();
    }

    // 닫기/종료/포커스 이탈 시 조합 중 미반영분이 남아 있으면 동기 flush
    private void FlushPendingBodySave()
    {
        if (_bodyUpdatePending) RunBodyUpdate();
    }

    // 전역 단축키 생성 경로 — 즉시 타이핑 가능하게 본문에 포커스 (App.OnHotkeyPressed)
    public void FocusBody() => BodyBox.Focus();

    private void BodyBox_SelectionChanged(object sender, RoutedEventArgs e)
    {
        // 조합 중에는 캐럿이 자모마다 움직여 반복 발화한다. SyncFormattingButtons가 Selection/
        // CaretPosition을 읽으면 조합을 확정·교란하므로 차단 — 조합 종료 후 RunBodyUpdate가
        // 버튼 상태를 맞춘다. (조합이 아닐 땐 즉시 갱신해 툴바 반응성 유지)
        if (_imeComposing) return;
        SyncFormattingButtons();
    }

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

    private void BulletButton_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleBullets.Execute(null, BodyBox);
        BodyBox.Focus();
    }

    private void NumberButton_Click(object sender, RoutedEventArgs e)
    {
        EditingCommands.ToggleNumbering.Execute(null, BodyBox);
        BodyBox.Focus();
    }

    // Pull 적용 — PullService(UI 스레드)에서 호출. 호출 전 PullService가 _sticker의
    // NotionLastEdit/By 쌍을 갱신해 두면 여기의 _repo.Update가 전체 행과 함께 영속한다.
    // _loading 가드로 TextChanged → pending 오염 방지
    public void ApplyPulledContent(string title, string plainBody, string bodyRtf, string? bodyRuns)
    {
        _loading = true;
        try
        {
            _sticker.Title = title;
            Notify(nameof(StickerTitle));
            _sticker.Body = plainBody;
            _sticker.BodyRtf = bodyRtf;
            _sticker.BodyRuns = bodyRuns;
            LoadBody();
            _sticker.SyncState = "synced";
            _sticker.RetryCount = 0;
            try { _repo.Update(_sticker); }
            catch { /* 다음 폴링/push가 재시도 */ }
            UpdateSyncIndicator();
        }
        finally
        {
            _loading = false;
        }
    }

    private void PomodoroButton_Click(object sender, RoutedEventArgs e)
    {
        // 웨지 색 = 이 스티커의 카테고리 색 (노션 색 이름). 카테고리/색 없으면 null → 마지막 색 유지
        string? colorKey = null;
        if (_sticker.Category is string cat &&
            AppSettings.Instance.CategoryColors.TryGetValue(cat, out var notionColor))
            colorKey = notionColor;
        App.Current.OpenPomodoro(colorKey);
    }

    private void BodyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab) { HandleListTab(e); return; }
        if (e.Key != Key.Space) return;
        var para = BodyBox.CaretPosition.Paragraph;
        if (para == null || para.Parent is ListItem) return;

        var text = new TextRange(para.ContentStart, BodyBox.CaretPosition).Text;

        if (text is "-" or "*")
        {
            new TextRange(para.ContentStart, BodyBox.CaretPosition).Text = "";
            EditingCommands.ToggleBullets.Execute(null, BodyBox);
            e.Handled = true;
        }
        else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+\.$"))
        {
            new TextRange(para.ContentStart, BodyBox.CaretPosition).Text = "";
            EditingCommands.ToggleNumbering.Execute(null, BodyBox);
            e.Handled = true;
        }
    }

    // Tab/Shift+Tab = 리스트 항목 강등/승격 (Notion식 아웃라이너).
    // WPF 내장 IncreaseIndentation/DecreaseIndentation이 "이전 형제 밑 중첩 / 첫 항목 no-op"을
    // 정확히 수행한다(TextRangeEditLists.IndentListItems). Tab을 기본 TabForward에 맡기면 빈
    // 셀렉션일 때 탭문자/빈 불릿이 생기므로 직접 실행 + Handled. (AcceptsTab=True 필요)
    private void HandleListTab(KeyEventArgs e)
    {
        var li = BodyBox.CaretPosition?.Paragraph?.Parent as ListItem;
        if (li == null) return;   // 리스트 밖 Tab은 기본 동작 유지

        bool numbered = NoteLineDocumentBuilder.IsNumberedStyle(li.List?.MarkerStyle);   // 강등 전 종류 기억
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            EditingCommands.DecreaseIndentation.Execute(null, BodyBox);
        else
            EditingCommands.IncreaseIndentation.Execute(null, BodyBox);

        // 강등이 만든 새 중첩 List의 종류를 원래대로 강제 (WPF 클론 동작 불확실 대비) —
        // 종류(불릿/번호)는 보존하고 스타일만 깊이로 사이클한다.
        if (BodyBox.CaretPosition?.Paragraph?.Parent is ListItem li2 && li2.List is { } newList &&
            NoteLineDocumentBuilder.IsNumberedStyle(newList.MarkerStyle) != numbered)
            newList.MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;

        FixListMarkersByDepth();
        RunBodyUpdate();          // 마커 변경은 TextChanged를 안 울리므로 보정 후 명시 저장
        e.Handled = true;
    }

    // 문서의 모든 List를 재귀로 내려가며 깊이별 마커(●→○→■ / 1.→a.→i.)·들여쓰기·마커크기 교정.
    // 종류는 List가 가진 것을 보존, 스타일만 깊이로 사이클. 마커 사이클은 NoteLineDocumentBuilder와 공유.
    private void FixListMarkersByDepth()
    {
        foreach (var block in BodyBox.Document.Blocks)
            if (block is System.Windows.Documents.List list)
                FixListMarker(list, 0);
    }

    private static void FixListMarker(System.Windows.Documents.List list, int depth)
    {
        // 마커 종류/크기·들여쓰기·마커 간격은 빌더와 동일한 ApplyListStyle로(공백 균일 + DRY).
        bool numbered = NoteLineDocumentBuilder.IsNumberedStyle(list.MarkerStyle);
        NoteLineDocumentBuilder.ApplyListStyle(list, depth, numbered);
        foreach (var item in list.ListItems)
            foreach (var inner in item.Blocks)
            {
                if (inner is Paragraph p) p.FontSize = 13;   // 텍스트는 본문 크기(마커 폰트와 분리)
                else if (inner is System.Windows.Documents.List nested) FixListMarker(nested, depth + 1);
            }
    }

    private void FontFamilyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var fontFamily = FontFamilyBox.SelectedValue as string ?? "";
        _sticker.FontFamily = fontFamily;
        BodyBox.Focus();
        if (!string.IsNullOrEmpty(fontFamily))
        {
            var ff = new System.Windows.Media.FontFamily(fontFamily);
            BodyBox.FontFamily = ff;
            BodyBox.Selection.ApplyPropertyValue(TextElement.FontFamilyProperty, ff);
        }
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

    public void CancelDebounce() => _debounce.Cancel();

    public void ForceClose()
    {
        _realClose = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // Triggers OnClosing hide path
        Close();
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        MenuButton.ContextMenu.PlacementTarget = MenuButton;
        MenuButton.ContextMenu.Placement = PlacementMode.Bottom;
        MenuButton.ContextMenu.IsOpen = true;
    }

    private void MenuItem_NewSticker_Click(object sender, RoutedEventArgs e)
    {
        App.Current.CreateSticker();
    }

    private void MenuItem_NoteList_Click(object sender, RoutedEventArgs e)
    {
        App.Current.OpenNoteList();
    }

    private void MenuItem_Delete_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "이 메모를 삭제할까요?\nNotion에 동기화된 내용은 그대로 유지됩니다.",
            "메모 삭제",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
            App.Current.DeleteSticker(_sticker.Id);
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double newW = Math.Max(MinWidth, Width + e.HorizontalChange);
        double newH = Math.Max(MinHeight, Height + e.VerticalChange);
        Width = newW;
        Height = newH;
        SaveSize();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        FlushPendingBodySave();
        if (App.Current.IsShuttingDown || _realClose)
        {
            _debounce.Cancel();
            AppSettings.Instance.PropertyChanged -= OnAppSettingsChanged;
            base.OnClosing(e);
            RealClosed?.Invoke(this, EventArgs.Empty);
            return;
        }
        // X / CloseButton → hide
        e.Cancel = true;
        _sticker.IsHidden = true;
        _repo.Update(_sticker);
        Hide();
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
            // 정본: BodyRuns(NoteLine+Depth) → 공용 빌더로 중첩 문서를 직접 구성.
            // RTF는 중첩 List를 신뢰성 있게 왕복 못 하므로, BodyRuns가 있으면 우선한다.
            var lines = NoteLineSerializer.Deserialize(_sticker.BodyRuns);
            bool builtFromRuns = false;
            if (lines is not null)
            {
                NoteLineDocumentBuilder.Populate(BodyBox.Document, lines);
                builtFromRuns = !RebuildLooksDegraded(lines);
                if (!builtFromRuns)
                    SyncLog.Write("load: bodyRuns 재구성 손실 의심 — RTF/plain 폴백 " +
                                  $"(sticker={_sticker.Id[..Math.Min(8, _sticker.Id.Length)]})");
            }

            if (!builtFromRuns)
            {
                // 폴백/구버전 행: RTF는 색·폰트·마진이 run에 박혀 들어오므로 정규화로
                // 테마 색·스티커 폰트를 상속받게 한다 (BodyRuns 경로는 빌더 출력이 이미 깨끗).
                LoadFromRtfOrPlain();
                NormalizeDocumentMargins();
                NormalizeInheritedFormatting();
            }
            // BodyRuns 경로는 정규화하지 않는다 — 정규화가 마커 크기용 FontSize까지 지운다.

            if (!string.IsNullOrEmpty(_sticker.FontFamily))
            {
                FontFamilyBox.SelectedValue = _sticker.FontFamily;
                BodyBox.FontFamily = new System.Windows.Media.FontFamily(_sticker.FontFamily);
            }
        }
        finally
        {
            _loading = false;
        }
        BodyPlaceholder.Visibility = IsBodyEmpty() ? Visibility.Visible : Visibility.Collapsed;
    }

    // BodyRuns로 만든 문서의 실제 텍스트가 기대치(run 텍스트 글자수 합)의 절반 미만이면 손상 의심.
    // 마커는 양쪽 다 텍스트에 없어 공정한 비교. 공백/개행 제외. (corpus 게이트의 런타임 백업)
    private bool RebuildLooksDegraded(IReadOnlyList<NoteLine> lines)
    {
        int expected = lines.Sum(l => l.Runs.Sum(r => r.Text.Count(c => !char.IsWhiteSpace(c))));
        if (expected == 0) return false;
        var actualText = new TextRange(BodyBox.Document.ContentStart, BodyBox.Document.ContentEnd).Text;
        int actual = actualText.Count(c => !char.IsWhiteSpace(c));
        return actual < expected / 2;
    }

    // BodyRuns 폴백/구버전 호환: RTF(있으면) → 실패 시 plain Body → 둘 다 없으면 빈 문서.
    private void LoadFromRtfOrPlain()
    {
        if (!string.IsNullOrEmpty(_sticker.BodyRtf))
        {
            try
            {
                using var ms = new System.IO.MemoryStream(Encoding.Latin1.GetBytes(_sticker.BodyRtf));
                new TextRange(BodyBox.Document.ContentStart, BodyBox.Document.ContentEnd)
                    .Load(ms, DataFormats.Rtf);
                return;
            }
            catch { }
        }
        BodyBox.Document.Blocks.Clear();
        if (!string.IsNullOrEmpty(_sticker.Body))
            BodyBox.Document.Blocks.Add(new Paragraph(new Run(_sticker.Body)));
    }

    private void NormalizeDocumentMargins()
    {
        foreach (var block in BodyBox.Document.Blocks)
        {
            block.ClearValue(Block.MarginProperty);
            if (block is System.Windows.Documents.List list)
            {
                list.Margin = new Thickness(20, 0, 0, 0);
                list.Padding = new Thickness(0);
                foreach (var item in list.ListItems)
                {
                    item.ClearValue(ListItem.MarginProperty);
                    foreach (var inner in item.Blocks)
                        inner.ClearValue(Block.MarginProperty);
                }
            }
        }
    }

    // 색/글꼴/크기의 명시값 제거 — Bold(FontWeight)/Underline(TextDecorations)은 보존
    private void NormalizeInheritedFormatting()
    {
        foreach (var block in BodyBox.Document.Blocks)
            NormalizeElement(block);
    }

    private static void NormalizeElement(TextElement el)
    {
        el.ClearValue(TextElement.ForegroundProperty);
        el.ClearValue(TextElement.FontFamilyProperty);
        el.ClearValue(TextElement.FontSizeProperty);

        switch (el)
        {
            case Paragraph p:
                foreach (var inline in p.Inlines) NormalizeElement(inline);
                break;
            case Span s:
                foreach (var inline in s.Inlines) NormalizeElement(inline);
                break;
            case System.Windows.Documents.List list:
                foreach (var item in list.ListItems)
                    foreach (var inner in item.Blocks)
                        NormalizeElement(inner);
                break;
        }
    }

    private void SaveBodyContent()
    {
        var range = new TextRange(BodyBox.Document.ContentStart, BodyBox.Document.ContentEnd);

        using var rtfMs = new System.IO.MemoryStream();
        range.Save(rtfMs, DataFormats.Rtf);
        _sticker.BodyRtf = Encoding.Latin1.GetString(rtfMs.ToArray());

        var lines = new List<string>();
        EmitPlainBlocks(BodyBox.Document.Blocks, lines);
        _sticker.Body = string.Join("\n", lines).TrimEnd('\n');
        // 같은 문서에서 run 단위 서식 + depth도 추출 — push가 굵게/밑줄·중첩을 보내도록
        _sticker.BodyRuns = NoteLineSerializer.Serialize(NoteLineExtractor.Extract(BodyBox.Document));
    }

    // plain Body 추출 — 중첩 포함, 깊이는 평탄(설계상 plain은 평면, 깊이는 BodyRuns가 정본).
    private static void EmitPlainBlocks(BlockCollection blocks, List<string> lines)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph para)
                lines.Add(new TextRange(para.ContentStart, para.ContentEnd).Text);
            else if (block is System.Windows.Documents.List list)
                EmitPlainList(list, lines);
        }
    }

    // 각 항목은 "• "/"N. " 접두. 마커가 텍스트에 샌 경우(RTF 왕복) 제거. 중첩 List는 재귀(평탄).
    private static void EmitPlainList(System.Windows.Documents.List list, List<string> lines)
    {
        bool numbered = NoteLineDocumentBuilder.IsNumberedStyle(list.MarkerStyle);
        int n = 1;
        foreach (var item in list.ListItems)
            foreach (var inner in item.Blocks)
            {
                if (inner is Paragraph innerPara)
                {
                    var text = new TextRange(innerPara.ContentStart, innerPara.ContentEnd).Text;
                    if (!numbered && text.Length > 0 && text[0] == '•')
                        text = text[1..].TrimStart('\t', ' ');
                    else if (numbered)
                    {
                        var m2 = System.Text.RegularExpressions.Regex.Match(text, @"^\d+[.)]\s");
                        if (m2.Success) text = text[m2.Length..];
                    }
                    lines.Add(numbered ? $"{n++}. {text}" : $"• {text}");
                }
                else if (inner is System.Windows.Documents.List nested)
                    EmitPlainList(nested, lines);
            }
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

        var para = BodyBox.CaretPosition.Paragraph;
        var listParent = para?.Parent;
        BulletButton.IsChecked = listParent is ListItem li1 &&
                                 li1.List?.MarkerStyle == TextMarkerStyle.Disc;
        NumberButton.IsChecked = listParent is ListItem li2 &&
                                 li2.List?.MarkerStyle == TextMarkerStyle.Decimal;
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

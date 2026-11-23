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
    // 본문 저장 디바운스 대기도 dirty로 본다 — 미저장 편집을 pull이 덮어쓰지 않도록
    public bool IsSyncPending => _debounce.IsPending || _contentSaveTimer?.IsEnabled == true;

    // 백그라운드 sync 전이(conflict 등) 후 App이 dispatcher에서 점/툴팁 갱신용으로 호출
    public void RefreshSyncIndicator() => UpdateSyncIndicator();

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public StickerWindow(Sticker sticker, StickerRepository repo, SyncQueue queue)
    {
        _sticker = sticker;
        _repo = repo;
        _debounce = new DebouncedSyncService(queue, FlushPendingContentSave);

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
        BodyBox.LostKeyboardFocus += (_, _) =>
        {
            _imeComposing = false;
            FlushPendingBodySave();
            FlushPendingContentSave();
        };

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
        ShowInTaskbar = AppSettings.Instance.ShowInTaskbar;

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
        set { _sticker.Title = value; NotifyTitle(); }
    }

    // 작업표시줄/Alt+Tab 라벨 — 제목 없는 스티커는 앱 이름
    public string WindowTitle =>
        string.IsNullOrWhiteSpace(_sticker.Title) ? "Noticker" : _sticker.Title;

    private void NotifyTitle()
    {
        Notify(nameof(StickerTitle));
        Notify(nameof(WindowTitle));
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

    // 상태색은 고정값 — 갱신마다 새 브러시를 만들지 않도록 Freeze된 공유 인스턴스 사용
    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly SolidColorBrush _dotEmpty    = Frozen(0x9C, 0xA3, 0xAF);
    private static readonly SolidColorBrush _dotSynced   = Frozen(0x22, 0xC5, 0x5E);
    private static readonly SolidColorBrush _dotFailed   = Frozen(0xEF, 0x44, 0x44);
    private static readonly SolidColorBrush _dotConflict = Frozen(0xFF, 0x8C, 0x00);
    private static readonly SolidColorBrush _dotPending  = Frozen(0xF5, 0x9E, 0x0B);

    public Brush SyncDotColor => IsEmptyUnsynced
        ? _dotEmpty
        : _sticker.SyncState switch
        {
            "synced"   => _dotSynced,
            "failed"   => _dotFailed,
            "conflict" => _dotConflict,
            _          => _dotPending,
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

    // 본문 글자수 캐시 — RefreshBodyTextState가 문서 텍스트를 한 번만 읽어 갱신한다.
    // (이전엔 키 입력마다 카운터 텍스트·표시 여부·placeholder가 각각 전체 문서를 다시 읽었음)
    private int _bodyTextLength;

    public string CharCounterText => $"{_bodyTextLength:N0}자";

    public Visibility CharCounterVisibility =>
        _bodyTextLength >= 1800 ? Visibility.Visible : Visibility.Collapsed;

    // ── Event handlers ─────────────────────────────────────────────────────────

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button) return;
        DragMove();
        _positionSaveTimer?.Stop();   // 드래그 종료 시점 위치를 즉시 확정 — 대기 중 저장은 불필요
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
        NotifyTitle();
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
        StickerListEditing.NormalizeTopLevelListIndent(BodyBox.Document);
        SaveContent();
        _debounce.OnChanged(_sticker);
        RefreshBodyTextState();
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
            _contentSaveTimer?.Stop();   // pull 내용이 정본 — 대기 중 로컬 직렬화는 폐기 (dirty 가드상 보통 비어 있음)
            _sticker.Title = title;
            NotifyTitle();
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
        if (e.Key == Key.Tab)
        {
            bool outdent = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
            if (StickerListEditing.TryIndentListItem(BodyBox, outdent))
            {
                RunBodyUpdate();      // 마커 변경은 TextChanged를 안 울리므로 보정 후 명시 저장
                e.Handled = true;
            }
            return;
        }
        if (e.Key == Key.Space && StickerListEditing.TryApplyMarkdownShortcut(BodyBox))
            e.Handled = true;
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
    }

    // 크기 저장은 리사이즈 종료 시 1회 — DragDelta마다 DB 행 전체를 쓰지 않도록
    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e) => SaveSize();

    protected override void OnClosing(CancelEventArgs e)
    {
        FlushPendingBodySave();
        FlushPendingContentSave(force: true);   // 닫기/종료는 조합 중이어도 확정 — 유실 방지 우선
        FlushPendingPositionSave();
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

    // 이동 중 LocationChanged는 픽셀마다 연속 발화한다 — 디바운스해 마지막 위치만 저장.
    // 대기 중 저장은 OnClosing에서 flush (숨김/종료 시 위치 유실 방지)
    private System.Windows.Threading.DispatcherTimer? _positionSaveTimer;

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (_loading) return;
        if (_positionSaveTimer is null)
        {
            _positionSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _positionSaveTimer.Tick += (_, _) => { _positionSaveTimer.Stop(); SavePosition(); };
        }
        _positionSaveTimer.Stop();
        _positionSaveTimer.Start();
    }

    private void FlushPendingPositionSave()
    {
        if (_positionSaveTimer is not { IsEnabled: true }) return;
        _positionSaveTimer.Stop();
        SavePosition();
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
                builtFromRuns = !StickerBodyCodec.RebuildLooksDegraded(BodyBox.Document, lines);
                if (!builtFromRuns)
                    SyncLog.Write("load: bodyRuns 재구성 손실 의심 — RTF/plain 폴백 " +
                                  $"(sticker={_sticker.Id[..Math.Min(8, _sticker.Id.Length)]})");
            }

            if (!builtFromRuns)
            {
                // 폴백/구버전 행: RTF는 색·폰트·마진이 run에 박혀 들어오므로 정규화로
                // 테마 색·스티커 폰트를 상속받게 한다 (BodyRuns 경로는 빌더 출력이 이미 깨끗).
                StickerBodyCodec.LoadFromRtfOrPlain(BodyBox.Document, _sticker.BodyRtf, _sticker.Body);
                StickerBodyCodec.NormalizeDocumentMargins(BodyBox.Document);
                StickerBodyCodec.NormalizeInheritedFormatting(BodyBox.Document);
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
        RefreshBodyTextState();
    }

    private void SaveBodyContent()
    {
        var body = StickerBodyCodec.Serialize(BodyBox.Document);
        _sticker.BodyRtf = body.Rtf;
        _sticker.Body = body.Plain;
        _sticker.BodyRuns = body.Runs;
    }

    // 문서 텍스트를 한 번 읽어 placeholder·글자수 카운터를 함께 갱신.
    // IME 조합 중엔 호출되지 않는다 (RunBodyUpdate/LoadBody 경로만 사용)
    private void RefreshBodyTextState()
    {
        var text = StickerBodyCodec.DocumentText(BodyBox.Document);
        _bodyTextLength = text.Length;
        BodyPlaceholder.Visibility = string.IsNullOrWhiteSpace(text) ? Visibility.Visible : Visibility.Collapsed;
        UpdateCharCounter();
    }

    // 편집 저장 — 상태(pending/UpdatedAt)와 점 색은 즉시, 비용이 큰 본문 직렬화(RTF·runs)와
    // DB 쓰기는 300ms 디바운스로 묶는다. 연속 타이핑 중 키마다 전체 문서를 직렬화하지 않기 위함.
    // 확정 시점: 타이머 만료, 본문 포커스 이탈, 창 닫기/숨김/종료, sync enqueue 직전(위 콜백).
    private System.Windows.Threading.DispatcherTimer? _contentSaveTimer;

    private void SaveContent()
    {
        _sticker.UpdatedAt = DateTime.UtcNow.ToString("O");
        _sticker.SyncState = "pending";
        UpdateSyncIndicator();

        if (_contentSaveTimer is null)
        {
            _contentSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _contentSaveTimer.Tick += (_, _) => FlushPendingContentSave();
        }
        _contentSaveTimer.Stop();
        _contentSaveTimer.Start();
    }

    private void FlushPendingContentSave() => FlushPendingContentSave(force: false);

    private void FlushPendingContentSave(bool force)
    {
        if (_contentSaveTimer is not { IsEnabled: true }) return;
        _contentSaveTimer.Stop();
        // IME 조합 중 직렬화는 조합을 깨뜨린다 (한글 복합 음절 손상) — 타이머를 재무장해 조합이
        // 끝난 뒤 저장. 직전 commit까지의 내용은 이미 문서에 있으므로 다음 tick에 함께 반영된다
        if (_imeComposing && !force) { _contentSaveTimer.Start(); return; }
        SaveBodyContent();
        try { _repo.Update(_sticker); }
        catch { /* logged inside repo */ }
        UpdateSyncIndicator();   // 빈 메모 판정(IsEmptyUnsynced)은 Body 확정 후 값 기준
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
        else if (e.PropertyName == nameof(AppSettings.ShowInTaskbar))
            ShowInTaskbar = AppSettings.Instance.ShowInTaskbar;
    }

    public void RefreshCategoryOptions()
    {
        Notify(nameof(CategoryOptions));
        Notify(nameof(HasCategoryOptions));
        UpdateBarColors();
    }
}

using System.IO;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using Noticker.Data;
using Noticker.Infrastructure;
using Noticker.Models;
using Noticker.Services;
using Noticker.Sync;
using Noticker.Windows;

namespace Noticker;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private NotifyIcon? _trayIcon;

    public StickerRepository? StickerRepo { get; private set; }
    public SettingsRepository? SettingsRepo { get; private set; }
    public SyncQueue? SyncQueue { get; private set; }
    private NotionClient? _notionClient;
    private PullService? _pullService;
    private CancellationTokenSource _cts = new();
    private System.Windows.Threading.DispatcherTimer? _retryTimer;

    private readonly Dictionary<string, StickerWindow> _stickerWindows = [];
    public bool IsShuttingDown { get; private set; }

    // 포모도로 — App이 composition root (서비스/타이머 소유, 알림 side-effect 수행)
    private PomodoroService? _pomodoro;
    private System.Windows.Threading.DispatcherTimer? _pomodoroTimer;
    private PomodoroWindow? _pomodoroWindow;
    private string? _lastTrayTooltip;
    private string? _pomodoroWedgeColorKey;            // 노션 색 이름 — Idle일 때만 갱신 (색 잠금)

    // 트레이 미니 웨지 — 타이머 동작 중 아이콘 교체, Idle이면 기본 아이콘 복원
    private System.Drawing.Icon? _baseTrayIcon;
    private System.Drawing.Icon? _wedgeTrayIcon;
    private (int Bucket, string? Key, bool Active) _trayWedgeKey = (-1, null, false);

    public static new App Current => (App)System.Windows.Application.Current;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Single instance guard
        _mutex = new Mutex(true, "Noticker_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);

        if (!InitDatabase()) return;

        AppSettings.Instance.IsSyncPaused = false;
        _notionClient = new NotionClient(AppSettings.Instance);
        SyncQueue = new SyncQueue(StickerRepo!, _notionClient, AppSettings.Instance);
        SyncQueue.SyncError += OnSyncError;
        SyncQueue.SyncConflict += OnSyncConflict;
        SyncQueue.LiveStickerLookup = id => _stickerWindows.TryGetValue(id, out var w) ? w.Sticker : null;
        _ = EnsureBotUserIdAsync();   // 덮어쓰기 보호/pull의 전제 — 실패해도 앱 동작엔 영향 없음

        _pullService = new PullService(StickerRepo!, SettingsRepo!, _notionClient,
            AppSettings.Instance,
            id => _stickerWindows.TryGetValue(id, out var w) ? w : null,
            OnPullApplied);

        InitTray();
        InitPomodoro();
        RestoreStickers();
        StartSyncLoop();
        StartRetryTimer();

        if (!AppSettings.Instance.IsConfigured)
            OpenSettings();
    }

    private bool InitDatabase()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Noticker");
        try
        {
            Directory.CreateDirectory(dir);
            var dbPath = Path.Combine(dir, "noticker.db");
            StickerRepo = new StickerRepository(dbPath);
            SettingsRepo = new SettingsRepository(dbPath);
            SettingsRepo.LoadInto(AppSettings.Instance);
            return true;
        }
        catch (SqliteException ex)
        {
            var result = System.Windows.MessageBox.Show(
                $"데이터베이스를 열 수 없습니다.\n{ex.Message}\n\n" +
                "DB를 초기화하고 다시 시작하려면 [예]를 클릭하세요.",
                "Noticker — DB 오류",
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);
            if (result == MessageBoxResult.Yes)
            {
                try
                {
                    File.Delete(Path.Combine(dir, "noticker.db"));
                    StickerRepo = new StickerRepository(Path.Combine(dir, "noticker.db"));
                    SettingsRepo = new SettingsRepository(Path.Combine(dir, "noticker.db"));
                    return true;
                }
                catch { /* fall through */ }
            }
            Shutdown();
            return false;
        }
        catch (IOException ex)
        {
            System.Windows.MessageBox.Show(
                $"DB 파일을 생성할 수 없습니다.\n경로: {dir}\n\n{ex.Message}",
                "Noticker — 경로 오류",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
            return false;
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        var uri = new Uri("pack://application:,,,/Assets/noticker.ico");
        var stream = System.Windows.Application.GetResourceStream(uri)?.Stream;
        return stream is not null
            ? new System.Drawing.Icon(stream)
            : SystemIcons.Application;
    }

    private void InitTray()
    {
        _baseTrayIcon = LoadTrayIcon();
        _trayIcon = new NotifyIcon
        {
            Icon = _baseTrayIcon,
            Visible = true,
            Text = "Noticker"
        };

        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                OpenNoteList();
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("노트 목록", null, (_, _) => OpenNoteList());
        menu.Items.Add("새 스티커", null, (_, _) => CreateSticker());
        menu.Items.Add("포모도로 타이머", null, (_, _) => OpenPomodoro());
        menu.Items.Add("모든 스티커 표시", null, (_, _) => ShowAllStickers());
        menu.Items.Add("수동 Sync", null, async (_, _) => await RetryPendingAsync());
        menu.Items.Add("설정", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApp());

        _trayIcon.ContextMenuStrip = menu;
    }

    private static IReadOnlyList<(string DeviceName, System.Drawing.Rectangle Area)> CurrentScreens() =>
        Screen.AllScreens.Select(sc => (sc.DeviceName, sc.WorkingArea)).ToList();

    private void RestoreStickers()
    {
        var stickers = StickerRepo!.GetAll();
        var screens = CurrentScreens();
        var primary = Screen.PrimaryScreen!.WorkingArea;

        foreach (var s in stickers)
        {
            var wa = ScreenPlacement.SelectWorkingArea(screens, primary, s.MonitorDeviceName);
            var (x, y) = ScreenPlacement.ClampToArea(wa, s.PositionX, s.PositionY, s.Width, s.Height);
            OpenStickerWindow(s, x, y);
        }
    }

    public void CreateSticker()
    {
        var screen = GetActiveScreen();
        var wa = screen.WorkingArea;
        int x = wa.Left + (wa.Width - 250) / 2;
        int y = wa.Top + (wa.Height - 300) / 2;

        var s = new Sticker
        {
            MonitorDeviceName = screen.DeviceName,
            PositionX = x - wa.Left,
            PositionY = y - wa.Top,
        };
        StickerRepo!.Insert(s);
        OpenStickerWindow(s, x, y);
    }

    private void OpenStickerWindow(Sticker s, int x, int y)
    {
        var win = new StickerWindow(s, StickerRepo!, SyncQueue!);
        win.Left = x;
        win.Top = y;
        win.Width = s.Width;
        win.Height = s.Height;
        win.RealClosed += (_, _) => _stickerWindows.Remove(s.Id);
        _stickerWindows[s.Id] = win;
        if (!s.IsHidden) win.Show();
    }

    public void ShowSticker(string id)
    {
        if (!_stickerWindows.TryGetValue(id, out var win)) return;
        win.Sticker.IsHidden = false;
        StickerRepo!.Update(win.Sticker);
        win.Show();
        win.Activate();
    }

    public void DeleteSticker(string id)
    {
        if (_stickerWindows.TryGetValue(id, out var win))
        {
            win.CancelDebounce();
            win.ForceClose();
            // _stickerWindows.Remove handled by RealClosed subscriber
        }
        StickerRepo!.Delete(id);
    }

    public void OpenNoteList()
    {
        var existing = Windows.OfType<NoteListWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }
        new NoteListWindow(StickerRepo!).Show();
    }

    public void OpenNotionImport()
    {
        var existing = Windows.OfType<NotionImportWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }
        new NotionImportWindow(_notionClient!, StickerRepo!).Show();
    }

    // Notion 페이지 → 새 스티커 (가져오기 전용).
    // sync_state='synced'가 핵심 — 'pending' 기본값이면 1분 뒤 무편집 자동 push가
    // 본문 블록을 평탄화 텍스트로 교체해 Notion 원본 서식을 파괴한다 (리뷰 검증 시퀀스)
    public void CreateImportedSticker(string title, string plainBody, string bodyRtf,
        string pageId, string editTime, string editBy, bool pullDisabled)
    {
        var screen = GetActiveScreen();
        var wa = screen.WorkingArea;
        int x = wa.Left + (wa.Width - 250) / 2;
        int y = wa.Top + (wa.Height - 300) / 2;

        var s = new Sticker
        {
            Title = title,
            Body = plainBody,
            BodyRtf = bodyRtf,
            NotionPageId = pageId,
            NotionLastEdit = editTime,
            NotionLastEditBy = editBy,
            PullDisabled = pullDisabled,
            SyncState = "synced",
            MonitorDeviceName = screen.DeviceName,
            PositionX = x - wa.Left,
            PositionY = y - wa.Top,
        };
        StickerRepo!.Insert(s);
        OpenStickerWindow(s, x, y);
        _stickerWindows[s.Id].Activate();
    }

    private void ShowAllStickers()
    {
        foreach (var (_, w) in _stickerWindows)
        {
            if (!w.Sticker.IsHidden)
            {
                w.WindowState = WindowState.Normal;
                w.Activate();
            }
        }
    }

    // ── 포모도로 ───────────────────────────────────────────────────────────────

    private void InitPomodoro()
    {
        // 시간 소스는 UtcNow — Now 금지 (DST/시계 변경 시 타이머 동결 방지)
        _pomodoro = new PomodoroService(() => DateTime.UtcNow);
        // 프로퍼티 set — SetCustomDuration의 Custom+Idle 가드를 우회하는 초기 로드 경로
        _pomodoro.CustomMinutes = AppSettings.Instance.PomodoroCustomMinutes;
        RefreshPomodoroSettings();
        _pomodoro.Changed += OnPomodoroChanged;
        _pomodoro.SessionEnded += OnPomodoroSessionEnded;

        // State == Running일 때만 구동 (OnPomodoroChanged가 start/stop 동기화)
        _pomodoroTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pomodoroTimer.Tick += (_, _) =>
        {
            if (IsShuttingDown) return;
            _pomodoro!.Tick();
        };
    }

    // SettingsWindow 저장 후 호출 — AppSettings → 서비스로 복사 (세션 중 변경은 다음 세션부터)
    // 주의: CustomMinutes는 여기서 건드리지 않음 — 다이얼 입력만이 변경 (설계 불간섭 invariant)
    public void RefreshPomodoroSettings()
    {
        if (_pomodoro is null) return;
        var s = AppSettings.Instance;
        _pomodoro.FocusMinutes = s.PomodoroFocusMinutes;
        _pomodoro.ShortBreakMinutes = s.PomodoroShortBreakMinutes;
        _pomodoro.LongBreakMinutes = s.PomodoroLongBreakMinutes;
        _pomodoro.LongBreakInterval = s.PomodoroLongBreakInterval;
        _pomodoro.AutoStart = s.PomodoroAutoStart;
    }

    public void OpenPomodoro(string? colorKey = null)
    {
        if (_pomodoro is null) return;
        // 색 잠금: Idle일 때만 키 갱신. null(트레이/무카테고리) = 테마 바색(흑/백) 웨지
        if (_pomodoro.State == PomodoroState.Idle)
            _pomodoroWedgeColorKey = colorKey;
        _pomodoroWindow ??= new PomodoroWindow(_pomodoro, SettingsRepo!);
        // ??= 캐시 창에는 매 호출 push — 생성자 전달만으론 색이 첫 오픈에 고정됨
        _pomodoroWindow.SetWedgeColorKey(_pomodoroWedgeColorKey);
        _pomodoroWindow.Show();      // hide 패턴이라 OfType+Activate만으로는 안 보임
        _pomodoroWindow.Activate();  // 사용자가 직접 연 경우 — 활성화가 맞음
    }

    public void ShowPomodoroHideNotice()
    {
        if (IsShuttingDown) return;
        _trayIcon?.ShowBalloonTip(4000, "Noticker",
            "타이머는 백그라운드에서 계속 실행 중입니다", ToolTipIcon.None);
    }

    private void OnPomodoroChanged(object? sender, EventArgs e)
    {
        if (IsShuttingDown) return;
        bool running = _pomodoro!.State == PomodoroState.Running;
        if (running && !_pomodoroTimer!.IsEnabled) _pomodoroTimer.Start();
        else if (!running && _pomodoroTimer!.IsEnabled) _pomodoroTimer.Stop();
        UpdateTrayTooltip();
        UpdateTrayWedgeIcon();
    }

    private void UpdateTrayWedgeIcon()
    {
        if (IsShuttingDown || _trayIcon is null || _pomodoro is null) return;

        bool active = _pomodoro.State != PomodoroState.Idle;
        // 분 단위 quantize — 16px 아이콘에서 초 단위 재렌더는 무의미 (GDI 핸들 churn 방지)
        int bucket = active ? (int)Math.Ceiling(_pomodoro.WedgeFraction * 60) : -1;
        var key = (bucket, _pomodoroWedgeColorKey, active);
        if (key == _trayWedgeKey) return;
        _trayWedgeKey = key;

        var old = _wedgeTrayIcon;
        try
        {
            if (!active)
            {
                _trayIcon.Icon = _baseTrayIcon;
                _wedgeTrayIcon = null;
            }
            else
            {
                // 모노톤(키 null)은 라이트/다크 태스크바 양쪽에서 읽히는 중간 회색
                var color = _pomodoroWedgeColorKey is null
                    ? System.Drawing.Color.FromArgb(0x9A, 0xA0, 0xA6)
                    : ToDrawingColor(NotionColorPalette.Wedge(_pomodoroWedgeColorKey));
                _wedgeTrayIcon = TrayWedgeIcon.Render(_pomodoro.WedgeFraction, color);
                _trayIcon.Icon = _wedgeTrayIcon;
            }
        }
        catch (ObjectDisposedException) { return; }
        old?.Dispose();
    }

    private static System.Drawing.Color ToDrawingColor(System.Windows.Media.Color c) =>
        System.Drawing.Color.FromArgb(c.R, c.G, c.B);

    private void UpdateTrayTooltip()
    {
        if (IsShuttingDown || _trayIcon is null) return;
        var text = _pomodoro!.TrayTooltip;
        if (text == _lastTrayTooltip) return;          // 같은 값이면 재대입 생략
        _lastTrayTooltip = text;
        try { _trayIcon.Text = text; }
        catch (ObjectDisposedException) { }
    }

    private void OnPomodoroSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        if (IsShuttingDown) return;

        // 1순위: 위젯 자동 표시 (보장 채널 — ShowActivated=False라 포커스 안 뺏음)
        if (_pomodoroWindow is not null && !_pomodoroWindow.IsVisible)
            _pomodoroWindow.Show();

        // 2순위: 사운드 (Asterisk — 보상의 순간에 경고음은 부적합)
        if (AppSettings.Instance.PomodoroSound)
        {
            try { System.Media.SystemSounds.Asterisk.Play(); }
            catch { /* 장치 없음 등 — 무시 */ }
        }

        // 3순위: 트레이 풍선 (Win11 집중 지원이 억제할 수 있는 보조 채널)
        // Kind 최우선 분기 — 커스텀 종료에 포모도로 문구 금지 (EndedMode는 Custom에서 의미 없음)
        var msg = e.Kind == TimerKind.Custom
            ? $"타이머 끝 — {e.EndedMinutes}분 경과"
            : e.EndedMode == PomodoroMode.Focus
                ? $"집중 끝 — {(e.NextMode == PomodoroMode.LongBreak ? "긴" : "짧은")} 휴식하세요"
                : "휴식 끝 — 다시 집중할 시간";
        try
        {
            _trayIcon?.ShowBalloonTip(5000, "Noticker — 포모도로", msg, ToolTipIcon.None);
        }
        catch (ObjectDisposedException) { }
    }

    public void OpenSettings()
    {
        var existing = Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }
        new SettingsWindow(SettingsRepo!, _notionClient!, StickerRepo!).Show();
    }

    private async Task RetryPendingAsync()
    {
        AppSettings.Instance.IsSyncPaused = false;
        await SyncQueue!.RetryPendingAsync(_cts.Token);
        // 수동 Sync는 pull도 함께 수행
        if (_pullService is not null)
            await _pullService.RunOnceAsync(_cts.Token);
    }

    private void OnPullApplied(string title, string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (IsShuttingDown) return;
            var label = string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : title;
            try
            {
                _trayIcon?.ShowBalloonTip(5000, "Noticker — Notion 갱신",
                    $"'{label}' 메모가 {message}", ToolTipIcon.Info);
            }
            catch (ObjectDisposedException) { }
        });
    }

    private void StartSyncLoop()
    {
        Task.Run(() => SyncQueue!.RunAsync(_cts.Token));
    }

    private void StartRetryTimer()
    {
        _retryTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMinutes(1)
        };
        _retryTimer.Tick += async (_, _) =>
        {
            if (AppSettings.Instance.IsSyncPaused) return;
            await EnsureBotUserIdAsync();   // 오프라인 시작 후 복구 — 캐시되면 즉시 반환
            await SyncQueue!.RetryPendingAsync(_cts.Token);
            if (_pullService is not null && !IsShuttingDown)
                await _pullService.RunOnceAsync(_cts.Token);
        };
        _retryTimer.Start();
    }

    // bot user id 1회 캐시 — 없으면 0단계 보호와 pull이 조용히 비활성 (오프라인 등)
    private async Task EnsureBotUserIdAsync()
    {
        try
        {
            if (AppSettings.Instance.NotionBotUserId is not null) return;
            if (!AppSettings.Instance.IsConfigured) return;
            var botId = await _notionClient!.GetBotUserIdAsync(_cts.Token);
            if (botId is null) return;
            AppSettings.Instance.NotionBotUserId = botId;
            SettingsRepo!.Set("notion_bot_user_id", botId);
        }
        catch { /* 다음 실행에서 재시도 */ }
    }

    private void OnSyncConflict(string stickerId, string title, bool alreadyPushed)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (IsShuttingDown) return;
            var label = string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : title;
            // 사후 검출(TOCTOU)은 push가 이미 반영된 뒤 — "보류" 문구는 거짓이 됨
            var msg = alreadyPushed
                ? $"'{label}' — push가 Notion 수정과 겹침, 스티커 버전이 반영됨"
                : $"'{label}' — Notion에서 수정됨, push 보류";
            try
            {
                _trayIcon?.ShowBalloonTip(6000, "Noticker — 동기화 충돌", msg, ToolTipIcon.Warning);
            }
            catch (ObjectDisposedException) { }
            // 충돌 점/툴팁 즉시 반영 (백그라운드 전이는 UI 갱신 트리거가 없음)
            if (_stickerWindows.TryGetValue(stickerId, out var win))
                win.RefreshSyncIndicator();
        });
    }

    // 토큰이 교체되면 bot id도 무효 — 옛 봇 id로는 모든 push가 "남의 수정"으로 보여
    // 매번 충돌이 난다. SettingsWindow가 토큰 저장 시 호출
    public void InvalidateBotUserId()
    {
        AppSettings.Instance.NotionBotUserId = null;
        try { SettingsRepo!.Delete("notion_bot_user_id"); } catch { }
        _ = EnsureBotUserIdAsync();
    }

    private void OnSyncError(string stickerId, string message)
    {
        // Show tray balloon so the user sees exactly why sync failed
        Dispatcher.InvokeAsync(() =>
        {
            _trayIcon?.ShowBalloonTip(
                timeout: 6000,
                tipTitle: "Noticker — Sync 실패",
                tipText: message,
                tipIcon: ToolTipIcon.Error);
        });
    }

    private void ExitApp()
    {
        IsShuttingDown = true;
        _pomodoroTimer?.Stop();      // 트레이 dispose 전에 정지 — disposed NotifyIcon 쓰기 방지
        _cts.Cancel();
        _retryTimer?.Stop();
        _trayIcon?.Dispose();
        _trayIcon = null;
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // Windows reboot/shutdown: mark shutting down so StickerWindow.OnClosing
        // doesn't persist IsHidden = true for every open sticker.
        IsShuttingDown = true;
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _cts.Cancel();
        _trayIcon?.Dispose();
        _mutex?.ReleaseMutex();
        base.OnExit(e);
    }

    private static Screen GetActiveScreen()
    {
        var pos = System.Windows.Forms.Cursor.Position;
        return Screen.FromPoint(pos) ?? Screen.PrimaryScreen!;
    }
}

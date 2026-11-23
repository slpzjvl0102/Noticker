using System.IO;
using System.Reflection;
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
    private HotkeyManager? _hotkey;

    public StickerRepository? StickerRepo { get; private set; }
    public SettingsRepository? SettingsRepo { get; private set; }
    public SyncQueue? SyncQueue { get; private set; }
    private NotionClient? _notionClient;
    private PullService? _pullService;
    private CancellationTokenSource _cts = new();
    private System.Windows.Threading.DispatcherTimer? _retryTimer;

    private readonly Dictionary<string, StickerWindow> _stickerWindows = [];
    public bool IsShuttingDown { get; private set; }

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

        // 일회성 self-heal: 중첩 리스트 지원 전 has_children 때문에 PullDisabled된 스티커를 재활성.
        // 진짜 미지원(이미지/표 등)은 다음 pull에서 다시 비활성되므로 안전.
        if (SettingsRepo!.Get("pulldisabled_reset_nesting_v1") is null)
        {
            StickerRepo!.ClearAllPullDisabled();
            SettingsRepo.Set("pulldisabled_reset_nesting_v1", "1");
        }

        AppSettings.Instance.IsSyncPaused = false;
        _notionClient = new NotionClient(AppSettings.Instance);
        SyncQueue = new SyncQueue(StickerRepo!, _notionClient, AppSettings.Instance);
        SyncQueue.SyncError += OnSyncError;
        SyncQueue.SyncConflict += OnSyncConflict;
        SyncQueue.SyncSucceeded += OnSyncSucceeded;
        SyncQueue.LiveStickerLookup = id => _stickerWindows.TryGetValue(id, out var w) ? w.Sticker : null;
        _ = EnsureBotUserIdAsync();   // 덮어쓰기 보호/pull의 전제 — 실패해도 앱 동작엔 영향 없음

        _pullService = new PullService(StickerRepo!, SettingsRepo!, _notionClient,
            AppSettings.Instance,
            id => _stickerWindows.TryGetValue(id, out var w) ? w : null,
            OnPullApplied);

        // 자동 실행 기본 활성화 — 설정값을 레지스트리에 동기화 (멱등).
        // PersistSettings만으론 설정 창을 안 연 사용자에게 기본값이 적용되지 않는다
        var exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        if (AppSettings.Instance.AutostartEnabled)
            StartupManager.Enable(exePath);
        else
            StartupManager.Disable();

        InitTray();
        InitHotkey();
        InitPomodoro();
        RestoreStickers();
        StartSyncLoop();
        StartRetryTimer();

        if (!AppSettings.Instance.IsConfigured)
            OpenOnboarding();
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

    private void InitHotkey()
    {
        _hotkey = new HotkeyManager();
        _hotkey.Pressed += OnHotkeyPressed;
        if (!ApplyHotkey())
        {
            // 시작 시 실패(타 앱 점유) — 풍선 1회, 앱은 정상 동작 (스펙 §5)
            var name = HotkeyPresets.DisplayName(AppSettings.Instance.HotkeyPreset);
            ShowBalloon(6000, "Noticker",
                $"단축키 등록 실패 — {name}을(를) 다른 앱이 사용 중입니다. 설정에서 변경하세요.",
                ToolTipIcon.Warning);
        }
    }

    // 시작·설정 변경 공용 — AppSettings의 프리셋을 실제 등록에 반영. 성공 여부 반환.
    // SettingsWindow가 변경 적용 시 호출
    public bool ApplyHotkey()
    {
        if (_hotkey is null) return false;
        var combo = HotkeyPresets.Resolve(AppSettings.Instance.HotkeyPreset);
        if (combo is null) { _hotkey.Unregister(); return true; }   // 사용 안 함
        return _hotkey.Register(combo.Value.Modifiers, combo.Value.Vk);
    }

    // 단축키 = 즉시 캡처 — 생성 후 창 활성화 + 본문 포커스 (바로 타이핑).
    // 사용자가 등록된 hotkey를 누른 직후라 OS가 포그라운드 전환을 허용한다
    private void OnHotkeyPressed()
    {
        if (IsShuttingDown) return;
        var win = CreateSticker();
        win.Activate();
        // Show 직후라 레이아웃 전 — 동기 Focus는 조용히 실패할 수 있다. Loaded 후로 지연
        win.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
            (Action)win.FocusBody);
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

    // 새 스티커 기본 배치 — 커서가 있는 모니터 작업 영역 중앙 (250x300 기본 크기 기준).
    // 반환: 대상 모니터와 화면 절대 좌표(x, y). 모니터 상대 좌표는 PlaceOn이 채운다
    private static (Screen Screen, int X, int Y) CenterOnActiveScreen()
    {
        var screen = GetActiveScreen();
        var wa = screen.WorkingArea;
        return (screen, wa.Left + (wa.Width - 250) / 2, wa.Top + (wa.Height - 300) / 2);
    }

    private static void PlaceOn(Sticker s, Screen screen, int x, int y)
    {
        s.MonitorDeviceName = screen.DeviceName;
        s.PositionX = x - screen.WorkingArea.Left;
        s.PositionY = y - screen.WorkingArea.Top;
    }

    public StickerWindow CreateSticker()
    {
        var (screen, x, y) = CenterOnActiveScreen();
        var s = new Sticker();
        PlaceOn(s, screen, x, y);
        StickerRepo!.Insert(s);
        OpenStickerWindow(s, x, y);
        return _stickerWindows[s.Id];
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
    public void CreateImportedSticker(string title, string plainBody, string bodyRtf, string bodyRuns,
        string pageId, string editTime, string editBy, bool pullDisabled)
    {
        var (screen, x, y) = CenterOnActiveScreen();
        var s = new Sticker
        {
            Title = title,
            Body = plainBody,
            BodyRtf = bodyRtf,
            BodyRuns = bodyRuns,
            NotionPageId = pageId,
            NotionLastEdit = editTime,
            NotionLastEditBy = editBy,
            PullDisabled = pullDisabled,
            SyncState = "synced",
        };
        PlaceOn(s, screen, x, y);
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

    public void OpenSettings()
    {
        var existing = Windows.OfType<SettingsWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }

        var win = new SettingsWindow(SettingsRepo!, _notionClient!, StickerRepo!);
        // 모니터 우측 배치 — 온보딩 직후 중앙에 생기는 새 스티커와 겹치지 않도록
        var wa = GetActiveScreen().WorkingArea;
        win.Left = Math.Max(wa.Left, wa.Right - win.Width - 16);
        win.Top = wa.Top + Math.Max(0, (wa.Height - win.Height) / 2);
        win.Show();
    }

    public void OpenOnboarding()
    {
        var existing = Windows.OfType<OnboardingWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }
        new OnboardingWindow(SettingsRepo!, _notionClient!).Show();
    }

    // 온보딩 완료 직후 호출 — 설정이 끝났다는 걸 눈에 보이게: 스티커가 하나도
    // 없으면 한 장 만들고, 설정 창을 연다 (재연결 경로에선 이미 떠 있어 Activate만 됨)
    public void ShowPostOnboardingUi()
    {
        if (StickerRepo!.GetAllSummary().Count == 0)
            CreateSticker();
        OpenSettings();
    }

    private void ExitApp()
    {
        IsShuttingDown = true;
        _pomodoroTimer?.Stop();      // 트레이 dispose 전에 정지 — disposed NotifyIcon 쓰기 방지
        _cts.Cancel();
        _retryTimer?.Stop();
        _hotkey?.Dispose();
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
        _hotkey?.Dispose();
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

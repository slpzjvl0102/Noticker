using System.IO;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using Noticker.Data;
using Noticker.Models;
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

        AppSettings.Instance.IsSyncPaused = false;
        _notionClient = new NotionClient(AppSettings.Instance);
        SyncQueue = new SyncQueue(StickerRepo!, _notionClient, AppSettings.Instance);
        SyncQueue.SyncError += OnSyncError;

        InitTray();
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
        _trayIcon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
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
        menu.Items.Add("모든 스티커 표시", null, (_, _) => ShowAllStickers());
        menu.Items.Add("수동 Sync", null, async (_, _) => await RetryPendingAsync());
        menu.Items.Add("설정", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("종료", null, (_, _) => ExitApp());

        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreStickers()
    {
        var stickers = StickerRepo!.GetAll();
        var screens = Screen.AllScreens;

        foreach (var s in stickers)
        {
            var screen = screens.FirstOrDefault(sc =>
                string.Equals(sc.DeviceName, s.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
                ?? Screen.PrimaryScreen!;

            var wa = screen.WorkingArea;
            int x = Math.Clamp(s.PositionX + wa.Left, wa.Left, wa.Right - s.Width);
            int y = Math.Clamp(s.PositionY + wa.Top, wa.Top, wa.Bottom - s.Height);

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
        new SettingsWindow(SettingsRepo!, _notionClient!, StickerRepo!).Show();
    }

    private async Task RetryPendingAsync()
    {
        AppSettings.Instance.IsSyncPaused = false;
        await SyncQueue!.RetryPendingAsync(_cts.Token);
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
            if (!AppSettings.Instance.IsSyncPaused)
                await SyncQueue!.RetryPendingAsync(_cts.Token);
        };
        _retryTimer.Start();
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
        _cts.Cancel();
        _retryTimer?.Stop();
        _trayIcon?.Dispose();
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

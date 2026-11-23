using System.Windows.Forms;
using Noticker.Infrastructure;

namespace Noticker;

// App partial — 자동 업데이트 (GitHub Releases, Velopack).
// 시작 1분 뒤와 이후 6시간마다 확인해 백그라운드로 받아 두고, 트레이 메뉴로 재시작 적용.
// 받아 둔 업데이트는 재시작하지 않아도 다음 실행(부팅 시 자동 실행 포함) 때 적용된다.
public partial class App
{
    private readonly AppUpdater _updater = new();
    private System.Windows.Threading.DispatcherTimer? _updateTimer;
    private ToolStripMenuItem? _updateMenuItem;

    private void InitUpdater(ContextMenuStrip menu, int insertIndex)
    {
        _updateMenuItem = new ToolStripMenuItem("업데이트 확인", null, async (_, _) => await OnUpdateMenuClickedAsync());
        menu.Items.Insert(insertIndex, _updateMenuItem);
        RefreshUpdateMenuItem();

        if (!_updater.IsInstalled) return;
        _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(6);
            await CheckForUpdateAsync(manual: false);
        };
        _updateTimer.Start();
    }

    private async Task OnUpdateMenuClickedAsync()
    {
        if (_updater.ReadyVersion is not null)
        {
            if (_updater.ScheduleApplyOnExit()) ExitApp();
            return;
        }
        await CheckForUpdateAsync(manual: true);
    }

    private async Task CheckForUpdateAsync(bool manual)
    {
        if (IsShuttingDown) return;
        var result = await _updater.CheckAndDownloadAsync(_cts.Token);
        if (IsShuttingDown) return;
        RefreshUpdateMenuItem();

        switch (result)
        {
            case UpdateCheckResult.Ready:
                ShowBalloon(5000, "Noticker 업데이트 준비됨",
                    $"v{_updater.ReadyVersion}을 받아 두었습니다. 트레이 메뉴의 '재시작하여 업데이트'를 누르거나, 다음 실행 때 자동 적용됩니다.",
                    ToolTipIcon.Info);
                break;
            case UpdateCheckResult.UpToDate when manual:
                ShowBalloon(3000, "Noticker", $"최신 버전입니다 (v{_updater.CurrentVersion}).", ToolTipIcon.Info);
                break;
            case UpdateCheckResult.NotInstalled when manual:
                ShowBalloon(5000, "Noticker",
                    "설치 버전(Noticker-win-Setup.exe)에서만 자동 업데이트를 쓸 수 있습니다.", ToolTipIcon.Info);
                break;
            case UpdateCheckResult.Failed when manual:
                ShowBalloon(5000, "업데이트 확인 실패", _updater.LastError ?? "알 수 없는 오류", ToolTipIcon.Warning);
                break;
        }
    }

    private void RefreshUpdateMenuItem()
    {
        if (_updateMenuItem is null) return;
        _updateMenuItem.Text = _updater.ReadyVersion is { } v
            ? $"재시작하여 업데이트 (v{v})"
            : $"업데이트 확인 (v{_updater.CurrentVersion})";
    }
}

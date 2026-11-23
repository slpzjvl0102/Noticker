using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Noticker.Infrastructure;

public enum UpdateCheckResult { NotInstalled, UpToDate, Ready, Failed }

// GitHub Releases에서 새 버전을 확인·다운로드한다 (Velopack).
// 다운로드만 해 두면 다음 실행 때 자동 적용되고, 트레이 메뉴로 즉시 재시작 적용도 할 수 있다.
// 사용자 데이터(%APPDATA%\Noticker)는 설치 폴더 밖이라 업데이트와 무관하게 유지된다.
public sealed class AppUpdater
{
    public const string RepoUrl = "https://github.com/slpzjvl0102/Noticker";

    private readonly UpdateManager _manager = new(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    private VelopackAsset? _ready;
    private int _busy;

    // Setup.exe로 설치된 경우에만 업데이트 가능 (dotnet run·단독 exe 실행은 대상 아님)
    public bool IsInstalled => _manager.IsInstalled;

    public string CurrentVersion =>
        _manager.CurrentVersion?.ToString()
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)
        ?? "?";

    public string? ReadyVersion => (_ready ?? (IsInstalled ? _manager.UpdatePendingRestart : null))?.Version.ToString();

    public string? LastError { get; private set; }

    public async Task<UpdateCheckResult> CheckAndDownloadAsync(CancellationToken ct = default)
    {
        if (!IsInstalled) return UpdateCheckResult.NotInstalled;
        if (ReadyVersion is not null) return UpdateCheckResult.Ready;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return UpdateCheckResult.UpToDate;
        try
        {
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info is null) return UpdateCheckResult.UpToDate;
            await _manager.DownloadUpdatesAsync(info, null, ct).ConfigureAwait(false);
            _ready = info.TargetFullRelease;
            LastError = null;
            return UpdateCheckResult.Ready;
        }
        catch (OperationCanceledException) { return UpdateCheckResult.UpToDate; }
        catch (Exception ex)
        {
            // 오프라인·GitHub 일시 오류 — 다음 주기에 다시 시도
            LastError = ex.Message;
            return UpdateCheckResult.Failed;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    // 앱이 정상 종료(데이터 저장)된 뒤 업데이트를 적용하고 다시 실행하도록 예약한다.
    // 호출 후 앱은 평소처럼 종료하면 된다.
    public bool ScheduleApplyOnExit()
    {
        var asset = _ready ?? _manager.UpdatePendingRestart;
        if (asset is null) return false;
        _manager.WaitExitThenApplyUpdates(asset, silent: true, restart: true);
        return true;
    }
}

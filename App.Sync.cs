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

// App partial — Notion sync 루프·재시도·pull 알림·충돌/오류 표시·카테고리 새로고침. (App.xaml.cs에서 분리, 동작 변경 없음)
public partial class App
{
    // 카테고리 옵션/색 새로고침 — SettingsWindow와 OnboardingWindow가 공유 (로직 한 곳).
    // 실패 시 예외를 그대로 던진다 — 호출자가 각자 상태 표시를 책임진다.
    // UI 스레드에서만 호출할 것 — await 후 Windows/StickerWindow 접근이 디스패처 복귀에 의존한다.
    public async Task<int> RefreshCategoryOptionsAsync()
    {
        var options = await _notionClient!.FetchCategoryOptionsAsync(_cts.Token);
        var names = options.Select(o => o.Name).ToList();
        var colors = options.ToDictionary(o => o.Name, o => o.Color);

        AppSettings.Instance.CategoryOptions = names;
        AppSettings.Instance.CategoryColors = colors;
        SettingsRepo!.SaveCategoryOptions(names);
        SettingsRepo!.SaveCategoryColors(colors);

        foreach (var win in Windows.OfType<StickerWindow>())
            win.RefreshCategoryOptions();

        return options.Count;
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
            ShowBalloon(5000, "Noticker — Notion 갱신", $"'{label}' 메모가 {message}", ToolTipIcon.Info);
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
                : $"'{label}' — Notion에서 편집 중입니다. 동기화를 잠시 보류했어요";
            ShowBalloon(6000, "Noticker — 동기화 충돌", msg, ToolTipIcon.Warning);
            // 충돌 점/툴팁 즉시 반영 (백그라운드 전이는 UI 갱신 트리거가 없음)
            if (_stickerWindows.TryGetValue(stickerId, out var win))
                win.RefreshSyncIndicator();
        });
    }

    private void OnSyncSucceeded(string stickerId)
    {
        // 주황(대기) → 초록(동기화됨) 즉시 반영 — 백그라운드 전이는 UI 갱신 트리거가 없음
        Dispatcher.InvokeAsync(() =>
        {
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
            ShowBalloon(6000, "Noticker — Sync 실패", message, ToolTipIcon.Error);
            // 실패 점/툴팁 즉시 반영 (백그라운드 전이는 UI 갱신 트리거가 없음)
            if (_stickerWindows.TryGetValue(stickerId, out var win))
                win.RefreshSyncIndicator();
        });
    }
}

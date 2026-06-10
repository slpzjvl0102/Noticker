using Noticker.Sync;

namespace Noticker.Tests;

public class PullDecisionTests
{
    private const string Bot = "bot-user-id";
    private const string Human = "human-user-id";
    private const string T0 = "2026-06-10T12:00:00.000Z";
    private const string T1 = "2026-06-10T12:01:00.000Z";
    private const string T2 = "2026-06-10T12:02:00.000Z";

    private static PageMeta Page(string time = T1, string by = Human)
        => new("page-1", time, by, "제목");

    // 기본값: 원격 사람 수정(T1) vs 저장 baseline(T0, Human), 깨끗한 synced 스티커
    private static PullAction Decide(
        PageMeta? page = null,
        string? storedTime = T0,
        string? storedBy = Human,
        string syncState = "synced",
        bool bodyEmpty = false,
        bool debouncePending = false,
        bool hasKeyboardFocus = false,
        bool pullDisabled = false)
        => PullDecision.Decide(page ?? Page(), Bot, storedTime, storedBy, syncState,
            bodyEmpty, debouncePending, hasKeyboardFocus, pullDisabled);

    // ── Decide: 각 분기 ────────────────────────────────────────────────────────

    [Fact]
    public void CleanSyncedSticker_HumanRemoteEdit_Applies()
    {
        Assert.Equal(PullAction.Apply, Decide());
    }

    [Fact]
    public void PullDisabled_Skips()
    {
        Assert.Equal(PullAction.Skip, Decide(pullDisabled: true));
    }

    [Fact]
    public void PairEqualToStored_Skips()
    {
        Assert.Equal(PullAction.Skip, Decide(page: Page(time: T1, by: Human), storedTime: T1, storedBy: Human));
    }

    [Fact]
    public void BotEdit_AckOnly()
    {
        Assert.Equal(PullAction.AckOnly, Decide(page: Page(by: Bot)));
    }

    [Fact]
    public void ConflictState_Skips()
    {
        Assert.Equal(PullAction.Skip, Decide(syncState: "conflict"));
    }

    [Fact]
    public void PendingWithBody_Skips()
    {
        Assert.Equal(PullAction.Skip, Decide(syncState: "pending", bodyEmpty: false));
    }

    [Fact]
    public void PendingWithEmptyBody_Applies()
    {
        // 빈 스티커의 영구 pending이 pull을 막으면 안 됨 (설계 가드 ④ 예외)
        Assert.Equal(PullAction.Apply, Decide(syncState: "pending", bodyEmpty: true));
    }

    [Fact]
    public void DebouncePending_Skips()
    {
        Assert.Equal(PullAction.Skip, Decide(debouncePending: true));
    }

    [Fact]
    public void KeyboardFocus_Skips()
    {
        Assert.Equal(PullAction.Skip, Decide(hasKeyboardFocus: true));
    }

    [Fact]
    public void NullStoredBaseline_HumanRemoteEdit_Applies()
    {
        // 레거시(V4 이전): 저장 쌍 null — 원격 쌍과 절대 같지 않으므로 dedupe 안 걸림
        Assert.Equal(PullAction.Apply, Decide(storedTime: null, storedBy: null));
    }

    // ── Decide: 검사 순서 고정 ─────────────────────────────────────────────────

    [Fact]
    public void Ordering_PullDisabledBeatsBotEcho()
    {
        // pullDisabled가 최우선 — 봇 에코라도 AckOnly가 아니라 Skip
        Assert.Equal(PullAction.Skip, Decide(page: Page(by: Bot), pullDisabled: true));
    }

    [Fact]
    public void Ordering_PairDedupeBeatsBotCheck()
    {
        // 저장 쌍과 동일한 봇 수정 — dedupe가 먼저라 AckOnly 아님
        Assert.Equal(PullAction.Skip, Decide(page: Page(time: T1, by: Bot), storedTime: T1, storedBy: Bot));
    }

    [Fact]
    public void Ordering_BotCheckBeatsConflict()
    {
        // conflict 스티커의 봇 에코 — 봇 검사가 먼저라 AckOnly (저장 쌍 갱신 허용)
        Assert.Equal(PullAction.AckOnly, Decide(page: Page(by: Bot), syncState: "conflict"));
    }

    [Fact]
    public void Ordering_BotCheckBeatsDirtyGuards()
    {
        // 타이핑 중이어도 봇 에코는 AckOnly — 본문을 건드리지 않으므로 안전
        Assert.Equal(PullAction.AckOnly, Decide(page: Page(by: Bot), debouncePending: true, hasKeyboardFocus: true));
    }

    [Fact]
    public void SameMinuteDifferentEditor_NotDeduped()
    {
        // 분 반올림으로 시각이 같아도 편집자가 다르면 쌍이 달라 dedupe 안 걸림
        Assert.Equal(PullAction.Apply, Decide(page: Page(time: T0, by: "other-human"), storedTime: T0, storedBy: Human));
    }

    // ── IsPushConflict ─────────────────────────────────────────────────────────

    [Fact]
    public void IsPushConflict_NullBaseline_NotConflict()
    {
        // 레거시 스티커 — baseline 없음 → 이번 push의 사후 GET이 baseline 수립
        Assert.False(PullDecision.IsPushConflict(T1, Human, storedTime: null, storedBy: null, Bot));
    }

    [Fact]
    public void IsPushConflict_TimeDiffers_Conflict()
    {
        Assert.True(PullDecision.IsPushConflict(T1, Human, T0, Human, Bot));
    }

    [Fact]
    public void IsPushConflict_ByDiffers_Conflict()
    {
        // 같은 분 내 다른 사람 수정 — 시각 동일해도 편집자로 검출
        Assert.True(PullDecision.IsPushConflict(T0, "other-human", T0, Human, Bot));
    }

    [Fact]
    public void IsPushConflict_BotEditor_NotConflict()
    {
        // 마지막 수정자가 봇이면 쌍이 달라도 충돌 아님 (우리 자신의 write)
        Assert.False(PullDecision.IsPushConflict(T1, Bot, T0, Human, Bot));
    }

    [Fact]
    public void IsPushConflict_PairEqual_NotConflict()
    {
        Assert.False(PullDecision.IsPushConflict(T0, Human, T0, Human, Bot));
    }

    // ── NextCursor ─────────────────────────────────────────────────────────────

    [Fact]
    public void NextCursor_EmptyApplied_KeepsCursor()
    {
        Assert.Equal(T1, PullDecision.NextCursor(T1, Array.Empty<string>()));
    }

    [Fact]
    public void NextCursor_NewerApplied_Advances()
    {
        Assert.Equal(T2, PullDecision.NextCursor(T0, new[] { T1, T2 }));
    }

    [Fact]
    public void NextCursor_OlderApplied_KeepsCursor()
    {
        // 커서보다 오래된 시각만 적용돼도 커서는 뒤로 가지 않음
        Assert.Equal(T1, PullDecision.NextCursor(T1, new[] { T0 }));
    }

    [Fact]
    public void NextCursor_Mixed_PicksMax()
    {
        Assert.Equal(T2, PullDecision.NextCursor(T1, new[] { T0, T2, T1 }));
    }
}

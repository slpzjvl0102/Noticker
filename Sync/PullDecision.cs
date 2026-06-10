namespace Noticker.Sync;

// 폴링 쿼리 결과의 페이지 메타 — 쿼리 결과가 페이지 객체라 last_edited_by 포함 (blocks GET 불필요).
public sealed record PageMeta(string PageId, string LastEditedTime, string LastEditedById, string Title);

public enum PullAction { Skip, AckOnly, Apply }

// Pull 파이프라인 ②-④ + push 충돌 판정 + 커서 갱신의 순수 결정 로직 (WPF/저장소 의존 없음).
public static class PullDecision
{
    public static PullAction Decide(
        PageMeta page,
        string botId,
        string? storedTime,
        string? storedBy,
        string syncState,
        bool bodyEmpty,
        bool debouncePending,
        bool hasKeyboardFocus,
        bool pullDisabled)
    {
        if (pullDisabled)
            return PullAction.Skip;

        // dedupe는 (시각, 편집자) 쌍 비교 — last_edited_time이 분 단위 반올림이라
        // 시각 단독으로는 같은 분 내 재수정을 구분 못 함
        if (page.LastEditedTime == storedTime && page.LastEditedById == storedBy)
            return PullAction.Skip;

        // 봇 에코: 본문 적용 없이 저장 쌍만 갱신
        if (page.LastEditedById == botId)
            return PullAction.AckOnly;

        // conflict = 로컬 보존 원칙 — pull 적용 금지
        if (syncState == "conflict")
            return PullAction.Skip;

        // 빈 스티커의 영구 pending이 pull을 막는 것 방지 — 본문 비면 dirty 아님
        if (syncState == "pending" && !bodyEmpty)
            return PullAction.Skip;

        if (debouncePending || hasKeyboardFocus)
            return PullAction.Skip;

        return PullAction.Apply;
    }

    public static bool IsPushConflict(
        string remoteTime,
        string remoteBy,
        string? storedTime,
        string? storedBy,
        string botId)
    {
        if (remoteBy == botId)
            return false;

        // storedTime null = V4 이전 레거시 스티커 — 비교할 baseline 자체가 없음.
        // 이번 push의 사후 GET이 baseline을 처음 수립하므로 충돌 아님
        // (충돌로 취급하면 레거시 스티커 전부가 첫 push에서 영구히 막힌다).
        if (storedTime is null)
            return false;

        return remoteTime != storedTime || remoteBy != storedBy;
    }

    public static string NextCursor(string currentCursor, IEnumerable<string> appliedLastEditedTimes)
    {
        // Notion 시각은 UTC 고정 폭 ISO-8601("...Z") — 사전식 비교가 시간순과 일치, 파싱 불필요.
        var max = currentCursor;
        foreach (var time in appliedLastEditedTimes)
        {
            if (string.CompareOrdinal(time, max) > 0)
                max = time;
        }
        return max;
    }
}

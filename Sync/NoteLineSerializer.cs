using System.Text.Json;

namespace Noticker.Sync;

// NoteLine ↔ JSON (stickers.body_runs 컬럼) — HTTP/WPF 없이 단위 테스트 가능한 순수 함수.
// 역직렬화 실패는 null 반환 — 호출자(push)가 plain 폴백을 타도록 예외를 삼킨다
public static class NoteLineSerializer
{
    private sealed record RunDto(string? T, bool B, bool U);
    private sealed record LineDto(string? Kind, List<RunDto>? Runs);

    public static string Serialize(IReadOnlyList<NoteLine> lines)
    {
        var dtos = lines.Select(l => new LineDto(
            l.Kind switch
            {
                NoteLineKind.Bullet => "bullet",
                NoteLineKind.Number => "numbered",
                _ => "paragraph",
            },
            l.Runs.Select(r => new RunDto(r.Text, r.Bold, r.Underline)).ToList())).ToList();
        return JsonSerializer.Serialize(dtos);
    }

    public static List<NoteLine>? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var dtos = JsonSerializer.Deserialize<List<LineDto>>(json);
            if (dtos is null) return null;

            var lines = new List<NoteLine>(dtos.Count);
            foreach (var d in dtos)
            {
                NoteLineKind? kind = d.Kind switch
                {
                    "paragraph" => NoteLineKind.Paragraph,
                    "bullet" => NoteLineKind.Bullet,
                    "numbered" => NoteLineKind.Number,
                    _ => null,
                };
                // 알 수 없는 Kind/Runs 누락 — 부분 복구 대신 통째로 폴백 (본문 유실 방지)
                if (kind is null || d.Runs is null) return null;
                lines.Add(new NoteLine(kind.Value,
                    d.Runs.Select(r => new NoteRun(r.T ?? "", r.B, r.U)).ToList()));
            }
            return lines;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

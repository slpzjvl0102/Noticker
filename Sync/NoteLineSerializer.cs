using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Noticker.Sync;

// NoteLine ↔ JSON (stickers.body_runs 컬럼) — HTTP/WPF 없이 단위 테스트 가능한 순수 함수.
// 역직렬화 실패는 null 반환 — 호출자(push)가 plain 폴백을 타도록 예외를 삼킨다
public static class NoteLineSerializer
{
    // DB 영속 포맷 — 속성명을 C# 식별자에서 유도하지 않고 명시적으로 고정
    // (공용 JsonSerializerOptions(camelCase 등)가 끼어도 기존 행을 계속 읽도록)
    private sealed record RunDto(
        [property: JsonPropertyName("T")] string? T,
        [property: JsonPropertyName("B")] bool B,
        [property: JsonPropertyName("U")] bool U);

    private sealed record LineDto(
        [property: JsonPropertyName("Kind")] string? Kind,
        [property: JsonPropertyName("Runs")] List<RunDto?>? Runs);

    // 한글을 \uXXXX로 이스케이프하지 않는다 (~3x 절감). DB 저장 전용 JSON이라 안전 —
    // [JsonPropertyName] 고정과 같은 이유로 옵션도 여기 고정 (외부 옵션 주입 금지)
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(IReadOnlyList<NoteLine> lines)
    {
        var dtos = lines.Select(l => new LineDto(
            l.Kind switch
            {
                NoteLineKind.Bullet => "bullet",
                NoteLineKind.Number => "numbered",
                _ => "paragraph",
            },
            l.Runs.Select(r => (RunDto?)new RunDto(r.Text, r.Bold, r.Underline)).ToList())).ToList();
        return JsonSerializer.Serialize(dtos, _writeOptions);
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
                NoteLineKind? kind = d?.Kind switch
                {
                    "paragraph" => NoteLineKind.Paragraph,
                    "bullet" => NoteLineKind.Bullet,
                    "numbered" => NoteLineKind.Number,
                    _ => null,
                };
                // 알 수 없는 Kind/Runs 누락/null 요소 — 부분 복구 대신 통째로 폴백 (본문 유실 방지)
                if (d is null || kind is null || d.Runs is null || d.Runs.Any(r => r is null))
                    return null;
                lines.Add(new NoteLine(kind.Value,
                    d.Runs.Select(r => new NoteRun(r!.T ?? "", r.B, r.U)).ToList()));
            }
            return lines;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

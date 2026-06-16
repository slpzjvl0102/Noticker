using System.Text;
using System.Text.Json;

namespace Noticker.Sync;

public enum NoteLineKind { Paragraph, Bullet, Number }

public sealed record NoteRun(string Text, bool Bold, bool Underline);

// Depth: 중첩 리스트 깊이 (0 = 최상위/단락). 단락은 항상 0. 하위호환 위해 기본값 0 —
// depth 없던 구버전 body_runs JSON은 역직렬화 시 0으로 채워진다.
public sealed record NoteLine(NoteLineKind Kind, IReadOnlyList<NoteRun> Runs, int Depth = 0);

// Pure block-JSON → intermediate-model conversion for the pull direction.
// No WPF/WinForms references — MTA xUnit testable.
// ToPlainText는 push 쪽 NotionClient.BuildParagraphBlocks의 관례를 그대로 역으로 따라야
// round-trip이 안정됨: "• " 접두 = bullet, "N. " 접두 = numbered, 그 외 = paragraph.
public static class NotionBlockConverter
{
    private static readonly string[] SupportedTypes =
        ["paragraph", "bulleted_list_item", "numbered_list_item"];

    // Returns (true, null) when every block is within the Noticker vocabulary.
    // A block with has_children=true is out of scope and reported as "nested {type}";
    // any other unsupported block is reported by its type string.
    public static (bool Supported, string? UnsupportedType) CheckVocabulary(JsonElement blocksArray)
    {
        if (blocksArray.ValueKind != JsonValueKind.Array)
            return (false, "malformed");

        foreach (var block in blocksArray.EnumerateArray())
        {
            var type = TryGetProp(block, "type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() ?? "unknown"
                : "unknown";

            if (TryGetProp(block, "has_children", out var hc) && hc.ValueKind == JsonValueKind.True)
                return (false, "nested " + type);

            if (!SupportedTypes.Contains(type))
                return (false, type);
        }

        return (true, null);
    }

    // 가져오기 경고용 — 스티커가 왕복하지 못하는 서식(기울임/취소선/코드/색)이 있는가.
    // bold/underline은 push가 annotation으로 보존하므로 경고 대상이 아니다
    public static bool HasUnsupportedAnnotations(JsonElement blocksArray)
    {
        if (blocksArray.ValueKind != JsonValueKind.Array) return false;
        foreach (var block in blocksArray.EnumerateArray())
            if (BlockHasUnsupportedAnnotations(block)) return true;
        return false;
    }

    // 재귀 fetch 트리(depth 태그)용 — 중첩 import의 서식 경고 게이트.
    public static bool HasUnsupportedAnnotations(IReadOnlyList<PulledBlock> blocks)
    {
        foreach (var pb in blocks)
            if (BlockHasUnsupportedAnnotations(pb.Block)) return true;
        return false;
    }

    private static bool BlockHasUnsupportedAnnotations(JsonElement block)
    {
        var type = TryGetProp(block, "type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;
        if (type is null || !TryGetProp(block, type, out var payload) ||
            !TryGetProp(payload, "rich_text", out var richText) ||
            richText.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var rt in richText.EnumerateArray())
        {
            if (!TryGetProp(rt, "annotations", out var ann)) continue;
            foreach (var flag in new[] { "italic", "strikethrough", "code" })
            {
                if (TryGetProp(ann, flag, out var v) && v.ValueKind == JsonValueKind.True)
                    return true;
            }
            if (TryGetProp(ann, "color", out var c) && c.ValueKind == JsonValueKind.String &&
                c.GetString() is string color && color != "default")
                return true;
        }
        return false;
    }

    // 재귀 fetch 결과 — Notion 블록 + 중첩 깊이(pull 정본). 클라이언트가 has_children를 따라
    // 전위순회로 채운다.
    public sealed record PulledBlock(JsonElement Block, int Depth);

    // Converts a block array into lines of formatted runs (one run per rich_text element).
    // Empty rich_text → line with zero runs. Blocks outside the vocabulary are skipped
    // defensively (CheckVocabulary is expected to have run first). depth 0 평면 (import 경로).
    public static List<NoteLine> ToLines(JsonElement blocksArray)
    {
        var lines = new List<NoteLine>();
        if (blocksArray.ValueKind != JsonValueKind.Array) return lines;
        foreach (var block in blocksArray.EnumerateArray())
            if (ParseBlock(block, 0) is { } line) lines.Add(line);
        return lines;
    }

    // depth 태그 블록(재귀 fetch) → 중첩 NoteLine. pull이 Notion 중첩을 앱 중첩으로 복원.
    public static List<NoteLine> ToLines(IReadOnlyList<PulledBlock> blocks)
    {
        var lines = new List<NoteLine>(blocks.Count);
        foreach (var pb in blocks)
            if (ParseBlock(pb.Block, pb.Depth) is { } line) lines.Add(line);
        return lines;
    }

    // 단일 Notion 블록 → NoteLine(depth 부여). 미지원 타입이면 null.
    private static NoteLine? ParseBlock(JsonElement block, int depth)
    {
        var type = TryGetProp(block, "type", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString()
            : null;

        NoteLineKind? kind = type switch
        {
            "paragraph" => NoteLineKind.Paragraph,
            "bulleted_list_item" => NoteLineKind.Bullet,
            "numbered_list_item" => NoteLineKind.Number,
            _ => null,
        };
        if (kind is null) return null;

        var runs = new List<NoteRun>();
        if (TryGetProp(block, type!, out var payload) &&
            TryGetProp(payload, "rich_text", out var richText) &&
            richText.ValueKind == JsonValueKind.Array)
        {
            foreach (var rt in richText.EnumerateArray())
            {
                bool bold = false, underline = false;
                if (TryGetProp(rt, "annotations", out var ann))
                {
                    bold = TryGetProp(ann, "bold", out var b) && b.ValueKind == JsonValueKind.True;
                    underline = TryGetProp(ann, "underline", out var u) && u.ValueKind == JsonValueKind.True;
                }
                runs.Add(new NoteRun(ExtractText(rt), bold, underline));
            }
        }

        // 단락은 깊이 개념 없음 — 항상 0. 리스트만 depth 부여.
        return new NoteLine(kind.Value, runs, kind == NoteLineKind.Paragraph ? 0 : depth);
    }

    // Renders lines back to the plain Body convention used by push (BuildParagraphBlocks):
    // bullets get "• ", numbered lines get "{n}. ", paragraphs are bare. Formatting is dropped.
    public static string ToPlainText(IReadOnlyList<NoteLine> lines)
    {
        var sb = new StringBuilder();
        int number = 0; // 연속된 번호 줄에서만 증가 — 끊기면 1부터 재시작 (Notion 렌더링과 동일)

        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) sb.Append('\n');

            var line = lines[i];
            var text = string.Concat(line.Runs.Select(r => r.Text));

            switch (line.Kind)
            {
                case NoteLineKind.Bullet:
                    number = 0;
                    sb.Append("• ").Append(text);
                    break;
                case NoteLineKind.Number:
                    number++;
                    sb.Append(number).Append(". ").Append(text);
                    break;
                default:
                    number = 0;
                    sb.Append(text);
                    break;
            }
        }

        return sb.ToString();
    }

    // Pull responses carry plain_text on every rich_text element; fall back to text.content.
    // Notion의 soft line break(shift+enter)는 plain_text 안의 '\n'으로 옴 — 그대로 두면
    // 다음 push가 한 블록을 둘로 쪼갠다 (구조 변형) → 공백으로 평탄화
    private static string ExtractText(JsonElement richText)
    {
        string raw = "";
        if (TryGetProp(richText, "plain_text", out var pt) && pt.ValueKind == JsonValueKind.String)
            raw = pt.GetString() ?? "";
        else if (TryGetProp(richText, "text", out var textObj) &&
                 TryGetProp(textObj, "content", out var content) &&
                 content.ValueKind == JsonValueKind.String)
            raw = content.GetString() ?? "";
        return raw.Replace('\n', ' ');
    }

    // JsonElement.TryGetProperty throws on non-object elements — guard ValueKind first.
    private static bool TryGetProp(JsonElement el, string name, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value))
            return true;
        value = default;
        return false;
    }
}

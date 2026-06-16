using System.Text.Json;
using System.Text.RegularExpressions;
using Noticker.Sync;

namespace Noticker.Tests;

public class NotionBlockConverterTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // Minimal Notion block JSON, push-shaped (text.content) unless richText is given raw.
    private static string Block(string type, string content, bool hasChildren = false) =>
        $$$"""
        {"type":"{{{type}}}","has_children":{{{(hasChildren ? "true" : "false")}}},
         "{{{type}}}":{"rich_text":[{"type":"text","text":{"content":"{{{content}}}"},"plain_text":"{{{content}}}"}]}}
        """;

    // ── CheckVocabulary ────────────────────────────────────────────────────────

    [Fact]
    public void CheckVocabulary_AllSupportedTypes_ReturnsSupported()
    {
        var blocks = Parse($"[{Block("paragraph", "a")},{Block("bulleted_list_item", "b")},{Block("numbered_list_item", "c")}]");

        var (supported, unsupportedType) = NotionBlockConverter.CheckVocabulary(blocks);

        Assert.True(supported);
        Assert.Null(unsupportedType);
    }

    [Fact]
    public void CheckVocabulary_EmptyArray_ReturnsSupported()
    {
        var (supported, unsupportedType) = NotionBlockConverter.CheckVocabulary(Parse("[]"));

        Assert.True(supported);
        Assert.Null(unsupportedType);
    }

    [Theory]
    [InlineData("heading_1")]
    [InlineData("toggle")]
    [InlineData("image")]
    [InlineData("callout")]
    [InlineData("to_do")]
    public void CheckVocabulary_UnsupportedType_ReportsTypeString(string type)
    {
        var blocks = Parse($"[{Block("paragraph", "ok")},{Block(type, "x")}]");

        var (supported, unsupportedType) = NotionBlockConverter.CheckVocabulary(blocks);

        Assert.False(supported);
        Assert.Equal(type, unsupportedType);
    }

    [Theory]
    [InlineData("paragraph")]
    [InlineData("bulleted_list_item")]
    [InlineData("numbered_list_item")]
    [InlineData("toggle")]
    public void CheckVocabulary_HasChildren_ReportsNestedType(string type)
    {
        var blocks = Parse($"[{Block(type, "x", hasChildren: true)}]");

        var (supported, unsupportedType) = NotionBlockConverter.CheckVocabulary(blocks);

        Assert.False(supported);
        Assert.Equal("nested " + type, unsupportedType);
    }

    [Fact]
    public void CheckVocabulary_MissingTypeProperty_ReportsUnknown_DoesNotThrow()
    {
        var blocks = Parse("""[{"has_children":false}]""");

        var (supported, unsupportedType) = NotionBlockConverter.CheckVocabulary(blocks);

        Assert.False(supported);
        Assert.Equal("unknown", unsupportedType);
    }

    [Fact]
    public void CheckVocabulary_NonArrayInput_ReportsMalformed_DoesNotThrow()
    {
        var (supported, unsupportedType) = NotionBlockConverter.CheckVocabulary(Parse("""{"object":"error"}"""));

        Assert.False(supported);
        Assert.Equal("malformed", unsupportedType);
    }

    [Fact]
    public void CheckVocabulary_NonObjectBlockElement_DoesNotThrow()
    {
        var (supported, _) = NotionBlockConverter.CheckVocabulary(Parse("""["garbage", 42]"""));

        Assert.False(supported);
    }

    // ── ToLines ────────────────────────────────────────────────────────────────

    [Fact]
    public void ToLines_MapsBlockTypesToLineKinds()
    {
        var blocks = Parse($"[{Block("paragraph", "p")},{Block("bulleted_list_item", "b")},{Block("numbered_list_item", "n")}]");

        var lines = NotionBlockConverter.ToLines(blocks);

        Assert.Equal(3, lines.Count);
        Assert.Equal(NoteLineKind.Paragraph, lines[0].Kind);
        Assert.Equal(NoteLineKind.Bullet, lines[1].Kind);
        Assert.Equal(NoteLineKind.Number, lines[2].Kind);
        Assert.Equal("p", lines[0].Runs.Single().Text);
        Assert.Equal("b", lines[1].Runs.Single().Text);
        Assert.Equal("n", lines[2].Runs.Single().Text);
    }

    [Fact]
    public void ToLines_PreservesRunBoundariesAndAnnotations()
    {
        var blocks = Parse("""
            [{"type":"paragraph","has_children":false,"paragraph":{"rich_text":[
                {"plain_text":"plain ","annotations":{"bold":false,"underline":false}},
                {"plain_text":"bold","annotations":{"bold":true,"underline":false}},
                {"plain_text":" both","annotations":{"bold":true,"underline":true}},
                {"plain_text":" under","annotations":{"bold":false,"underline":true}}
            ]}}]
            """);

        var lines = NotionBlockConverter.ToLines(blocks);

        var runs = Assert.Single(lines).Runs;
        Assert.Equal(4, runs.Count);
        Assert.Equal(new NoteRun("plain ", false, false), runs[0]);
        Assert.Equal(new NoteRun("bold", true, false), runs[1]);
        Assert.Equal(new NoteRun(" both", true, true), runs[2]);
        Assert.Equal(new NoteRun(" under", false, true), runs[3]);
    }

    [Fact]
    public void ToLines_EmptyRichText_ProducesEmptyLine()
    {
        var blocks = Parse("""[{"type":"paragraph","has_children":false,"paragraph":{"rich_text":[]}}]""");

        var lines = NotionBlockConverter.ToLines(blocks);

        Assert.Single(lines);
        Assert.Equal(NoteLineKind.Paragraph, lines[0].Kind);
        Assert.Empty(lines[0].Runs);
    }

    [Fact]
    public void ToLines_FallsBackToTextContent_WhenPlainTextMissing()
    {
        var blocks = Parse("""[{"type":"paragraph","paragraph":{"rich_text":[{"text":{"content":"fallback"}}]}}]""");

        var lines = NotionBlockConverter.ToLines(blocks);

        Assert.Equal("fallback", lines.Single().Runs.Single().Text);
    }

    [Fact]
    public void ToLines_MalformedBlocks_DoNotThrow()
    {
        // payload 누락, rich_text 누락, rich_text 비배열, run 비객체, annotations 비객체
        var blocks = Parse("""
            [{"type":"paragraph"},
             {"type":"bulleted_list_item","bulleted_list_item":{}},
             {"type":"paragraph","paragraph":{"rich_text":"oops"}},
             {"type":"paragraph","paragraph":{"rich_text":[42,"str"]}},
             {"type":"paragraph","paragraph":{"rich_text":[{"plain_text":"ok","annotations":"bad"}]}},
             "garbage"]
            """);

        var lines = NotionBlockConverter.ToLines(blocks);

        Assert.Equal(5, lines.Count); // 비객체 블록("garbage")만 스킵
        Assert.Empty(lines[0].Runs);
        Assert.Empty(lines[1].Runs);
        Assert.Empty(lines[2].Runs);
        Assert.Equal(new[] { "", "" }, lines[3].Runs.Select(r => r.Text));
        Assert.Equal(new NoteRun("ok", false, false), lines[4].Runs.Single());
    }

    [Fact]
    public void ToLines_NonArrayInput_ReturnsEmptyList()
    {
        Assert.Empty(NotionBlockConverter.ToLines(Parse("""{"object":"error"}""")));
    }

    [Fact]
    public void ToLines_SkipsUnsupportedTypesDefensively()
    {
        var blocks = Parse($"[{Block("heading_1", "h")},{Block("paragraph", "p")}]");

        var lines = NotionBlockConverter.ToLines(blocks);

        Assert.Single(lines);
        Assert.Equal("p", lines[0].Runs.Single().Text);
    }

    // ── ToPlainText (push 관례 미러: "• " / "N. " / bare) ──────────────────────

    private static NoteLine Line(NoteLineKind kind, string text) =>
        new(kind, text.Length == 0 ? [] : [new NoteRun(text, false, false)]);

    [Fact]
    public void ToPlainText_BulletGetsPushPrefix()
    {
        var text = NotionBlockConverter.ToPlainText([Line(NoteLineKind.Bullet, "item")]);

        Assert.Equal("• item", text);
    }

    [Fact]
    public void ToPlainText_NumbersAreSequential_AndResetAfterBreak()
    {
        var text = NotionBlockConverter.ToPlainText(
        [
            Line(NoteLineKind.Number, "a"),
            Line(NoteLineKind.Number, "b"),
            Line(NoteLineKind.Paragraph, "break"),
            Line(NoteLineKind.Number, "c"),
        ]);

        Assert.Equal("1. a\n2. b\nbreak\n1. c", text);
    }

    [Fact]
    public void ToPlainText_ConcatenatesRuns_DroppingFormatting()
    {
        var line = new NoteLine(NoteLineKind.Paragraph,
            [new NoteRun("a", true, false), new NoteRun("b", false, true), new NoteRun("c", false, false)]);

        Assert.Equal("abc", NotionBlockConverter.ToPlainText([line]));
    }

    [Fact]
    public void ToPlainText_EmptyLinesAndEmptyList()
    {
        Assert.Equal("", NotionBlockConverter.ToPlainText([]));
        Assert.Equal("\n", NotionBlockConverter.ToPlainText(
            [Line(NoteLineKind.Paragraph, ""), Line(NoteLineKind.Paragraph, "")]));
    }

    [Fact]
    public void ToPlainText_RoundTripsThroughPushLineClassification()
    {
        // NotionClient.BuildParagraphBlocks의 줄 분류 규칙을 그대로 적용했을 때
        // 원래의 (종류, 텍스트)로 되돌아와야 round-trip이 안정됨.
        var original = new List<NoteLine>
        {
            Line(NoteLineKind.Paragraph, "plain text"),
            Line(NoteLineKind.Bullet, "first bullet"),
            Line(NoteLineKind.Bullet, ""),
            Line(NoteLineKind.Number, "one"),
            Line(NoteLineKind.Number, "two"),
            Line(NoteLineKind.Paragraph, ""),
            Line(NoteLineKind.Number, "restarted"),
        };

        var body = NotionBlockConverter.ToPlainText(original);
        var reparsed = body.Split('\n').Select(ClassifyLikePush).ToList();

        Assert.Equal(
            original.Select(l => (l.Kind, string.Concat(l.Runs.Select(r => r.Text)))),
            reparsed);
    }

    // Mirror of the push-side convention in NotionClient.BuildParagraphBlocks.
    private static (NoteLineKind Kind, string Text) ClassifyLikePush(string line)
    {
        if (line.StartsWith("• "))
            return (NoteLineKind.Bullet, line[2..]);
        var m = Regex.Match(line, @"^\d+\. (.*)$");
        if (m.Success)
            return (NoteLineKind.Number, m.Groups[1].Value);
        return (NoteLineKind.Paragraph, line);
    }

    [Fact]
    public void PullThenToPlainText_MatchesPushBodyConvention()
    {
        // Notion이 돌려주는 형태의 블록 → ToLines → ToPlainText가 push가 만들었을 Body와 일치
        var blocks = Parse($"[{Block("paragraph", "title line")},{Block("bulleted_list_item", "milk")},{Block("numbered_list_item", "step")}]");

        var body = NotionBlockConverter.ToPlainText(NotionBlockConverter.ToLines(blocks));

        Assert.Equal("title line\n• milk\n1. step", body);
    }

    // ── HasUnsupportedAnnotations ──────────────────────────────────────────────
    // bold/underline은 push가 annotation으로 왕복하므로 더 이상 경고 대상이 아니다

    private static string AnnotatedBlock(string flag) =>
        $$$"""
        [{"type":"paragraph","has_children":false,
          "paragraph":{"rich_text":[{"plain_text":"x","annotations":{"{{{flag}}}":true}}]}}]
        """;

    [Theory]
    [InlineData("bold")]
    [InlineData("underline")]
    public void HasUnsupportedAnnotations_BoldUnderline_False(string flag)
    {
        Assert.False(NotionBlockConverter.HasUnsupportedAnnotations(Parse(AnnotatedBlock(flag))));
    }

    [Theory]
    [InlineData("italic")]
    [InlineData("strikethrough")]
    [InlineData("code")]
    public void HasUnsupportedAnnotations_OtherFlags_True(string flag)
    {
        Assert.True(NotionBlockConverter.HasUnsupportedAnnotations(Parse(AnnotatedBlock(flag))));
    }

    [Fact]
    public void HasUnsupportedAnnotations_NonDefaultColor_True()
    {
        var blocks = Parse("""
            [{"type":"paragraph","has_children":false,
              "paragraph":{"rich_text":[{"plain_text":"x","annotations":{"color":"red"}}]}}]
            """);
        Assert.True(NotionBlockConverter.HasUnsupportedAnnotations(blocks));
    }

    [Fact]
    public void HasUnsupportedAnnotations_NoAnnotations_False()
    {
        var blocks = Parse("""
            [{"type":"paragraph","has_children":false,
              "paragraph":{"rich_text":[{"plain_text":"x"}]}}]
            """);
        Assert.False(NotionBlockConverter.HasUnsupportedAnnotations(blocks));
    }

    // ── ToLines(depth 태그) — 재귀 fetch 결과를 중첩 NoteLine으로 (PR3) ──

    [Fact]
    public void ToLines_DepthTagged_PreservesKindAndDepth()
    {
        var blocks = new List<NotionBlockConverter.PulledBlock>
        {
            new(Parse(Block("bulleted_list_item", "부모")), 0),
            new(Parse(Block("bulleted_list_item", "자식")), 1),
            new(Parse(Block("numbered_list_item", "번호손자")), 2),
        };

        var lines = NotionBlockConverter.ToLines(blocks);

        Assert.Equal(3, lines.Count);
        Assert.Equal((NoteLineKind.Bullet, 0, "부모"), (lines[0].Kind, lines[0].Depth, lines[0].Runs[0].Text));
        Assert.Equal((NoteLineKind.Bullet, 1, "자식"), (lines[1].Kind, lines[1].Depth, lines[1].Runs[0].Text));
        Assert.Equal((NoteLineKind.Number, 2, "번호손자"), (lines[2].Kind, lines[2].Depth, lines[2].Runs[0].Text));
    }

    [Fact]
    public void ToLines_DepthTagged_ParagraphForcedToDepthZero()
    {
        // 단락은 깊이 개념이 없으므로 depth가 들어와도 0으로 정규화
        var blocks = new List<NotionBlockConverter.PulledBlock>
        {
            new(Parse(Block("paragraph", "단락")), 3),
        };

        var lines = NotionBlockConverter.ToLines(blocks);

        Assert.Single(lines);
        Assert.Equal(NoteLineKind.Paragraph, lines[0].Kind);
        Assert.Equal(0, lines[0].Depth);
    }
}

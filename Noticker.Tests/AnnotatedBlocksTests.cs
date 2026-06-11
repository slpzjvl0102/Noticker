using System.Text.Json;
using Noticker.Sync;

namespace Noticker.Tests;

// BuildAnnotatedBlocks의 출력(익명 객체)을 JSON으로 직렬화해 형태를 검증
public class AnnotatedBlocksTests
{
    private static JsonElement Build(params NoteLine[] lines)
    {
        var json = JsonSerializer.Serialize(NotionClient.BuildAnnotatedBlocks(lines));
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void PlainRun_OmitsAnnotations()
    {
        var root = Build(new NoteLine(NoteLineKind.Paragraph, [new NoteRun("plain", false, false)]));

        var rt = root[0].GetProperty("paragraph").GetProperty("rich_text")[0];
        Assert.Equal("plain", rt.GetProperty("text").GetProperty("content").GetString());
        Assert.False(rt.TryGetProperty("annotations", out _));
    }

    [Fact]
    public void FormattedRuns_CarryAnnotations()
    {
        var root = Build(new NoteLine(NoteLineKind.Paragraph,
            [new NoteRun("b", true, false), new NoteRun("u", false, true), new NoteRun("bu", true, true)]));

        var rts = root[0].GetProperty("paragraph").GetProperty("rich_text");
        Assert.True(rts[0].GetProperty("annotations").GetProperty("bold").GetBoolean());
        Assert.False(rts[0].GetProperty("annotations").GetProperty("underline").GetBoolean());
        Assert.True(rts[1].GetProperty("annotations").GetProperty("underline").GetBoolean());
        Assert.True(rts[2].GetProperty("annotations").GetProperty("bold").GetBoolean());
        Assert.True(rts[2].GetProperty("annotations").GetProperty("underline").GetBoolean());
    }

    [Fact]
    public void Kinds_MapToBlockTypes()
    {
        var root = Build(
            new NoteLine(NoteLineKind.Paragraph, [new NoteRun("p", false, false)]),
            new NoteLine(NoteLineKind.Bullet, [new NoteRun("b", false, false)]),
            new NoteLine(NoteLineKind.Number, [new NoteRun("n", false, false)]));

        Assert.Equal("paragraph", root[0].GetProperty("type").GetString());
        Assert.Equal("bulleted_list_item", root[1].GetProperty("type").GetString());
        Assert.Equal("numbered_list_item", root[2].GetProperty("type").GetString());
    }

    [Fact]
    public void EmptyLine_HasEmptyRichText()
    {
        var root = Build(new NoteLine(NoteLineKind.Paragraph, []));
        Assert.Equal(0, root[0].GetProperty("paragraph").GetProperty("rich_text").GetArrayLength());
    }

    [Fact]
    public void LongRun_SplitsIntoChunks_EachKeepingAnnotations()
    {
        var root = Build(new NoteLine(NoteLineKind.Paragraph,
            [new NoteRun(new string('가', 2500), true, false)]));

        var rts = root[0].GetProperty("paragraph").GetProperty("rich_text");
        Assert.Equal(2, rts.GetArrayLength());
        Assert.Equal(2000, rts[0].GetProperty("text").GetProperty("content").GetString()!.Length);
        Assert.Equal(500, rts[1].GetProperty("text").GetProperty("content").GetString()!.Length);
        Assert.True(rts[0].GetProperty("annotations").GetProperty("bold").GetBoolean());
        Assert.True(rts[1].GetProperty("annotations").GetProperty("bold").GetBoolean());
    }

    [Fact]
    public void EmptyTextRun_IsDropped()
    {
        var root = Build(new NoteLine(NoteLineKind.Paragraph,
            [new NoteRun("", true, false), new NoteRun("a", false, false)]));

        var rts = root[0].GetProperty("paragraph").GetProperty("rich_text");
        Assert.Equal(1, rts.GetArrayLength());
        Assert.Equal("a", rts[0].GetProperty("text").GetProperty("content").GetString());
    }
}

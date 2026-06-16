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

    [Fact]
    public void Over100Runs_DegradesLineToPlain()
    {
        var runs = Enumerable.Range(0, 150)
            .Select(i => new NoteRun("a", i % 2 == 0, false)).ToList();
        var root = Build(new NoteLine(NoteLineKind.Paragraph, runs));

        var rts = root[0].GetProperty("paragraph").GetProperty("rich_text");
        Assert.True(rts.GetArrayLength() <= 100);
        Assert.Equal(new string('a', 150),
            string.Concat(Enumerable.Range(0, rts.GetArrayLength())
                .Select(i => rts[i].GetProperty("text").GetProperty("content").GetString())));
        Assert.False(rts[0].TryGetProperty("annotations", out _));
    }

    // ── PR2: depth 태그 시퀀스 → 블록 forest (Notion 중첩 push의 트리 조립) ──

    private static string TypeOf(NotionClient.BlockNode node) =>
        JsonDocument.Parse(JsonSerializer.Serialize(node.Block))
            .RootElement.GetProperty("type").GetString()!;

    [Fact]
    public void BuildForest_NestsByDepth()
    {
        var forest = NotionClient.BuildForest(
        [
            new(NoteLineKind.Bullet, [new NoteRun("부모", false, false)], 0),
            new(NoteLineKind.Bullet, [new NoteRun("자식", false, false)], 1),
            new(NoteLineKind.Bullet, [new NoteRun("손자", false, false)], 2),
            new(NoteLineKind.Bullet, [new NoteRun("다시", false, false)], 0),
            new(NoteLineKind.Number, [new NoteRun("번호", false, false)], 1),
        ]);

        Assert.Equal(2, forest.Count);                          // 루트 2 (부모, 다시)
        Assert.Equal("bulleted_list_item", TypeOf(forest[0]));
        Assert.Single(forest[0].Children);                       // 부모 → 자식
        Assert.Single(forest[0].Children[0].Children);           // 자식 → 손자
        Assert.Empty(forest[0].Children[0].Children[0].Children); // 손자 leaf

        Assert.Single(forest[1].Children);                       // 다시 → 번호
        Assert.Equal("numbered_list_item", TypeOf(forest[1].Children[0]));
    }

    [Fact]
    public void BuildForest_DepthJump_ClampsToDeepestAvailable()
    {
        // depth 0 → 2 (1 건너뜀) — 2를 가능한 가장 깊은 곳(depth 1)에 붙여 방어
        var forest = NotionClient.BuildForest(
        [
            new(NoteLineKind.Bullet, [new NoteRun("a", false, false)], 0),
            new(NoteLineKind.Bullet, [new NoteRun("b", false, false)], 2),
        ]);

        Assert.Single(forest);
        Assert.Single(forest[0].Children);   // b가 a의 자식으로 클램프
    }

    [Fact]
    public void BuildForest_FlatLines_AllRoots()
    {
        var forest = NotionClient.BuildForest(
        [
            new(NoteLineKind.Bullet, [new NoteRun("1", false, false)]),
            new(NoteLineKind.Bullet, [new NoteRun("2", false, false)]),
            new(NoteLineKind.Paragraph, [new NoteRun("3", false, false)]),
        ]);

        Assert.Equal(3, forest.Count);
        Assert.All(forest, n => Assert.Empty(n.Children));
    }
}

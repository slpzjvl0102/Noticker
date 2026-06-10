using System.Text.Json;
using Noticker.Sync;

namespace Noticker.Tests;

public class NotionDirectoryTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── ParseDatabaseList ──────────────────────────────────────────────────────

    [Fact]
    public void ParseDatabaseList_TwoDatabases_ReturnsIdAndConcatenatedTitle()
    {
        var root = Parse("""
            {"results":[
              {"id":"aaa-111","title":[{"plain_text":"업무 "},{"plain_text":"노트"}]},
              {"id":"bbb-222","title":[{"plain_text":"Reading List"}]}
            ],"has_more":false}
            """);

        var list = NotionDirectory.ParseDatabaseList(root);

        Assert.Equal(2, list.Count);
        Assert.Equal(("aaa-111", "업무 노트"), list[0]);
        Assert.Equal(("bbb-222", "Reading List"), list[1]);
    }

    [Fact]
    public void ParseDatabaseList_UntitledDatabase_FallsBackToPlaceholder()
    {
        var root = Parse("""{"results":[{"id":"ccc","title":[]}],"has_more":false}""");

        var list = NotionDirectory.ParseDatabaseList(root);

        Assert.Equal("(제목 없음)", list[0].Title);
    }

    [Fact]
    public void ParseDatabaseList_MissingTitleProperty_FallsBackToPlaceholder()
    {
        var root = Parse("""{"results":[{"id":"ddd"}],"has_more":false}""");

        var list = NotionDirectory.ParseDatabaseList(root);

        Assert.Equal("(제목 없음)", list[0].Title);
    }

    [Fact]
    public void ParseDatabaseList_EmptyResults_ReturnsEmpty()
    {
        Assert.Empty(NotionDirectory.ParseDatabaseList(Parse("""{"results":[],"has_more":false}""")));
    }

    [Fact]
    public void ParseDatabaseList_MissingResults_ReturnsEmpty()
    {
        Assert.Empty(NotionDirectory.ParseDatabaseList(Parse("{}")));
    }

    // ── ParseSelectPropertyNames ───────────────────────────────────────────────

    [Fact]
    public void ParseSelectPropertyNames_MixedTypes_ReturnsSelectOnly()
    {
        // multi_select 제외 — push(BuildProperties)가 select 단일 값만 쓴다
        var root = Parse("""
            {"properties":{
              "Name":{"type":"title"},
              "Category":{"type":"select"},
              "Tags":{"type":"multi_select"},
              "Status":{"type":"select"}
            }}
            """);

        var names = NotionDirectory.ParseSelectPropertyNames(root);

        Assert.Equal(new[] { "Category", "Status" }, names);
    }

    [Fact]
    public void ParseSelectPropertyNames_NoSelectProperty_ReturnsEmpty()
    {
        var root = Parse("""{"properties":{"Name":{"type":"title"}}}""");

        Assert.Empty(NotionDirectory.ParseSelectPropertyNames(root));
    }

    [Fact]
    public void ParseSelectPropertyNames_MissingProperties_ReturnsEmpty()
    {
        Assert.Empty(NotionDirectory.ParseSelectPropertyNames(Parse("{}")));
    }
}

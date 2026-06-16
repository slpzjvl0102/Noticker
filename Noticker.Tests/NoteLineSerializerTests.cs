using Noticker.Sync;

namespace Noticker.Tests;

public class NoteLineSerializerTests
{
    private static List<NoteLine> Sample() =>
    [
        new(NoteLineKind.Paragraph, [new NoteRun("plain ", false, false), new NoteRun("굵게🔥", true, false)]),
        new(NoteLineKind.Bullet, [new NoteRun("밑줄", false, true)]),
        new(NoteLineKind.Number, [new NoteRun("둘 다", true, true)]),
        new(NoteLineKind.Paragraph, []),
    ];

    [Fact]
    public void RoundTrip_PreservesKindsRunsAndFlags()
    {
        var json = NoteLineSerializer.Serialize(Sample());
        var back = NoteLineSerializer.Deserialize(json);

        Assert.NotNull(back);
        Assert.Equal(Sample().Count, back!.Count);
        for (int i = 0; i < back.Count; i++)
        {
            Assert.Equal(Sample()[i].Kind, back[i].Kind);
            Assert.Equal(Sample()[i].Runs, back[i].Runs);
        }
    }

    [Fact]
    public void Serialize_UsesCompactKindAndRunKeys()
    {
        var json = NoteLineSerializer.Serialize(
            [new(NoteLineKind.Bullet, [new NoteRun("a", true, false)])]);

        Assert.Contains("\"Kind\":\"bullet\"", json);
        Assert.Contains("\"T\":\"a\"", json);
        Assert.Contains("\"B\":true", json);
        Assert.Contains("\"U\":false", json);
    }

    [Fact]
    public void Serialize_DoesNotEscapeKorean()
    {
        var json = NoteLineSerializer.Serialize(
            [new(NoteLineKind.Paragraph, [new NoteRun("한글", false, false)])]);
        Assert.Contains("한글", json);
        Assert.DoesNotContain("\\u", json);
    }

    [Fact]
    public void RoundTrip_EmptyList()
    {
        Assert.Equal([], NoteLineSerializer.Deserialize(NoteLineSerializer.Serialize([])));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"oops\":1}")]
    [InlineData("[{\"Kind\":\"unknown\",\"Runs\":[]}]")]
    [InlineData("[{\"Kind\":\"paragraph\"}]")]
    [InlineData("[null]")]
    [InlineData("[{\"Kind\":\"paragraph\",\"Runs\":[null]}]")]
    public void Deserialize_InvalidInput_ReturnsNull(string? json)
    {
        Assert.Null(NoteLineSerializer.Deserialize(json));
    }

    // ── 중첩 깊이(Depth) ─────────────────────────────────────────────

    [Fact]
    public void RoundTrip_PreservesDepth()
    {
        List<NoteLine> lines =
        [
            new(NoteLineKind.Bullet, [new NoteRun("부모", false, false)], 0),
            new(NoteLineKind.Bullet, [new NoteRun("자식", false, false)], 1),
            new(NoteLineKind.Bullet, [new NoteRun("손자", false, false)], 2),
            new(NoteLineKind.Number, [new NoteRun("번호 자식", false, false)], 1),
        ];
        var back = NoteLineSerializer.Deserialize(NoteLineSerializer.Serialize(lines));

        Assert.NotNull(back);
        Assert.Equal(lines.Count, back!.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            Assert.Equal(lines[i].Kind, back[i].Kind);
            Assert.Equal(lines[i].Runs, back[i].Runs);   // 요소별(NoteRun은 값 동등성)
            Assert.Equal(lines[i].Depth, back[i].Depth);
        }
    }

    [Fact]
    public void Serialize_IncludesDepthKey()
    {
        var json = NoteLineSerializer.Serialize(
            [new(NoteLineKind.Bullet, [new NoteRun("a", false, false)], 2)]);
        Assert.Contains("\"D\":2", json);
    }

    // CRITICAL 회귀: depth 필드가 없던 구버전 body_runs JSON은 Depth=0으로 읽혀야 한다
    // (마이그레이션 없이 기존 모든 스티커가 평면 0레벨로 안전하게 로드).
    [Fact]
    public void Deserialize_LegacyJsonWithoutDepth_DefaultsToZero()
    {
        const string legacy =
            "[{\"Kind\":\"bullet\",\"Runs\":[{\"T\":\"옛 항목\",\"B\":false,\"U\":false}]}]";
        var back = NoteLineSerializer.Deserialize(legacy);

        Assert.NotNull(back);
        var line = Assert.Single(back!);
        Assert.Equal(NoteLineKind.Bullet, line.Kind);
        Assert.Equal(0, line.Depth);
        Assert.Equal("옛 항목", line.Runs[0].Text);
    }
}

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
}

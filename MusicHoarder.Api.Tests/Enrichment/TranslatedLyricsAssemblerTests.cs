using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Tests.Enrichment;

public class TranslatedLyricsAssemblerTests
{
    // --- Parse ---

    [Fact]
    public void Parse_CapturesTimestampPrefixVerbatim()
    {
        var lines = TranslatedLyricsAssembler.Parse("[00:12.34]First line\n[01:02.5] Second line");

        Assert.Equal(2, lines.Count);
        Assert.Equal("[00:12.34]", lines[0].TagPrefix);
        Assert.Equal("First line", lines[0].Text);
        Assert.Equal("[01:02.5]", lines[1].TagPrefix);
        Assert.Equal("Second line", lines[1].Text);
    }

    [Fact]
    public void Parse_KeepsMultiTimestampPrefix()
    {
        var lines = TranslatedLyricsAssembler.Parse("[00:10.00][01:30.00]Repeated hook");

        var line = Assert.Single(lines);
        Assert.Equal("[00:10.00][01:30.00]", line.TagPrefix);
        Assert.Equal("Repeated hook", line.Text);
    }

    [Fact]
    public void Parse_SupportsColonFractionTimestamps()
    {
        var line = Assert.Single(TranslatedLyricsAssembler.Parse("[00:12:34]Colon fraction"));

        Assert.Equal("[00:12:34]", line.TagPrefix);
        Assert.Equal("Colon fraction", line.Text);
    }

    [Fact]
    public void Parse_PlainTextGetsEmptyPrefixes()
    {
        var lines = TranslatedLyricsAssembler.Parse("First line\r\nSecond line");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Equal(string.Empty, l.TagPrefix));
        Assert.Equal("First line", lines[0].Text);
    }

    [Fact]
    public void Parse_DropsBlankAndTimestampOnlyLines()
    {
        var lines = TranslatedLyricsAssembler.Parse("[00:01.00]Sung line\n\n   \n[00:30.00]\nAnother");

        Assert.Equal(2, lines.Count);
        Assert.Equal("Sung line", lines[0].Text);
        Assert.Equal("Another", lines[1].Text);
    }

    // --- Assemble ---

    [Fact]
    public void Assemble_RoundTripsTimestampsVerbatim()
    {
        var source = TranslatedLyricsAssembler.Parse("[00:12.34]حبيبي\n[01:02.5][02:00.0]يا نور العين");

        var (synced, plain) = TranslatedLyricsAssembler.Assemble(source, ["7abibi", "ya nour el ein"]);

        Assert.Equal("[00:12.34]7abibi\n[01:02.5][02:00.0]ya nour el ein", synced);
        Assert.Equal("7abibi\nya nour el ein", plain);
    }

    [Fact]
    public void Assemble_PlainOnlySourceHasNoSynced()
    {
        var source = TranslatedLyricsAssembler.Parse("Quiero bailar\nToda la noche");

        var (synced, plain) = TranslatedLyricsAssembler.Assemble(source, ["KYEH-roh bai-LAR", "TOH-dah lah NOH-cheh"]);

        Assert.Null(synced);
        Assert.Equal("KYEH-roh bai-LAR\nTOH-dah lah NOH-cheh", plain);
    }

    [Fact]
    public void Assemble_ThrowsOnCountMismatch()
    {
        var source = TranslatedLyricsAssembler.Parse("[00:01.00]One\n[00:02.00]Two");

        Assert.Throws<ArgumentException>(() => TranslatedLyricsAssembler.Assemble(source, ["only one"]));
    }
}

using System.Text.Json;
using MusicHoarder.Api.BackgroundWork;

namespace MusicHoarder.Api.Tests.BackgroundWork;

public class BackgroundWorkContractTests
{
    private static readonly DateTime T = new(2026, 11, 25, 12, 0, 0, DateTimeKind.Utc);

    private static BackgroundJobDetailDto Detail()
    {
        var summary = BackgroundJobSummaries.Build(
            BackgroundJobDefinitions.Lyrics, null, null, null,
            new BackgroundHeadlineDto(12, 20, BackgroundFormat.Count, "of 20 songs", T, "at today's rate"),
            new double[14], "songs per day");
        return new BackgroundJobDetailDto(summary,
        [
            new BreakdownPanelDto("b", null, 3, "songs", [new BreakdownSegmentDto("k", "L", 3, BackgroundTone.Ai, null)]),
            new SeriesPanelDto("s", "c", "hour", BackgroundFormat.Duration, true,
                [new SeriesDefinitionDto("a", "A", BackgroundTone.Good)],
                [new SeriesPointDto(T, [1.5])],
                [new SeriesLimitDto("ceiling", 6000, BackgroundTone.Warning)]),
            new LibraryMapPanelDto("m", null, "Done", "Waiting", "Excluded",
                [new LibraryMapBinDto(T, T, 1, 2, 3)], [new LibraryMapMarkerDto("here", T)]),
            new BarsPanelDto("r", null, BackgroundFormat.Count, [new BarDto("0-9", 4, BackgroundTone.Bad)]),
            new FiguresPanelDto("f", null, [new FigureDto("Left", 5, BackgroundFormat.Count, null, "sub", BackgroundTone.Neutral)]),
        ]);
    }

    [Fact]
    public void Panels_are_camel_case_and_say_which_kind_they_are()
    {
        var json = JsonSerializer.Serialize(Detail(), BackgroundWorkEndpoints.Json);
        using var doc = JsonDocument.Parse(json);
        var panels = doc.RootElement.GetProperty("panels").EnumerateArray().ToList();

        Assert.Equal(
            ["breakdown", "series", "library-map", "bars", "figures"],
            panels.Select(p => p.GetProperty("kind").GetString()));
        Assert.Equal("lyrics", doc.RootElement.GetProperty("summary").GetProperty("id").GetString());
        Assert.Equal("unknown", doc.RootElement.GetProperty("summary").GetProperty("state").GetString());
        Assert.Equal(12, doc.RootElement.GetProperty("summary").GetProperty("headline").GetProperty("value").GetDouble());
        Assert.Equal(1.5, panels[1].GetProperty("points")[0].GetProperty("values")[0].GetDouble());
        Assert.Equal("2026-11-25T12:00:00Z", panels[1].GetProperty("points")[0].GetProperty("startUtc").GetString());
        Assert.Equal(1, panels[2].GetProperty("bins")[0].GetProperty("done").GetInt32());
        Assert.Equal("hour", panels[1].GetProperty("bucket").GetString());
        Assert.Equal("duration", panels[1].GetProperty("format").GetString());
        Assert.Equal("sub", panels[4].GetProperty("items")[0].GetProperty("sublabel").GetString());
    }

    [Fact]
    public void The_kind_is_the_first_property_of_each_panel()
    {
        var json = JsonSerializer.Serialize(Detail(), BackgroundWorkEndpoints.Json);
        using var doc = JsonDocument.Parse(json);

        foreach (var panel in doc.RootElement.GetProperty("panels").EnumerateArray())
            Assert.Equal("kind", panel.EnumerateObject().First().Name);
    }

    [Fact]
    public void Every_wire_id_has_a_definition()
    {
        Assert.Equal(BackgroundJobIds.All, BackgroundJobDefinitions.All.Select(d => d.Id));
    }
}

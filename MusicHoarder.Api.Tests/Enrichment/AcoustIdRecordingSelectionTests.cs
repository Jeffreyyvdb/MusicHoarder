using MusicHoarder.Api.Enrichment;

namespace MusicHoarder.Api.Tests.Enrichment;

public class AcoustIdRecordingSelectionTests
{
    private static AcoustIdService.AcoustIdRecording Rec(string id, string? title, double duration, bool withArtist = true)
        => new()
        {
            Id = id,
            Title = title,
            Duration = duration,
            Artists = withArtist ? [new AcoustIdService.AcoustIdArtist { Id = "a", Name = "Some Artist" }] : null,
        };

    [Fact]
    public void Picks_recording_whose_duration_is_closest_to_file()
    {
        // Same fingerprint cluster, three pressings of different lengths; file is ~200s.
        var recordings = new List<AcoustIdService.AcoustIdRecording>
        {
            Rec("radio-edit", "Song", 180),
            Rec("album", "Song", 201),
            Rec("extended", "Song", 420),
        };

        var picked = AcoustIdService.SelectRecording(recordings, durationSeconds: 200);

        Assert.Equal("album", picked.Id);
    }

    [Fact]
    public void Prefers_a_titled_recording_over_an_untitled_one_even_if_closer()
    {
        var recordings = new List<AcoustIdService.AcoustIdRecording>
        {
            Rec("untitled", title: null, duration: 200),
            Rec("titled", "Song", 205),
        };

        var picked = AcoustIdService.SelectRecording(recordings, durationSeconds: 200);

        Assert.Equal("titled", picked.Id);
    }

    [Fact]
    public void Recording_with_unknown_duration_loses_to_one_that_matches()
    {
        var recordings = new List<AcoustIdService.AcoustIdRecording>
        {
            Rec("unknown-duration", "Song", 0),
            Rec("matches", "Song", 198),
        };

        var picked = AcoustIdService.SelectRecording(recordings, durationSeconds: 200);

        Assert.Equal("matches", picked.Id);
    }

    [Fact]
    public void Falls_back_to_first_when_no_usable_signal()
    {
        var recordings = new List<AcoustIdService.AcoustIdRecording>
        {
            Rec("first", title: null, duration: 0, withArtist: false),
            Rec("second", title: null, duration: 0, withArtist: false),
        };

        var picked = AcoustIdService.SelectRecording(recordings, durationSeconds: 200);

        Assert.Equal("first", picked.Id);
    }
}

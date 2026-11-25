using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Download;

public class MusicVideoAutoPickTests
{
    private static readonly MusicEnricherOptions Options = new()
    {
        SourceDirectory = "/src",
        DestinationDirectory = "/dst",
    };

    private static SongMetadata Song(string? artist = "Offset", int? seconds = 200, int? ms = null) => new()
    {
        Id = 1,
        OwnerUserId = Guid.NewGuid(),
        SourcePath = "/src/1.opus",
        FileName = "1.opus",
        Extension = ".opus",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        Title = "Trickin'",
        DurationSeconds = seconds,
        DurationMs = ms,
    };

    private static CachedVideoCandidate Clip(
        string id = "vid-real",
        string title = "Offset - Trickin' (Official Video)",
        string channel = "Offset",
        int? duration = 215,
        int score = 75,
        MusicVideoMotion motion = MusicVideoMotion.RealVideo,
        long? bytes = 60_000_000,
        bool square = false) =>
        new(id, title, channel, duration, score, HasThumbnail: true, motion, bytes, square);

    [Fact]
    public void An_official_real_video_by_the_artist_is_picked()
    {
        Assert.Equal("vid-real", MusicVideoAutoPick.Choose([Clip()], Song(), Options)?.VideoId);
    }

    public static TheoryData<string, CachedVideoCandidate> FailingGates => new()
    {
        { "lyric video (low motion)", Clip(motion: MusicVideoMotion.LowMotion) },
        { "static cover", Clip(motion: MusicVideoMotion.Static) },
        { "not probed yet", Clip(motion: MusicVideoMotion.Unknown) },
        { "square source", Clip(square: true) },
        // DDG's own "Trickin'" for Offset's: another artist's song of the same name.
        { "wrong artist", Clip(title: "DDG - Trickin' (Official Video)", channel: "DDG") },
        { "score 65 (Music Video)", Clip(title: "Offset - Trickin' (Music Video)", score: 65) },
        { "unknown clip length", Clip(duration: null) },
        { "clip too short", Clip(duration: 184) },
        { "clip too long", Clip(duration: 291) },
        { "unknown size", Clip(bytes: null) },
        { "zero size", Clip(bytes: 0) },
        { "oversized clip", Clip(bytes: 401L * 1024 * 1024) },
    };

    [Theory]
    [MemberData(nameof(FailingGates))]
    public void A_candidate_failing_one_gate_is_not_picked(string gate, CachedVideoCandidate candidate)
    {
        Assert.True(MusicVideoAutoPick.Choose([candidate], Song(), Options) is null, gate);
    }

    [Theory]
    [InlineData(185, true)] // song 200 s: 15 s shorter is the limit
    [InlineData(184, false)]
    [InlineData(290, true)] // 90 s longer is the limit
    [InlineData(291, false)]
    public void The_length_window_is_fifteen_seconds_under_and_ninety_over(int clipSeconds, bool picked)
    {
        Assert.Equal(picked, MusicVideoAutoPick.Choose([Clip(duration: clipSeconds)], Song(), Options) is not null);
    }

    [Fact]
    public void The_size_limit_is_inclusive_and_follows_the_option()
    {
        var atLimit = Clip(bytes: 400L * 1024 * 1024);
        Assert.NotNull(MusicVideoAutoPick.Choose([atLimit], Song(), Options));

        var small = new MusicEnricherOptions
        {
            SourceDirectory = "/src", DestinationDirectory = "/dst", MusicVideoAutoDownloadMaxClipMb = 50,
        };
        Assert.Null(MusicVideoAutoPick.Choose([Clip(bytes: 60_000_000)], Song(), small));
    }

    [Fact]
    public void The_minimum_score_follows_the_option()
    {
        var lenient = new MusicEnricherOptions
        {
            SourceDirectory = "/src", DestinationDirectory = "/dst", MusicVideoAutoDownloadMinScore = 60,
        };
        var sixtyFive = Clip(score: 65);
        Assert.Null(MusicVideoAutoPick.Choose([sixtyFive], Song(), Options));
        Assert.NotNull(MusicVideoAutoPick.Choose([sixtyFive], Song(), lenient));
    }

    [Fact]
    public void The_second_ranked_candidate_is_picked_when_the_first_fails_a_gate()
    {
        var lyricCard = Clip(id: "vid-lyrics", motion: MusicVideoMotion.LowMotion);
        var real = Clip(id: "vid-real");
        Assert.Equal("vid-real", MusicVideoAutoPick.Choose([lyricCard, real], Song(), Options)?.VideoId);
    }

    [Fact]
    public void The_first_passing_candidate_in_rank_order_wins()
    {
        var first = Clip(id: "vid-first");
        var second = Clip(id: "vid-second");
        Assert.Equal("vid-first", MusicVideoAutoPick.Choose([first, second], Song(), Options)?.VideoId);
    }

    [Fact]
    public void The_artist_may_be_named_by_the_channel_alone()
    {
        var byChannel = Clip(title: "Trickin' (Official Video)", channel: "Offset");
        Assert.NotNull(MusicVideoAutoPick.Choose([byChannel], Song(), Options));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    public void A_song_with_no_usable_artist_picks_nothing_even_for_a_full_score(string? artist)
    {
        // NamesAnArtist answers null here, and a null must not read as a match.
        Assert.Null(MusicVideoAutoPick.Choose([Clip(score: 75)], Song(artist), Options));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    public void A_song_with_no_known_length_picks_nothing(int? seconds, int? ms)
    {
        Assert.Null(MusicVideoAutoPick.Choose([Clip()], Song(seconds: seconds, ms: ms), Options));
    }

    [Fact]
    public void The_songs_length_in_milliseconds_is_preferred_over_whole_seconds()
    {
        // 210 s by the milliseconds: a 296 s clip is inside the window, though 200 s would make it too long.
        Assert.NotNull(MusicVideoAutoPick.Choose([Clip(duration: 296)], Song(seconds: 200, ms: 210_000), Options));
        Assert.Null(MusicVideoAutoPick.Choose([Clip(duration: 296)], Song(seconds: 200), Options));
    }

    [Fact]
    public void An_empty_list_picks_nothing()
    {
        Assert.Null(MusicVideoAutoPick.Choose([], Song(), Options));
    }
}

using MusicHoarder.Api.Download;

namespace MusicHoarder.Api.Tests.Download;

public class MusicVideoDownloaderTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ&si=abc", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("dQw4w9WgXcQ", "https://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    public void CanonicalizePin_AcceptsUrlsAndBareIds(string input, string expected)
    {
        Assert.Equal(expected, MusicVideoDownloader.CanonicalizePin(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a video / id")]
    public void CanonicalizePin_RejectsUnusableInput(string? input)
    {
        Assert.Null(MusicVideoDownloader.CanonicalizePin(input));
    }

    [Theory]
    [InlineData(
        "BabyChiefDoit",
        "Lyrical Lemonade, BabyChiefDoit - Riley N Lamilton (Lunchbreak Freestyle) [Official Audio]",
        "BabyChiefDoit Riley N Lamilton (Lunchbreak Freestyle)")]
    [InlineData("Artist", "Song Name (Official Video) (4K)", "Artist Song Name")]
    [InlineData("Artist", "Song Name (Acoustic Session)", "Artist Song Name (Acoustic Session)")]
    [InlineData("Daft Punk", "Around the World", "Daft Punk Around the World")]
    [InlineData("", "Around the World", "Around the World")]
    [InlineData("Artist", "Other Band - Song Name", "Artist Other Band - Song Name")] // prefix ≠ artist: kept
    public void BuildSearchTerms_StripsUploadNoiseAndCreditPrefix(string artist, string title, string expected)
    {
        Assert.Equal(expected, MusicVideoDownloader.BuildSearchTerms(artist, title));
    }

    [Fact]
    public void BuildSearchTerms_AllNoiseTitle_FallsBackToRawTitle()
    {
        Assert.Equal("Artist [Official Audio]", MusicVideoDownloader.BuildSearchTerms("Artist", "[Official Audio]"));
    }

    [Fact]
    public void BuildFormat_CapsHeightAndPrefersMp4()
    {
        Assert.Equal(
            "bestvideo[height<=?1080][ext=mp4]+bestaudio[ext=m4a]/best[height<=?1080][ext=mp4]/best",
            MusicVideoDownloader.BuildFormat(1080));
    }

    [Theory]
    [InlineData("Song Name (Official Audio)", "Some Channel", true)]
    [InlineData("Song Name [Audio]", "Some Channel", true)]
    [InlineData("Song Name (Lyrics)", "Some Channel", true)]
    [InlineData("Song Name — Visualizer", "Some Channel", true)]
    [InlineData("Song Name", "Artist - Topic", true)]
    [InlineData("Song Name (Official Video)", "Artist", false)]
    [InlineData("Song Name (Official Music Video)", "Lyrical Lemonade", false)]
    [InlineData("Song Name", "Artist", false)]
    public void LooksLikeAudioOnlyUpload_ClassifiesUploads(string title, string channel, bool expected)
    {
        Assert.Equal(expected, MusicVideoDownloader.LooksLikeAudioOnlyUpload(title, channel));
    }

    private static IReadOnlyCollection<string> Tokens(string artist, string title) =>
        MusicVideoDownloader.TitleTokens(artist, title);

    [Fact]
    public void PickBestCandidate_PrefersOfficialVideoOverOfficialAudio()
    {
        // The search order mimics YouTube relevance: the audio upload ranks first (it's the popular
        // one), but the scorer must still pick the actual music video.
        var candidates = new List<MusicVideoDownloader.SearchCandidate>
        {
            new("aud", "Riley N Lamilton (Lunchbreak Freestyle) [Official Audio]", "Lyrical Lemonade", 212),
            new("vid", "Riley N Lamilton (Lunchbreak Freestyle) [Official Video]", "Lyrical Lemonade", 231),
            new("lyr", "Riley N Lamilton - Lyrics", "LyricsHub", 212),
        };
        var tokens = Tokens("BabyChiefDoit", "Riley N Lamilton (Lunchbreak Freestyle)");

        Assert.Equal("vid", MusicVideoDownloader.PickBestCandidate(candidates, 212_000, tokens)?.Id);
    }

    [Fact]
    public void PickBestCandidate_RejectsWrongSongOfficialVideo()
    {
        // Regression: the artist's unrelated official video must NOT win just because it is a real
        // video — with only wrong-song and audio-only options, nothing qualifies.
        var candidates = new List<MusicVideoDownloader.SearchCandidate>
        {
            new("aud", "Lyrical Lemonade, BabyChiefDoit - Riley N Lamilton (Lunchbreak Freestyle) [Official Audio]", "Lyrical Lemonade", 145),
            new("wrong", "BabyChiefDoit - WENT WEST (Official Music Video)", "Lyrical Lemonade", 163),
        };
        var tokens = Tokens(
            "BabyChiefDoit", "Lyrical Lemonade, BabyChiefDoit - Riley N Lamilton (Lunchbreak Freestyle) [Official Audio]");

        Assert.Null(MusicVideoDownloader.PickBestCandidate(candidates, 145_000, tokens));
    }

    [Fact]
    public void PickBestCandidate_PenalizesTopicChannelsAndWrongDurations()
    {
        var candidates = new List<MusicVideoDownloader.SearchCandidate>
        {
            new("topic", "Song Name", "Artist - Topic", 212),
            new("sped", "Song Name (sped up)", "NightcoreHub", 150),
            new("plain", "Song Name", "Artist", 220),
        };
        var tokens = Tokens("Artist", "Song Name");

        Assert.Equal("plain", MusicVideoDownloader.PickBestCandidate(candidates, 212_000, tokens)?.Id);
    }

    [Fact]
    public void PickBestCandidate_TieBreaksBySearchOrder()
    {
        var candidates = new List<MusicVideoDownloader.SearchCandidate>
        {
            new("first", "Song (Official Video)", "Artist", 212),
            new("second", "Song (Official Video)", "Artist", 213),
        };

        Assert.Equal("first",
            MusicVideoDownloader.PickBestCandidate(candidates, 212_000, Tokens("Artist", "Song"))?.Id);
    }

    [Fact]
    public void PickBestCandidate_EmptyList_ReturnsNull()
    {
        Assert.Null(MusicVideoDownloader.PickBestCandidate([], 200_000, Tokens("Artist", "Song")));
    }

    [Theory]
    [InlineData("Song Name (Live at Wembley)", true)]
    [InlineData("Song Name cover by somebody", true)]
    [InlineData("Alive Song Name (Official Video)", false)]
    [InlineData("Song Name Discovery", false)]
    public void ScoreCandidate_PenalizesLiveAndCovers_ButNotSubstrings(string title, bool penalized)
    {
        var tokens = Tokens("Artist", "Song Name");
        var candidate = new MusicVideoDownloader.SearchCandidate("x", title, "Artist", 212);
        var baseline = new MusicVideoDownloader.SearchCandidate("y", "Song Name", "Artist", 212);
        var delta = MusicVideoDownloader.ScoreCandidate(candidate, 212_000, tokens)
            - MusicVideoDownloader.ScoreCandidate(baseline, 212_000, tokens);
        // The official-video variants gain a bonus; the point is they are not penalized.
        Assert.Equal(penalized, delta < 0);
    }

    [Fact]
    public void ScoreCandidate_DurationNearSong_ScoresAboveWildMismatch()
    {
        var tokens = Tokens("Artist", "Song");
        var near = new MusicVideoDownloader.SearchCandidate("a", "Song", "Artist", 225);
        var wild = new MusicVideoDownloader.SearchCandidate("b", "Song", "Artist", 700);

        Assert.True(
            MusicVideoDownloader.ScoreCandidate(near, 212_000, tokens)
                > MusicVideoDownloader.ScoreCandidate(wild, 212_000, tokens));
    }

    [Fact]
    public void TitleTokens_StripNoiseArtistAndGenericWords()
    {
        var tokens = Tokens(
            "BabyChiefDoit", "Lyrical Lemonade, BabyChiefDoit - Riley N Lamilton (Lunchbreak Freestyle) [Official Audio]");

        Assert.Equal(["freestyle", "lamilton", "lunchbreak", "riley"], tokens.Order().ToArray());
        Assert.DoesNotContain("official", tokens);
        Assert.DoesNotContain("babychiefdoit", tokens);
    }

    [Fact]
    public void ParseFlatSearch_ReadsEntries_ToleratingMissingFields()
    {
        const string json = """
        {"entries":[
          {"id":"abc123","title":"Song (Official Video)","channel":"Artist","duration":231.5},
          {"id":"def456","title":"Song (Official Audio)","uploader":"Artist - Topic"},
          {"title":"no id — skipped"},
          {"id":"ghi789"}
        ]}
        """;

        var parsed = MusicVideoDownloader.ParseFlatSearch(json);

        Assert.Equal(3, parsed.Count);
        Assert.Equal(new MusicVideoDownloader.SearchCandidate("abc123", "Song (Official Video)", "Artist", 231.5), parsed[0]);
        Assert.Equal("Artist - Topic", parsed[1].Channel); // uploader fallback
        Assert.Null(parsed[1].DurationSeconds);
        Assert.Equal("", parsed[2].Title);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{\"no\":\"entries\"}")]
    public void ParseFlatSearch_UnusableJson_ReturnsEmpty(string json)
    {
        Assert.Empty(MusicVideoDownloader.ParseFlatSearch(json));
    }

    [Theory]
    [InlineData("dQw4w9WgXcQ\n212\n", "dQw4w9WgXcQ", 212)]
    [InlineData("dQw4w9WgXcQ\n212.7\n", "dQw4w9WgXcQ", 213)]
    [InlineData("dQw4w9WgXcQ\nNA\n", "dQw4w9WgXcQ", null)]
    [InlineData("dQw4w9WgXcQ\n", "dQw4w9WgXcQ", null)]
    [InlineData("", null, null)]
    public void ParsePrinted_ReadsIdAndDuration(string stdout, string? expectedId, int? expectedDuration)
    {
        var (id, duration) = MusicVideoDownloader.ParsePrinted(stdout);
        Assert.Equal(expectedId, id);
        Assert.Equal(expectedDuration, duration);
    }
}

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

    private static MusicVideoDownloader.SongTarget Target(string artist, string title) =>
        MusicVideoDownloader.SongTarget.For(artist, title);

    [Fact]
    public void PickBestCandidate_PrefersOfficialVideoOverOfficialAudio()
    {
        // The search order mimics YouTube relevance: the audio upload ranks first (it's the popular
        // one), but the scorer must still pick the actual music video.
        var candidates = new List<MusicVideoDownloader.SearchCandidate>
        {
            new("aud", "BabyChiefDoit - Riley N Lamilton (Lunchbreak Freestyle) [Official Audio]", "Lyrical Lemonade", 212),
            new("vid", "BabyChiefDoit - Riley N Lamilton (Lunchbreak Freestyle) [Official Video]", "Lyrical Lemonade", 231),
            new("lyr", "BabyChiefDoit - Riley N Lamilton - Lyrics", "LyricsHub", 212),
        };
        var target = Target("BabyChiefDoit", "Riley N Lamilton (Lunchbreak Freestyle)");

        Assert.Equal("vid", MusicVideoDownloader.PickBestCandidate(candidates, 212_000, target)?.Id);
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
        var target = Target(
            "BabyChiefDoit", "Lyrical Lemonade, BabyChiefDoit - Riley N Lamilton (Lunchbreak Freestyle) [Official Audio]");

        Assert.Null(MusicVideoDownloader.PickBestCandidate(candidates, 145_000, target));
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
        Assert.Equal("plain",
            MusicVideoDownloader.PickBestCandidate(candidates, 212_000, Target("Artist", "Song Name"))?.Id);
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
            MusicVideoDownloader.PickBestCandidate(candidates, 212_000, Target("Artist", "Song"))?.Id);
    }

    [Fact]
    public void PickBestCandidate_EmptyList_ReturnsNull()
    {
        Assert.Null(MusicVideoDownloader.PickBestCandidate([], 200_000, Target("Artist", "Song")));
    }

    [Theory]
    [InlineData("Song Name (Live at Wembley)", true)]
    [InlineData("Song Name cover by somebody", true)]
    [InlineData("Alive Song Name (Official Video)", false)]
    [InlineData("Song Name Discovery", false)]
    public void ScoreCandidate_PenalizesLiveAndCovers_ButNotSubstrings(string title, bool penalized)
    {
        var target = Target("Artist", "Song Name");
        var candidate = new MusicVideoDownloader.SearchCandidate("x", title, "Artist", 212);
        var baseline = new MusicVideoDownloader.SearchCandidate("y", "Song Name", "Artist", 212);
        var delta = MusicVideoDownloader.ScoreCandidate(candidate, 212_000, target)
            - MusicVideoDownloader.ScoreCandidate(baseline, 212_000, target);
        // The official-video variants gain a bonus; the point is they are not penalized.
        Assert.Equal(penalized, delta < 0);
    }

    [Fact]
    public void ScoreCandidate_DurationNearSong_ScoresAboveWildMismatch()
    {
        var target = Target("Artist", "Song");
        var near = new MusicVideoDownloader.SearchCandidate("a", "Song", "Artist", 225);
        var wild = new MusicVideoDownloader.SearchCandidate("b", "Song", "Artist", 700);

        Assert.True(
            MusicVideoDownloader.ScoreCandidate(near, 212_000, target)
                > MusicVideoDownloader.ScoreCandidate(wild, 212_000, target));
    }

    // Production, 2026-11-24: a same-title song by someone else took full title credit, the
    // official-video bonus and the length bonus, and won the ranking.
    [Theory]
    [InlineData("Yung Miami", "Blink Twice", "Rose Gray - Blink Twice (Official Video)", "Rose Gray")]
    [InlineData("Offset", "Trickin'", "DDG - Trickin' [Official Video]", "DDG")]
    [InlineData("Westside Gunn", "DON PISTOLA", "Guelo Star - Don Pistola (Official Video)", "MusicFireVEVO")]
    [InlineData("Gucci Mane", "Get the F Out My Face",
        "Rich Homie Quan - Get TF Out My Face ft. Young Thug (Official Video)", "RichHomieQuanVEVO")]
    [InlineData("Offset", "Pop Shit", "Gunna - pop it [Official Video]", "Gunna")]
    [InlineData("Offset", "5 Star", "Wiz Khalifa - 5 Star (feat. Gunna) [Official Music Video]", "Wiz Khalifa")]
    [InlineData("Jack Harlow", "PRESSURE", "mgk - PRESSURE (Official Music Video)", "mgk")]
    public void RankCandidates_DropsAnotherArtistsSongOfTheSameTitle(
        string artist, string title, string videoTitle, string channel)
    {
        var wrong = new MusicVideoDownloader.SearchCandidate("wrong", videoTitle, channel, 200);

        Assert.True(MusicVideoDownloader.ScoreCandidate(wrong, 200_000, Target(artist, title)) < 0);
        Assert.Empty(MusicVideoDownloader.RankCandidates([wrong], 200_000, Target(artist, title)));
    }

    [Fact]
    public void PickBestCandidate_PrefersTheArtistsVideoOverAnotherArtistsSameTitle()
    {
        // The wrong one ranks first on YouTube and is the "better" upload by every other signal.
        var candidates = new List<MusicVideoDownloader.SearchCandidate>
        {
            new("ddg", "DDG - Trickin' [Official Video]", "DDG", 200),
            new("offset", "Offset - Trickin' (Visual)", "OffsetVEVO", 260),
        };

        Assert.Equal("offset",
            MusicVideoDownloader.PickBestCandidate(candidates, 200_000, Target("Offset", "Trickin'"))?.Id);
    }

    // The common case, which must keep its full score: the artist named in the title (however it is
    // punctuated or joined to the song title), in the channel, or as a featured guest.
    [Theory]
    [InlineData("Belly Gang Kushington", "No Jewelry",
        "Belly Gang Kushington - No Jewelry (Official Music Video)", "Belly Gang Kushington")]
    [InlineData("Pooh Shiesty", "Last Man Breathin",
        "Pooh Shiesty- Last Man Breathing(Official Video)#308xmc#poohshiesty", "308XMC")]
    [InlineData("Casanova", "Set Trippin",
        "Casanova \"Set Trippin\" (WSHH Exclusive - Official Music Video)", "WORLDSTARHIPHOP")]
    [InlineData("Offset, Don Toliver", "Every Night",
        "Offset ft. Don Toliver – Every Night (Official Music Video)", "Midnight Rap")]
    [InlineData("Gucci Mane", "All I Have", "BIG30 ft. Gucci Mane - All I Have [Official Video]", "BIG30")]
    [InlineData("Clipse, Pusha T & Malice", "The Birds Don't Sing",
        "Clipse, John Legend, Voices of Fire - The Birds Don't Sing (Official Video)", "Clipse")]
    [InlineData("Malice", "The Birds Don't Sing", "The Birds Don't Sing (Official Video)", "Clipse, Pusha T & Malice")]
    [InlineData("A$AP Rocky", "Praise The Lord (Da Shine)",
        "A$AP Rocky - Praise The Lord (Da Shine) (Official Video) ft. Skepta", "ASAPROCKYUPTOWN")]
    [InlineData("A$AP Rocky", "Praise The Lord (Da Shine)",
        "Praise The Lord (Da Shine) (Official Video)", "AsapRockyVEVO")]
    [InlineData("Yeat", "Gët Busy", "Yëat - Gët Busy (Official Video)", "Lyrical Lemonade")]
    [InlineData("Tay-K", "The Race", "Tay-K - The Race (Official Video)", "Tay-K")]
    [InlineData("NAV", "Myself", "Myself (Official Music Video)", "NAV - Official")]
    [InlineData("Juice WRLD", "Lucid Dreams", "Lucid Dreams (Official Music Video)", "JuiceWRLDVEVO")]
    public void ScoreCandidate_TheArtistsOwnVideo_KeepsItsFullScore(
        string artist, string title, string videoTitle, string channel)
    {
        var right = new MusicVideoDownloader.SearchCandidate("right", videoTitle, channel, 200);

        Assert.Equal(75, MusicVideoDownloader.ScoreCandidate(right, 200_000, Target(artist, title)));
    }

    [Theory]
    [InlineData("NAV", "Navigate The Night (Official Video)", "Navigation Records")] // a word, not a substring
    [InlineData("Future", "Song (Official Video)", "Futures Market")]
    [InlineData("Gucci Mane", "Natasha - Song (Official Video)", "Gucci")] // part of a name is not the name
    public void NamesAnArtist_MatchesWholeWordsOnly(string artist, string videoTitle, string channel)
    {
        var candidate = new MusicVideoDownloader.SearchCandidate("x", videoTitle, channel, 200);

        Assert.False(MusicVideoDownloader.NamesAnArtist(candidate, MusicVideoDownloader.ArtistKeys(artist)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("¥$")]
    public void NamesAnArtist_NoArtistToLookFor_IsNoEvidence(string artist)
    {
        var candidate = new MusicVideoDownloader.SearchCandidate("x", "Song (Official Video)", "Someone", 200);

        Assert.Null(MusicVideoDownloader.NamesAnArtist(candidate, MusicVideoDownloader.ArtistKeys(artist)));
        Assert.Equal(75, MusicVideoDownloader.ScoreCandidate(candidate, 200_000, Target(artist, "Song")));
    }

    [Fact]
    public void ArtistKeys_SplitsEveryJoinerOfACreditAndKeepsTheWholeName()
    {
        Assert.Equal(
            ["clipse", "clipse pusha t", "clipse pusha t malice", "malice", "pusha t"],
            MusicVideoDownloader.ArtistKeys("Clipse, Pusha T & Malice").Order().ToArray());
        Assert.Equal(
            ["drake", "drake feat future", "future"],
            MusicVideoDownloader.ArtistKeys("Drake feat. Future").Order().ToArray());
    }

    [Fact]
    public void TitleTokens_StripNoiseArtistAndGenericWords()
    {
        var tokens = Tokens(
            "BabyChiefDoit", "Lyrical Lemonade, BabyChiefDoit - Riley N Lamilton (Lunchbreak Freestyle) [Official Audio]");

        Assert.Equal(["freestyle", "lamilton", "lunchbreak", "n", "riley"], tokens.Order().ToArray());
        Assert.DoesNotContain("official", tokens);
        Assert.DoesNotContain("babychiefdoit", tokens);
    }

    [Theory]
    [InlineData("Gucci Mane", "She on GO", new[] { "go", "on", "she" })]
    [InlineData("Offset", "5 Star", new[] { "5", "star" })]
    [InlineData("Kendrick Lamar", "u", new[] { "u" })]
    [InlineData("Drake", "9", new[] { "9" })]
    [InlineData("21 Savage", "X", new[] { "x" })]
    [InlineData("Jay-Z", "4:44", new[] { "4", "44" })]
    [InlineData("J. Cole", "G.O.M.D.", new[] { "gomd" })]
    [InlineData("Clipse", "The Birds Don't Sing", new[] { "birds", "dont", "sing" })]
    [InlineData("Yeat", "Gët Busy", new[] { "busy", "get" })]
    [InlineData("Artist", "Song ft. Guest", new[] { "guest", "song" })]
    public void TitleTokens_KeepShortWordsAndDigitsAndFoldPunctuation(string artist, string title, string[] expected)
    {
        Assert.Equal(expected, Tokens(artist, title).Order().ToArray());
    }

    [Fact]
    public void ScoreCandidate_ShortTitleTokensDistinguishSameArtistSongs()
    {
        // Production, 2026-11-24: "She on GO" previously dropped "on" and "GO" (< 3 chars), so
        // "She Be Puttin' On" matched 1/1 on "she" and scored 75. Keeping short tokens drops it
        // below 75 while the real song still scores 75.
        var target = Target("Gucci Mane", "She on GO");
        var wrong = new MusicVideoDownloader.SearchCandidate(
            "wrong", "Gucci Mane & Waka Flocka Flame - She Be Puttin' On (Official Video)", "Gucci Mane", 200);
        var right = new MusicVideoDownloader.SearchCandidate(
            "right", "Gucci Mane - She on GO (Official Video)", "Gucci Mane", 200);

        Assert.True(MusicVideoDownloader.ScoreCandidate(wrong, 200_000, target) < 75);
        Assert.Equal(75, MusicVideoDownloader.ScoreCandidate(right, 200_000, target));
        Assert.Equal("right", MusicVideoDownloader.PickBestCandidate([wrong, right], 200_000, target)?.Id);
    }

    [Theory]
    [InlineData("Kendrick Lamar", "u", "Kendrick Lamar - HUMBLE. (Official Video)", "KendrickLamarVEVO")]
    [InlineData("Drake", "9", "Drake - 2019 Freestyle (Official Video)", "DrakeVEVO")]
    [InlineData("21 Savage", "X", "21 Savage - Bank Account Remix (Official Video)", "21 Savage")]
    [InlineData("Gucci Mane", "She on GO", "Gucci Mane - Gonna Be Good (Official Video)", "Gucci Mane")]
    [InlineData("Offset", "5 Star", "Offset - 50 Bars (Official Video)", "OffsetVEVO")]
    public void ScoreCandidate_ShortTitleTokensMatchWholeWordsOnly(
        string artist, string title, string wrongVideoTitle, string channel)
    {
        var wrong = new MusicVideoDownloader.SearchCandidate("wrong", wrongVideoTitle, channel, 200);

        Assert.True(MusicVideoDownloader.ScoreCandidate(wrong, 200_000, Target(artist, title)) < 0);
        Assert.Empty(MusicVideoDownloader.RankCandidates([wrong], 200_000, Target(artist, title)));
    }

    [Theory]
    [InlineData("Kendrick Lamar", "u", "Kendrick Lamar - u (Official Video)", "KendrickLamarVEVO")]
    [InlineData("Drake", "9", "Drake - 9 (Official Video)", "DrakeVEVO")]
    [InlineData("21 Savage", "X", "21 Savage - X ft. Future (Official Music Video)", "21 Savage")]
    [InlineData("Offset", "5 Star", "Offset - 5 Star (Official Music Video)", "OffsetVEVO")]
    [InlineData("J. Cole", "G.O.M.D.", "J. Cole - GOMD (Official Music Video)", "JColeVEVO")]
    [InlineData("Yeat", "Gët Busy", "Yeat - Get Busy (Official Video)", "Lyrical Lemonade")]
    [InlineData("Clipse", "The Birds Don't Sing", "Clipse - The Birds Dont Sing (Official Video)", "Clipse")]
    public void ScoreCandidate_ShortTitlesAndNormalizedVariantsKeepFullScore(
        string artist, string title, string videoTitle, string channel)
    {
        var right = new MusicVideoDownloader.SearchCandidate("right", videoTitle, channel, 200);

        Assert.Equal(75, MusicVideoDownloader.ScoreCandidate(right, 200_000, Target(artist, title)));
    }

    [Theory]
    [InlineData("Gbnga 2026", "https://www.youtube.com/results?search_query=Gbnga%202026")]
    [InlineData("Artist Song (Live at Wembley)", "https://www.youtube.com/results?search_query=Artist%20Song%20%28Live%20at%20Wembley%29")]
    [InlineData("A&B Song", "https://www.youtube.com/results?search_query=A%26B%20Song")]
    public void BuildSearchUrl_UsesTheUnfilteredResultsPage(string terms, string expected)
    {
        // Regression: yt-dlp's `ytsearch:` prefix sends YouTube's "Type → Video" filter, whose
        // ranking dropped GBNGA "2026" out of the top 20 while the results page had it at rank 1.
        var url = MusicVideoDownloader.BuildSearchUrl(terms);

        Assert.Equal(expected, url);
        Assert.DoesNotContain("ytsearch", url);
        Assert.DoesNotContain("official", url); // no "official video" suffix baked into the query
    }

    [Fact]
    public void ParseFlatSearch_SkipsChannelsMixesAndPlaylists()
    {
        // The unfiltered results page interleaves non-video rows between the videos.
        const string json = """
        {"entries":[
          {"_type":"url","ie_key":"YoutubeTab","id":"UCP5JdQMz8A2cebNmrnJeatA","title":"GBNGA"},
          {"_type":"url","ie_key":"Youtube","id":"HC03NpSvxQM","title":"2026","channel":"GBNGA","duration":122.0},
          {"_type":"url","ie_key":"YoutubeTab","id":"RDHC03NpSvxQM","title":"Mix - 2026"},
          {"_type":"playlist","id":"PLDLhza1SM6QRai","title":"GBNGA"}
        ]}
        """;

        var parsed = MusicVideoDownloader.ParseFlatSearch(json);

        Assert.Equal(["HC03NpSvxQM"], parsed.Select(c => c.Id).ToArray());
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

using MusicHoarder.Api.Options;
using MusicHoarder.Api.Soulseek;

namespace MusicHoarder.Api.Tests.Soulseek;

public class SlskdCandidateSelectorTests
{
    private static SlskdOptions Options(Action<SlskdOptions>? mutate = null)
    {
        var opts = new SlskdOptions
        {
            BaseUrl = "http://slskd:5030",
            ApiKey = "key",
            DownloadsDirectory = "/staging",
            MinBitrateLossy = 200,
            DurationToleranceSeconds = 10,
        };
        mutate?.Invoke(opts);
        return opts;
    }

    private static SlskdSearchResponse Response(string username, params SlskdFile[] files) =>
        new(username, HasFreeUploadSlot: true, QueueLength: 0, UploadSpeed: 1000, files);

    private static SlskdFile File(
        string filename, long size = 30_000_000, int? bitRate = null, int? length = 210, bool locked = false) =>
        new(filename, size, bitRate, length, Extension: null, IsLocked: locked);

    [Fact]
    public void Select_PrefersLosslessOverHighBitrateLossy()
    {
        var responses = new[]
        {
            Response("mp3guy", File(@"Music\Artist - Wanted Song.mp3", bitRate: 320)),
            Response("flacguy", File(@"Music\Artist - Wanted Song.flac", bitRate: 900)),
        };

        var result = SlskdCandidateSelector.Select(responses, "Wanted Song", 210_000, Options());

        Assert.Equal(2, result.Count);
        Assert.Equal("flacguy", result[0].Username);
    }

    [Fact]
    public void Select_FiltersDisallowedExtensionsLockedAndLowBitrate()
    {
        var responses = new[]
        {
            Response("peer",
                File(@"a\Wanted Song.wma", bitRate: 320),          // extension not in allowlist
                File(@"a\Wanted Song.mp3", bitRate: 128),           // below lossy floor
                File(@"a\Wanted Song.flac", locked: true),          // locked
                File(@"a\Wanted Song.mp3", bitRate: 320)),          // acceptable
        };

        var result = SlskdCandidateSelector.Select(responses, "Wanted Song", 210_000, Options());

        Assert.Single(result);
        Assert.Equal(320, result[0].File.BitRate);
        Assert.Equal(".mp3", result[0].File.NormalizedExtension);
    }

    [Fact]
    public void Select_LossyBitrateFloorDoesNotApplyToLossless()
    {
        var responses = new[] { Response("peer", File(@"a\Wanted Song.flac", bitRate: null)) };

        var result = SlskdCandidateSelector.Select(responses, "Wanted Song", 210_000, Options());

        Assert.Single(result);
    }

    [Fact]
    public void Select_EnforcesDurationOnlyWhenAdvertised()
    {
        var responses = new[]
        {
            Response("wrongdur", File(@"a\Wanted Song.flac", length: 500)),   // way off → dropped
            Response("nodur", File(@"b\Wanted Song.flac", length: null)),     // unknown → kept
            Response("okdur", File(@"c\Wanted Song.flac", length: 213)),      // within tolerance → kept
        };

        var result = SlskdCandidateSelector.Select(responses, "Wanted Song", 210_000, Options());

        Assert.Equal(2, result.Count);
        Assert.DoesNotContain(result, c => c.Username == "wrongdur");
    }

    [Fact]
    public void Select_RequiresAllTitleTokensInRemotePath()
    {
        var responses = new[]
        {
            Response("folderpeer", File(@"Artist\Great Album\03 - Wanted Song.flac")),
            Response("otherpeer", File(@"Artist\Great Album\04 - Different Track.flac")),
        };

        var result = SlskdCandidateSelector.Select(responses, "Wanted Song", 0, Options());

        Assert.Single(result);
        Assert.Equal("folderpeer", result[0].Username);
    }

    [Fact]
    public void Select_TitleTokenMatchIsNormalized()
    {
        // Diacritics + punctuation + case differences must not block a match.
        var responses = new[] { Response("peer", File(@"a\ARTIST - Wanted, Söng!.flac")) };

        var result = SlskdCandidateSelector.Select(responses, "wanted song", 0, Options());

        Assert.Single(result);
    }

    [Fact]
    public void Select_RanksFreeSlotShortQueueFastPeerWithinSameQuality()
    {
        var busy = new SlskdSearchResponse("busy", HasFreeUploadSlot: false, QueueLength: 12, UploadSpeed: 9999,
            [File(@"a\Wanted Song.flac", bitRate: 900)]);
        var free = new SlskdSearchResponse("free", HasFreeUploadSlot: true, QueueLength: 0, UploadSpeed: 100,
            [File(@"b\Wanted Song.flac", bitRate: 900)]);

        var result = SlskdCandidateSelector.Select([busy, free], "Wanted Song", 0, Options());

        Assert.Equal(2, result.Count);
        Assert.Equal("free", result[0].Username);
    }

    [Fact]
    public void Select_PreferLosslessOff_RanksByBitrateAlone()
    {
        var responses = new[]
        {
            Response("mp3guy", File(@"a\Wanted Song.mp3", bitRate: 320)),
            Response("flacguy", File(@"b\Wanted Song.flac", bitRate: 250)),
        };

        var result = SlskdCandidateSelector.Select(
            responses, "Wanted Song", 0, Options(o => o.PreferLossless = false));

        Assert.Equal("mp3guy", result[0].Username);
    }

    [Fact]
    public void Select_EmptyResponses_ReturnsEmpty()
    {
        Assert.Empty(SlskdCandidateSelector.Select([], "Wanted Song", 0, Options()));
        Assert.Empty(SlskdCandidateSelector.Select(
            [new SlskdSearchResponse("peer", true, 0, 0, Files: null)], "Wanted Song", 0, Options()));
    }
}

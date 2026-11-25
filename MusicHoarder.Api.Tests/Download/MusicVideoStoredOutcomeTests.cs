using MusicHoarder.Api.Download;
using MusicHoarder.Api.Options;

namespace MusicHoarder.Api.Tests.Download;

public class MusicVideoStoredOutcomeTests
{
    private static readonly MusicEnricherOptions Options = new() { SourceDirectory = "/src", DestinationDirectory = "/dst" };

    private static readonly CachedVideoCandidate Confident =
        new("vid-real", "Offset - Trickin' (Official Video)", "Offset", 215, 90, true, MusicVideoMotion.RealVideo, 60_000_000, false);

    private static MusicVideoSearchOutcome Of(bool hasError, int emptySearches, params CachedVideoCandidate[] list)
        => MusicVideoAutoPick.OutcomeOfStoredSet(hasError, emptySearches, list, "Offset", null, 200, Options);

    [Fact]
    public void A_list_with_a_pick_is_a_clip_and_one_without_is_no_clip()
    {
        Assert.Equal(MusicVideoSearchOutcome.Clip, Of(false, 0, Confident));
        Assert.Equal(MusicVideoSearchOutcome.NoClip, Of(false, 0, Confident with { Score = 40 }));
    }

    [Fact]
    public void A_set_the_schedule_last_saw_empty_is_empty_whatever_list_it_kept()
    {
        Assert.Equal(MusicVideoSearchOutcome.Empty, Of(true, 0));
        Assert.Equal(MusicVideoSearchOutcome.Empty, Of(false, 2, Confident));
        Assert.Equal(MusicVideoSearchOutcome.Empty, Of(false, 0));
    }
}

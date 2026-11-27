using MusicHoarder.Api.CleanEdits;

namespace MusicHoarder.Api.Tests.CleanEdits;

public class QuietFramesTests
{
    private static EditionComparison.QuietFrames QuietAt(params int[] frames)
    {
        var quiet = new bool[40];
        foreach (var f in frames)
            quiet[f] = true;
        return new EditionComparison.QuietFrames("other", 0, quiet);
    }

    [Fact]
    public void Quiet_frames_on_both_sides_of_a_run_do_not_add_up_to_agreement()
    {
        Assert.False(QuietAt(8, 9, 15, 16).Agrees(10, 15));
    }

    [Fact]
    public void The_same_run_heard_a_frame_or_two_off_agrees()
    {
        Assert.True(QuietAt(12, 13, 14, 15, 16).Agrees(10, 15));
        Assert.True(QuietAt(10, 11, 12, 13, 14).Agrees(10, 15));
    }

    [Fact]
    public void A_run_outside_this_preview_is_not_contradicted()
    {
        Assert.True(new EditionComparison.QuietFrames("other", 20, new bool[10]).Agrees(5, 10));
    }
}

using MusicHoarder.Api.Playback;

namespace MusicHoarder.Api.Tests.Playback;

/// <summary>
/// The pure rules of playback sync. The queue trim is the one the clients apply before sending,
/// so these cases are also what their ports must agree with.
/// </summary>
public class PlaybackRulesTests
{
    private static readonly DateTime T0 = new(2026, 9, 25, 10, 0, 0, DateTimeKind.Utc);

    // ── Queue cap ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_queue_under_the_cap_is_left_alone()
    {
        // Trimming it would move the queue under a client that had already trimmed, and a later
        // "queue: null" report's index would then point at the wrong song.
        int[] queue = [.. Enumerable.Range(1, 200)];

        var (trimmed, index) = PlaybackRules.TrimQueue(queue, 150);

        Assert.Same(queue, trimmed);
        Assert.Equal(150, index);
    }

    [Fact]
    public void A_queue_over_the_cap_keeps_fifty_played_ids_before_the_current_song()
    {
        int[] queue = [.. Enumerable.Range(1, 3000)];

        var (trimmed, index) = PlaybackRules.TrimQueue(queue, 700);

        Assert.Equal(PlaybackRules.QueueCap, trimmed.Length);
        Assert.Equal(651, trimmed[0]);
        Assert.Equal(50, index);
        Assert.Equal(queue[700], trimmed[index]);
    }

    [Fact]
    public void A_trim_near_the_start_begins_at_the_first_id()
    {
        int[] queue = [.. Enumerable.Range(1, 1500)];

        var (trimmed, index) = PlaybackRules.TrimQueue(queue, 20);

        Assert.Equal(1, trimmed[0]);
        Assert.Equal(20, index);
        Assert.Equal(PlaybackRules.QueueCap, trimmed.Length);
    }

    [Fact]
    public void A_trim_near_the_end_takes_what_is_left()
    {
        int[] queue = [.. Enumerable.Range(1, 1500)];

        var (trimmed, index) = PlaybackRules.TrimQueue(queue, 1400);

        Assert.Equal(1351, trimmed[0]);
        Assert.Equal(150, trimmed.Length);
        Assert.Equal(queue[1400], trimmed[index]);
    }

    // ── Projection ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_playing_session_advances_from_its_last_report()
    {
        var (playing, position) = PlaybackRules.Project(
            10_000, T0, 200_000, isPlaying: true, playbackRate: 1.0, T0.AddSeconds(5), unreachableSinceUtc: null);

        Assert.True(playing);
        Assert.Equal(15_000, position);
    }

    [Fact]
    public void A_paused_session_holds_its_position()
    {
        var (playing, position) = PlaybackRules.Project(
            10_000, T0, 200_000, isPlaying: false, playbackRate: 1.0, T0.AddMinutes(10), unreachableSinceUtc: null);

        Assert.False(playing);
        Assert.Equal(10_000, position);
    }

    [Fact]
    public void The_playback_rate_scales_the_advance()
    {
        var (_, position) = PlaybackRules.Project(
            10_000, T0, 200_000, isPlaying: true, playbackRate: 1.5, T0.AddSeconds(10), unreachableSinceUtc: null);

        Assert.Equal(25_000, position);
    }

    [Fact]
    public void The_position_never_passes_the_duration()
    {
        var (playing, position) = PlaybackRules.Project(
            55_000, T0, 60_000, isPlaying: true, playbackRate: 1.0, T0.AddSeconds(20), unreachableSinceUtc: null);

        // Still inside the 30 s grace: the next track may simply not have been reported yet.
        Assert.True(playing);
        Assert.Equal(60_000, position);
    }

    [Fact]
    public void A_session_that_ran_well_past_its_end_is_reported_as_stopped_at_the_end()
    {
        // 5 s left + 30 s grace = 35 s after the report.
        var justBefore = PlaybackRules.Project(
            55_000, T0, 60_000, isPlaying: true, playbackRate: 1.0, T0.AddSeconds(34.9), unreachableSinceUtc: null);
        var after = PlaybackRules.Project(
            55_000, T0, 60_000, isPlaying: true, playbackRate: 1.0, T0.AddSeconds(35), unreachableSinceUtc: null);

        Assert.True(justBefore.IsPlaying);
        Assert.False(after.IsPlaying);
        Assert.Equal(60_000, after.PositionMs);
    }

    [Fact]
    public void The_grace_past_the_end_is_measured_at_the_playback_rate()
    {
        // At 2x, 5 s of song + 30 s of grace pass in 17.5 s of wall time.
        var (playing, _) = PlaybackRules.Project(
            55_000, T0, 60_000, isPlaying: true, playbackRate: 2.0, T0.AddSeconds(17.5), unreachableSinceUtc: null);

        Assert.False(playing);
    }

    [Fact]
    public void A_session_without_a_duration_never_goes_stale_by_position()
    {
        var (playing, position) = PlaybackRules.Project(
            0, T0, durationMs: null, isPlaying: true, playbackRate: 1.0, T0.AddHours(3), unreachableSinceUtc: null);

        Assert.True(playing);
        Assert.Equal(3 * 3600 * 1000, position);
    }

    [Fact]
    public void An_unreachable_device_freezes_the_position_where_it_went_away()
    {
        var (playing, position) = PlaybackRules.Project(
            10_000, T0, 200_000, isPlaying: true, playbackRate: 1.0,
            now: T0.AddMinutes(5), unreachableSinceUtc: T0.AddSeconds(8));

        Assert.False(playing);
        Assert.Equal(18_000, position);
    }

    [Fact]
    public void A_device_that_was_unreachable_before_it_reported_does_not_move_backwards()
    {
        var (playing, position) = PlaybackRules.Project(
            10_000, T0, 200_000, isPlaying: true, playbackRate: 1.0,
            now: T0.AddSeconds(30), unreachableSinceUtc: T0.AddSeconds(-5));

        Assert.False(playing);
        Assert.Equal(10_000, position);
    }

    [Fact]
    public void A_frozen_projection_says_when_it_stopped_advancing()
    {
        var wentAway = PlaybackRules.Project(
            10_000, T0, 200_000, isPlaying: true, playbackRate: 1.0,
            now: T0.AddMinutes(5), unreachableSinceUtc: T0.AddSeconds(8));
        var ranOver = PlaybackRules.Project(
            55_000, T0, 60_000, isPlaying: true, playbackRate: 1.0, T0.AddMinutes(5), unreachableSinceUtc: null);
        var beforeTheReport = PlaybackRules.Project(
            10_000, T0, 200_000, isPlaying: true, playbackRate: 1.0,
            now: T0.AddSeconds(30), unreachableSinceUtc: T0.AddSeconds(-5));

        Assert.Equal(T0.AddSeconds(8), wentAway.FrozenAtUtc);
        Assert.Equal(T0.AddSeconds(35), ranOver.FrozenAtUtc); // 5 s of song + 30 s of grace
        Assert.Equal(T0, beforeTheReport.FrozenAtUtc);
    }

    [Fact]
    public void A_projection_that_plays_or_was_paused_anyway_never_froze()
    {
        var playing = PlaybackRules.Project(
            10_000, T0, 200_000, isPlaying: true, playbackRate: 1.0, T0.AddSeconds(5), unreachableSinceUtc: null);
        var paused = PlaybackRules.Project(
            10_000, T0, 200_000, isPlaying: false, playbackRate: 1.0, T0.AddSeconds(5), unreachableSinceUtc: T0);

        Assert.Null(playing.FrozenAtUtc);
        Assert.Null(paused.FrozenAtUtc);
    }

    // ── What a device may send ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("0f8fad5b-d9cb-469f-a165-70867728950e", true)]
    [InlineData("0f8fad5bd9cb469fa16570867728950e", true)]
    [InlineData("abc_DEF-12", true)]
    [InlineData("short", false)]
    [InlineData("has space in it", false)]
    [InlineData("semi;colon-id", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Device_ids_are_uuid_like(string? id, bool valid)
    {
        Assert.Equal(valid, PlaybackRules.IsValidDeviceId(id));
        Assert.False(PlaybackRules.IsValidDeviceId(new string('a', 65)));
    }

    [Fact]
    public void Display_hints_are_trimmed_and_capped()
    {
        Assert.Null(PlaybackRules.Cap("   "));
        Assert.Equal("Nightswim", PlaybackRules.Cap("  Nightswim  "));
        Assert.Equal(512, PlaybackRules.Cap(new string('x', 600))!.Length);
        Assert.Equal(64, PlaybackRules.DeviceName(new string('y', 100)).Length);
        Assert.Equal(PlaybackRules.UnnamedDevice, PlaybackRules.DeviceName(null));
    }

    [Theory]
    [InlineData("phone", "phone")]
    [InlineData("Tablet", "tablet")]
    [InlineData("toaster", "unknown")]
    [InlineData(null, "unknown")]
    public void An_unrecognised_device_kind_is_unknown(string? kind, string expected) =>
        Assert.Equal(expected, PlaybackRules.DeviceKind(kind));

    [Theory]
    [InlineData(83_000.4, 83_000)]
    [InlineData(83_000.6, 83_001)]
    [InlineData(-5.0, 0)]
    [InlineData(double.NaN, 0)]
    public void Positions_are_whole_milliseconds(double value, long expected) =>
        Assert.Equal(expected, PlaybackRules.Milliseconds(value));

    [Theory]
    [InlineData(215_000.2, 215_000L)]
    [InlineData(0.4, null)]
    [InlineData(0.0, null)]
    [InlineData(-1.0, null)]
    [InlineData(null, null)]
    public void A_duration_that_is_not_positive_is_unknown(double? value, long? expected) =>
        Assert.Equal(expected, PlaybackRules.NormalizeDuration(value));

    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(-1.0, 1.0)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(1.25, 1.25)]
    public void Only_a_positive_finite_rate_is_taken(double? rate, double expected) =>
        Assert.Equal(expected, PlaybackRules.NormalizeRate(rate));

    [Fact]
    public void Commands_round_trip_through_their_wire_names()
    {
        foreach (var kind in Enum.GetValues<PlaybackCommandKind>())
        {
            Assert.True(PlaybackRules.TryParseCommand(kind.WireName(), out var parsed));
            Assert.Equal(kind, parsed);
        }
        Assert.False(PlaybackRules.TryParseCommand("shuffle", out _));
        Assert.False(PlaybackRules.TryParseCommand(null, out _));
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// The transcription audio meter: the background sweeps stop short of the provider's quota so a manual
/// re-sync always has room, and that stays true across the restart every deploy causes.
/// </summary>
public class TranscriptionAudioBudgetTests
{
    private static readonly LyricsTimingOptions Ceiling = new() { AudioSecondsPerHour = 1000, AudioSecondsPerDay = 1000 };

    [Fact]
    public async Task background_spend_stops_at_the_ceiling_while_a_manual_run_still_goes_through()
    {
        await using var db = NewDb();
        var budget = TestBudget.Over(db, Ceiling);

        await Spend(budget, TranscriptionPurpose.AutoResync, 900);

        await Assert.ThrowsAsync<TranscriptionRateLimitedException>(
            () => Spend(budget, TranscriptionPurpose.TimingProbe, 200));
        Assert.Equal("ok", await Spend(budget, TranscriptionPurpose.ManualTranscribe, 300));
    }

    [Fact]
    public async Task manual_runs_count_against_the_background_ceiling()
    {
        await using var db = NewDb();
        var budget = TestBudget.Over(db, Ceiling);

        await Spend(budget, TranscriptionPurpose.ManualTranscribe, 900);

        await Assert.ThrowsAsync<TranscriptionRateLimitedException>(
            () => Spend(budget, TranscriptionPurpose.TimingProbe, 200));
    }

    [Fact]
    public async Task a_restart_does_not_hand_the_sweeps_a_fresh_allowance()
    {
        await using var db = NewDb();
        await Spend(TestBudget.Over(db, Ceiling), TranscriptionPurpose.AutoResync, 900);

        var afterRestart = TestBudget.Over(db, Ceiling);

        await Assert.ThrowsAsync<TranscriptionRateLimitedException>(
            () => Spend(afterRestart, TranscriptionPurpose.TimingProbe, 200));
        Assert.Equal(1, await afterRestart.CountLastDayAsync(TranscriptionPurpose.AutoResync, CancellationToken.None));
    }

    [Fact]
    public async Task a_refusal_says_when_the_oldest_spend_ages_out()
    {
        await using var db = NewDb();
        db.TranscriptionAudioSpends.Add(new TranscriptionAudioSpend
        {
            AtUtc = DateTime.UtcNow.AddMinutes(-50),
            AudioSeconds = 900,
            Purpose = TranscriptionPurpose.TimingProbe,
        });
        await db.SaveChangesAsync();
        var budget = TestBudget.Over(db, new LyricsTimingOptions { AudioSecondsPerHour = 1000, AudioSecondsPerDay = 5000 });

        var refused = await Assert.ThrowsAsync<TranscriptionRateLimitedException>(
            () => Spend(budget, TranscriptionPurpose.TimingProbe, 200));

        Assert.InRange(refused.RetryAfter!.Value.TotalMinutes, 9.9, 10.1);
    }

    [Fact]
    public async Task spend_older_than_a_day_no_longer_counts_and_is_cleared_away()
    {
        await using var db = NewDb();
        db.TranscriptionAudioSpends.Add(new TranscriptionAudioSpend
        {
            AtUtc = DateTime.UtcNow.AddDays(-1).AddMinutes(-1),
            AudioSeconds = 900,
            Purpose = TranscriptionPurpose.AutoResync,
        });
        await db.SaveChangesAsync();
        var budget = TestBudget.Over(db, Ceiling);

        Assert.Equal("ok", await Spend(budget, TranscriptionPurpose.TimingProbe, 900));
        Assert.Single(await db.TranscriptionAudioSpends.ToListAsync());
    }

    [Fact]
    public async Task a_request_the_provider_refused_gives_its_audio_back()
    {
        await using var db = NewDb();
        var budget = TestBudget.Over(db, Ceiling);

        await Assert.ThrowsAsync<TranscriptionRateLimitedException>(() => budget.SpendAsync<string>(
            TranscriptionPurpose.ManualTranscribe,
            900,
            _ => throw new TranscriptionRateLimitedException("429"),
            CancellationToken.None));

        Assert.Empty(await db.TranscriptionAudioSpends.ToListAsync());
        Assert.Equal("ok", await Spend(budget, TranscriptionPurpose.TimingProbe, 900));
    }

    [Fact]
    public async Task each_retry_uploads_the_audio_again_and_is_metered_again()
    {
        await using var db = NewDb();
        var budget = TestBudget.Over(db, Ceiling);

        await budget.SpendAsync(
            TranscriptionPurpose.AutoSync,
            200,
            async retry =>
            {
                await retry(CancellationToken.None);
                await retry(CancellationToken.None);
                return "ok";
            },
            CancellationToken.None);

        Assert.Equal(600, (await db.TranscriptionAudioSpends.ToListAsync()).Sum(e => e.AudioSeconds));
        await Assert.ThrowsAsync<TranscriptionRateLimitedException>(
            () => Spend(budget, TranscriptionPurpose.TimingProbe, 500));
    }

    [Fact]
    public async Task a_background_retry_with_no_room_left_fails_the_request_and_the_upload_already_sent_stays_counted()
    {
        await using var db = NewDb();
        var budget = TestBudget.Over(db, Ceiling);
        await Spend(budget, TranscriptionPurpose.AutoSync, 500);

        // A failure, not "not now": the song must not stay first in line for the next pass.
        await Assert.ThrowsAsync<InvalidOperationException>(() => budget.SpendAsync<string>(
            TranscriptionPurpose.AutoSync,
            400,
            async retry =>
            {
                await retry(CancellationToken.None);
                return "ok";
            },
            CancellationToken.None));

        Assert.Equal(900, (await db.TranscriptionAudioSpends.ToListAsync()).Sum(e => e.AudioSeconds));
    }

    [Fact]
    public void a_song_known_only_in_milliseconds_is_metered_at_its_length()
    {
        var song = new SongMetadata
        {
            SourcePath = "/x.mp3",
            FileName = "x.mp3",
            Extension = ".mp3",
            FileSizeBytes = 1,
            LastModifiedUtc = DateTime.UtcNow,
            IndexedAtUtc = DateTime.UtcNow,
            DurationMs = 2_400_000,
        };

        Assert.Equal(2400, TranscriptionAudioBudget.WholeSongSeconds(song));
    }

    private static Task<string> Spend(TranscriptionAudioBudget budget, TranscriptionPurpose purpose, double seconds)
        => budget.SpendAsync(purpose, seconds, _ => Task.FromResult("ok"), CancellationToken.None);

    private static MusicHoarderDbContext NewDb() =>
        new(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
}

internal static class TestBudget
{
    public static TranscriptionAudioBudget Over(MusicHoarderDbContext db, LyricsTimingOptions? options = null)
        => new(
            new FixedScopeFactory(db),
            new StaticOptionsMonitor<LyricsTimingOptions>(options ?? new LyricsTimingOptions()),
            NullLogger<TranscriptionAudioBudget>.Instance);
}

/// <summary>Hands every scope the same context, so a test can see what a scoped service wrote.</summary>
internal sealed class FixedScopeFactory(MusicHoarderDbContext db) : IServiceScopeFactory, IServiceScope, IServiceProvider
{
    public IServiceScope CreateScope() => this;
    public IServiceProvider ServiceProvider => this;
    public void Dispose() { }
    public object? GetService(Type serviceType) => serviceType == typeof(MusicHoarderDbContext) ? db : null;
}

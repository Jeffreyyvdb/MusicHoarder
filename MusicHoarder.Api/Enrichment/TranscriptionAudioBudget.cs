using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// The meter for transcription <b>audio-seconds</b>, the unit the provider's quota is counted in (Groq's free
/// whisper tier allows 7,200 per hour and 28,800 per day). Every request is recorded here, whoever made it,
/// and the background sweeps are held below <see cref="LyricsTimingOptions.AudioSecondsPerHour"/> and
/// <see cref="LyricsTimingOptions.AudioSecondsPerDay"/> of <i>total</i> spend. The gap between those ceilings
/// and the provider's own limits is therefore always left for the requests a person makes by hand, which are
/// never refused here: the provider is the authority on whether they fit.
///
/// The meter is persisted (<see cref="TranscriptionAudioSpend"/>) and loaded once per process. In memory alone
/// it forgot its spend on every deploy, so the sweeps could spend a full allowance per restart, and a day with
/// several deploys left nothing for the AI lyrics button.
/// </summary>
public sealed class TranscriptionAudioBudget(
    IServiceScopeFactory scopeFactory,
    IOptionsMonitor<LyricsTimingOptions> options,
    ILogger<TranscriptionAudioBudget> logger)
{
    /// <summary>A song with no known length is metered as this long, so it cannot slip past the meter.</summary>
    public const int UnknownDurationSeconds = 300;

    private static readonly TimeSpan Day = TimeSpan.FromDays(1);
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>The last day's spend, oldest first; null until the first call loads it.</summary>
    private List<TranscriptionAudioSpend>? _spend;

    /// <summary>The audio a whole-song transcription of <paramref name="song"/> sends.</summary>
    public static double WholeSongSeconds(SongMetadata song)
        => song.DurationSeconds is > 0 ? song.DurationSeconds.Value
            : song.DurationMs is > 0 ? song.DurationMs.Value / 1000.0
            : UnknownDurationSeconds;

    public static bool IsBackground(TranscriptionPurpose purpose)
        => purpose is TranscriptionPurpose.TimingProbe or TranscriptionPurpose.AutoResync or TranscriptionPurpose.AutoSync;

    /// <summary>
    /// Makes one transcription request on the meter. The audio is recorded before the request goes out, so
    /// concurrent callers see each other's spend, and handed back when the provider never answered (it refused
    /// us, or the caller gave up). A background request that would take total spend past the background
    /// ceiling is not made at all: it throws <see cref="TranscriptionRateLimitedException"/> saying when there
    /// will be room, the same signal a provider refusal gives, because to the caller both mean "not now".
    ///
    /// The request is handed a callback to run before each retry it makes: a retry uploads the audio again,
    /// and the provider counts it again, so it is metered like a request of its own and a background retry
    /// with no room left is refused. The audio already sent is then not given back.
    /// </summary>
    public async Task<T> SpendAsync<T>(
        TranscriptionPurpose purpose,
        double audioSeconds,
        Func<Func<CancellationToken, Task>, Task<T>> request,
        CancellationToken ct)
    {
        var entry = await ReserveAsync(purpose, audioSeconds, ct);
        var retried = false;
        try
        {
            return await request(retryCt =>
            {
                retried = true;
                return ReserveAsync(purpose, audioSeconds, retryCt);
            });
        }
        catch (Exception ex) when (ex is TranscriptionRateLimitedException or OperationCanceledException)
        {
            // Only a request that never reached the provider costs nothing; a retry means it did.
            if (!retried)
                await RefundAsync(entry);
            throw;
        }
    }

    /// <summary>How many requests for <paramref name="purpose"/> were made in the last day.</summary>
    public async Task<int> CountLastDayAsync(TranscriptionPurpose purpose, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var spend = await LoadAsync(ct);
            Prune(spend, DateTime.UtcNow);
            return spend.Count(e => e.Purpose == purpose);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>How many audio-seconds requests for <paramref name="purpose"/> sent in the last day.</summary>
    public async Task<double> SecondsLastDayAsync(TranscriptionPurpose purpose, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var spend = await LoadAsync(ct);
            Prune(spend, DateTime.UtcNow);
            return spend.Where(e => e.Purpose == purpose).Sum(e => e.AudioSeconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TranscriptionAudioSpend> ReserveAsync(
        TranscriptionPurpose purpose, double audioSeconds, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var spend = await LoadAsync(ct);
            var now = DateTime.UtcNow;
            Prune(spend, now);

            if (IsBackground(purpose))
            {
                var opts = options.CurrentValue;
                var wait = Max(
                    WaitForRoom(spend, now, Hour, opts.AudioSecondsPerHour, audioSeconds),
                    WaitForRoom(spend, now, Day, opts.AudioSecondsPerDay, audioSeconds));
                if (wait > TimeSpan.Zero)
                {
                    logger.LogDebug(
                        "Transcription budget: no room for {Seconds:F0}s of background audio for another {Wait}.",
                        audioSeconds, wait);
                    throw new TranscriptionRateLimitedException(
                        "The background share of the transcription quota is spent for now.", wait);
                }
            }

            return await AddAsync(spend, purpose, audioSeconds, now, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<TranscriptionAudioSpend> AddAsync(
        List<TranscriptionAudioSpend> spend,
        TranscriptionPurpose purpose,
        double audioSeconds,
        DateTime now,
        CancellationToken ct)
    {
        var entry = new TranscriptionAudioSpend { AtUtc = now, AudioSeconds = audioSeconds, Purpose = purpose };
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            db.TranscriptionAudioSpends.Add(entry);
            await db.SaveChangesAsync(ct);
        }
        spend.Add(entry);
        return entry;
    }

    /// <summary>
    /// Takes a request off the meter. Never throws: it runs while a refusal is on its way to the caller, and a
    /// database error here must not replace that refusal, which callers rely on to record nothing. A row that
    /// could not be deleted only over-counts until it ages out, and only after a restart.
    /// </summary>
    private async Task RefundAsync(TranscriptionAudioSpend entry)
    {
        await _gate.WaitAsync(CancellationToken.None);
        try
        {
            _spend?.Remove(entry);
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            db.TranscriptionAudioSpends.Remove(entry);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not remove a refunded transcription spend row (Id={Id}).", entry.Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reads the last day's spend on first use, deleting rows too old to count again.</summary>
    private async Task<List<TranscriptionAudioSpend>> LoadAsync(CancellationToken ct)
    {
        if (_spend is not null)
            return _spend;

        var cutoff = DateTime.UtcNow - Day;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var expired = await db.TranscriptionAudioSpends.Where(e => e.AtUtc <= cutoff).ToListAsync(ct);
        if (expired.Count > 0)
        {
            db.TranscriptionAudioSpends.RemoveRange(expired);
            await db.SaveChangesAsync(ct);
        }

        _spend = await db.TranscriptionAudioSpends
            .AsNoTracking()
            .Where(e => e.AtUtc > cutoff)
            .OrderBy(e => e.AtUtc)
            .ToListAsync(ct);
        return _spend;
    }

    private static void Prune(List<TranscriptionAudioSpend> spend, DateTime now)
        => spend.RemoveAll(e => e.AtUtc <= now - Day);

    /// <summary>
    /// How long until <paramref name="audioSeconds"/> more fits under <paramref name="limit"/> in a rolling
    /// <paramref name="window"/>: zero when it fits now, else the moment enough of the oldest spend ages out.
    /// </summary>
    private static TimeSpan WaitForRoom(
        List<TranscriptionAudioSpend> spend, DateTime now, TimeSpan window, double limit, double audioSeconds)
    {
        var inWindow = spend.Where(e => e.AtUtc > now - window).ToList();
        var excess = inWindow.Sum(e => e.AudioSeconds) + audioSeconds - limit;
        if (excess <= 0)
            return TimeSpan.Zero;

        foreach (var e in inWindow)
        {
            excess -= e.AudioSeconds;
            if (excess <= 0)
                return e.AtUtc + window - now;
        }

        // More than the whole window allows on its own: it can never fit, so try again after a full window.
        return window;
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}

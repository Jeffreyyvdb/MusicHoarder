using MusicHoarder.Api.BackgroundWork;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

/// <summary>
/// The options <see cref="MusicVideoAutoPick.Choose(IReadOnlyList{CachedVideoCandidate}, string?, int?, int?, MusicVideoPickGates)"/>
/// reads, taken from <see cref="MusicEnricherOptions"/> in one place. A verdict remembered under these stays
/// valid exactly while they are equal, so a gate added here is in the remembered key without anyone listing it.
/// </summary>
public sealed record MusicVideoPickGates(int MinScore, int MaxClipMb)
{
    public static MusicVideoPickGates From(MusicEnricherOptions options)
        => new(options.MusicVideoAutoDownloadMinScore, options.MusicVideoAutoDownloadMaxClipMb);
}

/// <summary>
/// The one test for "we are confident this is the song's real video", used both to download a clip
/// without anyone choosing it and to say whether a song's list holds one (which sets when the song
/// is searched again), so the two can never disagree.
/// </summary>
public static class MusicVideoAutoPick
{
    /// <summary>
    /// The first candidate, in rank order, that is a measured moving clip, from a source that is not
    /// square, scored at least <c>MusicVideoAutoDownloadMinScore</c>, named for one of the song's
    /// artists, no more than 15 s shorter or 90 s longer than the song, and no bigger than
    /// <c>MusicVideoAutoDownloadMaxClipMb</c>. A missing fact (length, size) is a failed gate. Only
    /// candidates a probe has measured can pass, so in practice the pick is among the probed few.
    /// </summary>
    public static CachedVideoCandidate? Choose(
        IReadOnlyList<CachedVideoCandidate> ranked, SongMetadata song, MusicEnricherOptions options)
        => Choose(ranked, song.Artist, song.DurationMs, song.DurationSeconds, MusicVideoPickGates.From(options));

    /// <summary>The same test from just the song's fields it reads, for a page that loads no more.</summary>
    public static CachedVideoCandidate? Choose(
        IReadOnlyList<CachedVideoCandidate> ranked, string? artist, int? durationMs, int? durationSeconds,
        MusicVideoPickGates gates)
    {
        var songMs = durationMs ?? durationSeconds * 1000;
        if (songMs is not > 0)
            return null;

        var artistKeys = MusicVideoDownloader.ArtistKeys(artist ?? string.Empty);
        var songSeconds = songMs.Value / 1000.0;
        var maxBytes = gates.MaxClipMb * 1024L * 1024L;

        return ranked.FirstOrDefault(c =>
            c.Motion == MusicVideoMotion.RealVideo
            && !c.SquareSource
            && c.Score >= gates.MinScore
            // Explicitly `is true`: a blank artist reads null here and can still score full marks.
            && MusicVideoDownloader.NamesAnArtist(
                new MusicVideoDownloader.SearchCandidate(c.VideoId, c.Title, c.Channel, c.DurationSeconds),
                artistKeys) is true
            && c.DurationSeconds is { } seconds
            && seconds >= songSeconds - 15 && seconds <= songSeconds + 90
            && c.EstimatedBytes is > 0 and var bytes && bytes <= maxBytes);
    }

    /// <summary>What a search that returned candidates told us: a clip could be downloaded as it stands, or not.</summary>
    public static MusicVideoSearchOutcome OutcomeOf(
        IReadOnlyList<CachedVideoCandidate> candidates, string? artist, int? durationMs, int? durationSeconds,
        MusicVideoPickGates gates)
        => Choose(candidates, artist, durationMs, durationSeconds, gates) is null
            ? MusicVideoSearchOutcome.NoClip
            : MusicVideoSearchOutcome.Clip;

    /// <summary>
    /// The outcome the schedule recorded for a stored set. A set that carries an error or a run of empty
    /// searches was last searched with nothing usable, whatever list it kept from before; one with no
    /// candidates at all is the same.
    /// </summary>
    public static MusicVideoSearchOutcome OutcomeOfStoredSet(
        bool hasError, int emptySearches, IReadOnlyList<CachedVideoCandidate> candidates,
        string? artist, int? durationMs, int? durationSeconds, MusicVideoPickGates gates)
        => hasError || emptySearches > 0 || candidates.Count == 0
            ? MusicVideoSearchOutcome.Empty
            : OutcomeOf(candidates, artist, durationMs, durationSeconds, gates);

    /// <summary>
    /// Evaluates a stored candidate list once, returning both the chosen clip (if any) and the
    /// <see cref="MusicVideoVerdict"/> remembered in <see cref="MusicVideoOutcomeMemo"/>. Shared by
    /// the prefetch drain and the Background work sheet so their cached verdicts can never drift.
    /// </summary>
    public static (CachedVideoCandidate? Pick, MusicVideoVerdict Verdict) Evaluate(
        IReadOnlyList<CachedVideoCandidate> candidates,
        string? artist,
        int? durationMs,
        int? durationSeconds,
        MusicVideoPickGates gates)
    {
        if (candidates.Count == 0)
            return (null, new MusicVideoVerdict(MusicVideoSearchOutcome.Empty, null, null));

        var pick = Choose(candidates, artist, durationMs, durationSeconds, gates);
        var verdict = new MusicVideoVerdict(
            pick is null ? MusicVideoSearchOutcome.NoClip : MusicVideoSearchOutcome.Clip,
            candidates.Max(c => c.Score),
            pick?.EstimatedBytes);
        return (pick, verdict);
    }
}

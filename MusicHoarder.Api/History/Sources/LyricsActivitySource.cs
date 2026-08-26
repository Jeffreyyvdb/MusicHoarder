using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Endpoints;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.History.Sources;

/// <summary>
/// Everything that happens to a track's words: LRCLIB lookups, the timing check that catches an LRC
/// written for a different recording, the AI transcription that fills the gap LRCLIB cannot, and
/// translations.
/// </summary>
public sealed class LyricsActivitySource(MusicHoarderDbContext db) : IActivitySource
{
    private const string SyncedDetail = "Line-by-line synced lyrics";


    public async Task<IReadOnlyList<HistorySummary>> CollectAsync(ActivityWindow window, CancellationToken ct)
    {
        var summaries = new List<HistorySummary>();

        var fetched = await db.Songs
            .AsNoTracking()
            .Where(s => s.LyricsLastAttemptedAtUtc != null
                && s.LyricsLastAttemptedAtUtc >= window.FromUtc && s.LyricsLastAttemptedAtUtc <= window.ToUtc)
            .OrderByDescending(s => s.LyricsLastAttemptedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist, s.LyricsStatus,
                HasSynced = s.SyncedLyrics != null,
                At = s.LyricsLastAttemptedAtUtc!.Value,
            })
            .ToListAsync(ct);

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Lyrics, "lyrics-added", ActivityTint.Ok,
            fetched
                .Where(s => s.LyricsStatus == LyricsStatus.Fetched)
                .Select(s => new ActivityItem(
                    s.Id, s.Title, s.Album, s.AlbumArtist ?? s.Artist, s.At,
                    s.HasSynced ? SyncedDetail : "Plain lyrics only")),
            rows => rows.All(r => r.Detail == SyncedDetail)
                ? $"Added synced lyrics to {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}"
                : $"Added lyrics to {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}",
            rows => rows.All(r => r.Detail == SyncedDetail)
                ? "From LRCLIB, timed line by line"
                : "From LRCLIB"));

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Lyrics, "lyrics-missing", ActivityTint.Info,
            fetched
                .Where(s => s.LyricsStatus == LyricsStatus.NotFound)
                .Select(s => new ActivityItem(s.Id, s.Title, s.Album, s.AlbumArtist ?? s.Artist, s.At)),
            rows => $"LRCLIB has no lyrics for {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}",
            _ => "MusicHoarder will ask again later"));

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Lyrics, "lyrics-instrumental", ActivityTint.Info,
            fetched
                .Where(s => s.LyricsStatus == LyricsStatus.Instrumental)
                .Select(s => new ActivityItem(s.Id, s.Title, s.Album, s.AlbumArtist ?? s.Artist, s.At)),
            rows => $"{ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)} confirmed instrumental"));

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Lyrics, "lyrics-failed", ActivityTint.Warn,
            fetched
                .Where(s => s.LyricsStatus == LyricsStatus.Failed)
                .Select(s => new ActivityItem(s.Id, s.Title, s.Album, s.AlbumArtist ?? s.Artist, s.At)),
            rows => $"Lyrics lookup failed for {ActivityText.Plural(rows.Count, "track")} of {ActivityText.Quote(rows[0].Album)}"));

        summaries.AddRange(await CollectTimingAsync(window, ct));
        summaries.AddRange(await CollectTranscriptionAsync(window, ct));
        summaries.AddRange(await CollectTranslationAsync(window, ct));
        return summaries;
    }

    /// <summary>
    /// The LRC timing check. Only the two verdicts a human can act on get an entry — a shifted LRC that
    /// was repaired, and one that still looks wrong. "Checked and fine" runs over the whole library and
    /// says nothing worth reading.
    /// </summary>
    private async Task<List<HistorySummary>> CollectTimingAsync(ActivityWindow window, CancellationToken ct)
    {
        var rows = await db.Songs
            .AsNoTracking()
            .Where(s => s.LyricsSyncCheckedAtUtc != null
                && s.LyricsSyncCheckedAtUtc >= window.FromUtc && s.LyricsSyncCheckedAtUtc <= window.ToUtc
                && (s.LyricsSyncStatus == LyricsSyncStatus.Corrected || s.LyricsSyncStatus == LyricsSyncStatus.Suspect))
            .OrderByDescending(s => s.LyricsSyncCheckedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist, s.LyricsSyncStatus, s.LyricsSyncIssue,
                s.LyricsSyncOffsetMs,
                At = s.LyricsSyncCheckedAtUtc!.Value,
            })
            .ToListAsync(ct);

        var summaries = new List<HistorySummary>();

        foreach (var s in rows.Where(r => r.LyricsSyncStatus == LyricsSyncStatus.Corrected))
        {
            var shift = s.LyricsSyncOffsetMs is { } ms ? $"Shifted every line by {ActivityText.Seconds(ms)}" : "Timing realigned";
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Lyrics, "lyrics-timing-fixed", ActivityTint.Ok,
                idKey: s.Id.ToString(),
                headline: $"Fixed the lyric timing on {ActivityText.Quote(s.Title)}",
                detail: $"{shift} — the words are now labelled AI Enhanced",
                items: [new ActivityItem(s.Id, s.Title, s.Album, s.AlbumArtist ?? s.Artist, s.At)]));
        }

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Lyrics, "lyrics-timing-suspect", ActivityTint.Warn,
            rows
                .Where(r => r.LyricsSyncStatus == LyricsSyncStatus.Suspect)
                .Select(r => new ActivityItem(
                    r.Id, r.Title, r.Album, r.AlbumArtist ?? r.Artist, r.At, ActivityText.Clip(r.LyricsSyncIssue))),
            g => $"Lyric timing looks wrong on {ActivityText.Plural(g.Count, "track")} of {ActivityText.Quote(g[0].Album)}",
            g => ActivityText.Clip(g[0].Detail) ?? "Probably an LRC written for a different recording"));

        return summaries;
    }

    private async Task<List<HistorySummary>> CollectTranscriptionAsync(ActivityWindow window, CancellationToken ct)
    {
        var rows = await db.Songs
            .AsNoTracking()
            .Where(s => s.TranscribedAtUtc != null
                && s.TranscribedAtUtc >= window.FromUtc && s.TranscribedAtUtc <= window.ToUtc)
            .OrderByDescending(s => s.TranscribedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist, s.TranscriptionStatus, s.TranscriptionModel,
                s.TranscriptionError, s.TranscriptionAlignedToReference,
                At = s.TranscribedAtUtc!.Value,
            })
            .ToListAsync(ct);

        var summaries = new List<HistorySummary>();

        // Aligned to a reference = the song's real words, re-timed (AI Enhanced). Otherwise the AI wrote
        // the words too (AI Generated) — a meaningful difference to anyone reading along.
        foreach (var s in rows.Where(r => r.TranscriptionStatus == TranscriptionStatus.Completed))
        {
            summaries.Add(ActivityText.Summary(
                ActivityCategory.Lyrics,
                s.TranscriptionAlignedToReference ? "lyrics-realigned" : "lyrics-transcribed",
                ActivityTint.Info,
                idKey: s.Id.ToString(),
                headline: s.TranscriptionAlignedToReference
                    ? $"Re-timed the official words of {ActivityText.Quote(s.Title)} against the audio"
                    : $"Transcribed {ActivityText.Quote(s.Title)} with AI",
                detail: s.TranscriptionAlignedToReference
                    ? $"AI Enhanced{ModelSuffix(s.TranscriptionModel)}"
                    : $"AI Generated — nobody had written these down{ModelSuffix(s.TranscriptionModel)}",
                items: [new ActivityItem(s.Id, s.Title, s.Album, s.AlbumArtist ?? s.Artist, s.At)]));
        }

        summaries.AddRange(ActivityText.GroupByAlbumDay(
            ActivityCategory.Lyrics, "lyrics-transcription-failed", ActivityTint.Warn,
            rows
                .Where(r => r.TranscriptionStatus == TranscriptionStatus.Failed)
                .Select(r => new ActivityItem(
                    r.Id, r.Title, r.Album, r.AlbumArtist ?? r.Artist, r.At, ActivityText.Clip(r.TranscriptionError))),
            g => $"AI transcription failed for {ActivityText.Plural(g.Count, "track")} of {ActivityText.Quote(g[0].Album)}",
            g => ActivityText.Clip(g[0].Detail)));

        return summaries;
    }

    private async Task<List<HistorySummary>> CollectTranslationAsync(ActivityWindow window, CancellationToken ct)
    {
        var rows = await db.Songs
            .AsNoTracking()
            .Where(s => s.LyricsTranslatedAtUtc != null
                && s.LyricsTranslatedAtUtc >= window.FromUtc && s.LyricsTranslatedAtUtc <= window.ToUtc
                && s.LyricsTranslationStatus == LyricsTranslationStatus.Completed)
            .OrderByDescending(s => s.LyricsTranslatedAtUtc)
            .Take(window.MaxRowsPerSource)
            .Select(s => new
            {
                s.Id, s.Title, s.Album, s.AlbumArtist, s.Artist, s.DetectedLyricsLanguage,
                At = s.LyricsTranslatedAtUtc!.Value,
            })
            .ToListAsync(ct);

        return ActivityText.GroupByAlbumDay(
            ActivityCategory.Lyrics, "lyrics-translated", ActivityTint.Info,
            rows.Select(r => new ActivityItem(
                r.Id, r.Title, r.Album, r.AlbumArtist ?? r.Artist, r.At, r.DetectedLyricsLanguage)),
            g => $"Translated the lyrics of {ActivityText.Plural(g.Count, "track")} of {ActivityText.Quote(g[0].Album)}",
            g => g[0].Detail is { } lang ? $"Translated from {lang}" : null).ToList();
    }

    private static string ModelSuffix(string? model) =>
        string.IsNullOrWhiteSpace(model) ? "" : $" — {model}";
}

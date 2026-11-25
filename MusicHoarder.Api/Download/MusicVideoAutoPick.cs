using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Download;

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
    {
        var songMs = song.DurationMs ?? song.DurationSeconds * 1000;
        if (songMs is not > 0)
            return null;

        var artistKeys = MusicVideoDownloader.ArtistKeys(song.Artist ?? string.Empty);
        var songSeconds = songMs.Value / 1000.0;
        var maxBytes = options.MusicVideoAutoDownloadMaxClipMb * 1024L * 1024L;

        return ranked.FirstOrDefault(c =>
            c.Motion == MusicVideoMotion.RealVideo
            && !c.SquareSource
            && c.Score >= options.MusicVideoAutoDownloadMinScore
            // Explicitly `is true`: a blank artist reads null here and can still score full marks.
            && MusicVideoDownloader.NamesAnArtist(
                new MusicVideoDownloader.SearchCandidate(c.VideoId, c.Title, c.Channel, c.DurationSeconds),
                artistKeys) is true
            && c.DurationSeconds is { } seconds
            && seconds >= songSeconds - 15 && seconds <= songSeconds + 90
            && c.EstimatedBytes is > 0 and var bytes && bytes <= maxBytes);
    }
}

using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Applies a consensus-matched identity to a song's row <b>without degrading curated metadata</b>.
/// Per display field:
/// <list type="bullet">
/// <item>empty or low-quality existing value → filled/replaced;</item>
/// <item>existing value equals the incoming one (modulo normalization) → no-op;</item>
/// <item>a <i>good</i> existing value that differs → replaced only when the match is a strong
/// multi-provider consensus at high confidence; otherwise the change is recorded as a
/// <b>proposed</b> (not-applied) change for review.</item>
/// </list>
/// Identifiers (ISRC/MBID/Spotify) are additive and always attached. Every applied or proposed
/// change is returned so the caller can persist it to the undo/review history.
/// </summary>
public static class MetadataMerger
{
    public sealed record FieldChange(string Field, string? OldValue, string? NewValue, bool Applied);

    public static IReadOnlyList<FieldChange> ApplyMatch(
        SongMetadata song,
        EnrichmentProviderResult winner,
        double confidence,
        int agreeingProviderCount,
        double autoUpgradeConfidence,
        string? warningsJson)
    {
        song.CaptureOriginalMetadata();

        var highConsensus = agreeingProviderCount >= 2 && confidence >= autoUpgradeConfidence;
        var changes = new List<FieldChange>();

        MergeText(song, "Artist", song.Artist, winner.Artist, v => song.Artist = v, highConsensus, changes);
        MergeText(song, "AlbumArtist", song.AlbumArtist, winner.AlbumArtist, v => song.AlbumArtist = v, highConsensus, changes);
        MergeText(song, "Title", song.Title, winner.Title, v => song.Title = v, highConsensus, changes);
        MergeText(song, "Album", song.Album, winner.Album, v => song.Album = v, highConsensus, changes);
        MergeNumber(song, "Year", song.Year, winner.Year, v => song.Year = v, highConsensus, changes);
        MergeNumber(song, "TrackNumber", song.TrackNumber, winner.TrackNumber, v => song.TrackNumber = v, highConsensus, changes);

        // Identifiers are additive — attach the matched identity's IDs.
        if (!string.IsNullOrWhiteSpace(winner.MusicBrainzId)) song.MusicBrainzId = winner.MusicBrainzId;
        if (!string.IsNullOrWhiteSpace(winner.MusicBrainzReleaseId)) song.MusicBrainzReleaseId = winner.MusicBrainzReleaseId;
        if (!string.IsNullOrWhiteSpace(winner.SpotifyId)) song.SpotifyId = winner.SpotifyId;
        if (!string.IsNullOrWhiteSpace(winner.AcoustIdTrackId)) song.AcoustIdTrackId = winner.AcoustIdTrackId;
        if (!string.IsNullOrWhiteSpace(winner.Isrc)) song.Isrc = winner.Isrc;

        song.MatchedBy = winner.MatchedBy;
        song.MatchConfidence = confidence;
        song.MatchWarnings = warningsJson;
        song.EnrichmentStatus = EnrichmentStatus.Matched;
        song.EnrichedAtUtc = DateTime.UtcNow;
        song.EnrichmentError = null;

        return changes;
    }

    private static void MergeText(
        SongMetadata song, string field, string? existing, string? incoming,
        Action<string?> set, bool highConsensus, List<FieldChange> changes)
    {
        if (string.IsNullOrWhiteSpace(incoming))
            return;

        if (string.IsNullOrWhiteSpace(existing) || MetadataQualityHeuristics.IsLowQuality(existing, song.FileName))
        {
            set(incoming);
            changes.Add(new FieldChange(field, existing, incoming, Applied: true));
            return;
        }

        if (string.Equals(
                TitleNormalizer.NormalizeForSearch(existing),
                TitleNormalizer.NormalizeForSearch(incoming),
                StringComparison.Ordinal))
            return; // same value, just spelled differently — keep the curated form

        if (highConsensus)
        {
            set(incoming);
            changes.Add(new FieldChange(field, existing, incoming, Applied: true));
        }
        else
        {
            changes.Add(new FieldChange(field, existing, incoming, Applied: false)); // proposed
        }
    }

    private static void MergeNumber(
        SongMetadata song, string field, int? existing, int? incoming,
        Action<int?> set, bool highConsensus, List<FieldChange> changes)
    {
        if (incoming is null)
            return;

        if (existing is null)
        {
            set(incoming);
            changes.Add(new FieldChange(field, null, incoming.ToString(), Applied: true));
            return;
        }

        if (existing == incoming)
            return;

        if (highConsensus)
        {
            set(incoming);
            changes.Add(new FieldChange(field, existing.ToString(), incoming.ToString(), Applied: true));
        }
        else
        {
            changes.Add(new FieldChange(field, existing.ToString(), incoming.ToString(), Applied: false));
        }
    }
}

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

        // Force a destructive overwrite of curated values when EITHER a strong multi-provider
        // consensus justifies it OR the winner is authoritative for the fields it set (a user
        // match-rule rewriting its own captured tags). Originals are snapshotted above, so an
        // authoritative overwrite stays reversible via ResetEnrichment(restoreOriginal: true).
        var forceApply = (agreeingProviderCount >= 2 && confidence >= autoUpgradeConfidence)
            || winner.Authoritative;
        var changes = new List<FieldChange>();

        MergeText(song, "Artist", song.Artist, winner.Artist, v => song.Artist = v, forceApply, changes);
        MergeText(song, "AlbumArtist", song.AlbumArtist, winner.AlbumArtist, v => song.AlbumArtist = v, forceApply, changes);
        MergeText(song, "Title", song.Title, winner.Title, v => song.Title = v, forceApply, changes);
        MergeText(song, "Album", song.Album, winner.Album, v => song.Album = v, forceApply, changes);
        MergeText(song, "Artists", song.Artists, winner.Artists, v => song.Artists = v, forceApply, changes);
        MergeText(song, "ReleaseTypePrimary", song.ReleaseTypePrimary, winner.ReleaseTypePrimary, v => song.ReleaseTypePrimary = v, forceApply, changes);
        MergeText(song, "ReleaseTypes", song.ReleaseTypes, winner.ReleaseTypes, v => song.ReleaseTypes = v, forceApply, changes);
        MergeNumber(song, "Year", song.Year, winner.Year, v => song.Year = v, forceApply, changes);
        MergeNumber(song, "TrackNumber", song.TrackNumber, winner.TrackNumber, v => song.TrackNumber = v, forceApply, changes);
        MergeNumber(song, "DiscNumber", song.DiscNumber, winner.DiscNumber, v => song.DiscNumber = v, forceApply, changes);
        MergeNumber(song, "TotalDiscs", song.TotalDiscs, winner.TotalDiscs, v => song.TotalDiscs = v, forceApply, changes);
        MergeNumber(song, "TotalTracks", song.TotalTracks, winner.TotalTracks, v => song.TotalTracks = v, forceApply, changes);

        // Identifiers and the compilation flag are additive facts — attach the matched identity's data.
        if (!string.IsNullOrWhiteSpace(winner.MusicBrainzId)) song.MusicBrainzId = winner.MusicBrainzId;
        if (!string.IsNullOrWhiteSpace(winner.MusicBrainzReleaseId)) song.MusicBrainzReleaseId = winner.MusicBrainzReleaseId;
        if (!string.IsNullOrWhiteSpace(winner.MusicBrainzReleaseGroupId)) song.MusicBrainzReleaseGroupId = winner.MusicBrainzReleaseGroupId;
        if (!string.IsNullOrWhiteSpace(winner.AlbumArtistMusicBrainzId)) song.AlbumArtistMusicBrainzId = winner.AlbumArtistMusicBrainzId;
        if (!string.IsNullOrWhiteSpace(winner.ArtistMusicBrainzIds)) song.ArtistMusicBrainzIds = winner.ArtistMusicBrainzIds;
        if (winner.IsCompilation is true) song.IsCompilation = true;
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
        Action<string?> set, bool forceApply, List<FieldChange> changes)
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

        if (forceApply)
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
        Action<int?> set, bool forceApply, List<FieldChange> changes)
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

        if (forceApply)
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

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

    /// <summary>
    /// Album-level fields whose overwrite of a good embedded value is gated on <i>per-field</i>
    /// cluster corroboration (see <paramref name="corroboratedFields"/>) rather than the blanket
    /// recording-consensus flag. A recording legitimately appears on several releases, so agreeing on
    /// the recording must not by itself license replacing a curated album/year/track.
    /// </summary>
    private static readonly HashSet<string> AlbumLevelFields = new(StringComparer.Ordinal)
    {
        "Album", "Year", "TrackNumber", "DiscNumber", "TotalDiscs", "TotalTracks",
        "ReleaseTypePrimary", "ReleaseTypes",
    };

    public static IReadOnlyList<FieldChange> ApplyMatch(
        SongMetadata song,
        EnrichmentProviderResult winner,
        double confidence,
        int agreeingProviderCount,
        double autoUpgradeConfidence,
        string? warningsJson,
        IReadOnlySet<string>? corroboratedFields = null)
    {
        song.CaptureOriginalMetadata();

        var highConsensus = agreeingProviderCount >= 2 && confidence >= autoUpgradeConfidence;
        var changes = new List<FieldChange>();

        // Recording/identity fields: the agreeing cluster already corroborated these, so the blanket
        // consensus flag governs whether they may upgrade a good curated value.
        // Album-level fields: only upgrade a good curated value when *that field* was corroborated by
        // ≥2 providers (or no per-field signal exists, e.g. a solo match → fall back to highConsensus).
        bool Upgrade(string field) =>
            corroboratedFields is not null && AlbumLevelFields.Contains(field)
                ? corroboratedFields.Contains(field)
                : highConsensus;

        MergeText(song, "Artist", song.Artist, winner.Artist, v => song.Artist = v, highConsensus, changes);
        MergeText(song, "AlbumArtist", song.AlbumArtist, winner.AlbumArtist, v => song.AlbumArtist = v, highConsensus, changes);
        MergeText(song, "Title", song.Title, winner.Title, v => song.Title = v, highConsensus, changes);
        MergeText(song, "Album", song.Album, winner.Album, v => song.Album = v, Upgrade("Album"), changes);
        MergeText(song, "Artists", song.Artists, winner.Artists, v => song.Artists = v, highConsensus, changes);
        MergeText(song, "ReleaseTypePrimary", song.ReleaseTypePrimary, winner.ReleaseTypePrimary, v => song.ReleaseTypePrimary = v, Upgrade("ReleaseTypePrimary"), changes);
        MergeText(song, "ReleaseTypes", song.ReleaseTypes, winner.ReleaseTypes, v => song.ReleaseTypes = v, Upgrade("ReleaseTypes"), changes);
        MergeNumber(song, "Year", song.Year, winner.Year, v => song.Year = v, Upgrade("Year"), changes);
        MergeNumber(song, "TrackNumber", song.TrackNumber, winner.TrackNumber, v => song.TrackNumber = v, Upgrade("TrackNumber"), changes);
        MergeNumber(song, "DiscNumber", song.DiscNumber, winner.DiscNumber, v => song.DiscNumber = v, Upgrade("DiscNumber"), changes);
        MergeNumber(song, "TotalDiscs", song.TotalDiscs, winner.TotalDiscs, v => song.TotalDiscs = v, Upgrade("TotalDiscs"), changes);
        MergeNumber(song, "TotalTracks", song.TotalTracks, winner.TotalTracks, v => song.TotalTracks = v, Upgrade("TotalTracks"), changes);

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
        Action<string?> set, bool mayUpgrade, List<FieldChange> changes)
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

        if (mayUpgrade)
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
        Action<int?> set, bool mayUpgrade, List<FieldChange> changes)
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

        if (mayUpgrade)
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

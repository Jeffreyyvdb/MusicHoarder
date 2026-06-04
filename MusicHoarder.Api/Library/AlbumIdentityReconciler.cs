using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// Elects a single canonical <see cref="AlbumIdentity"/> across all songs that build into one
/// destination album folder. MusicHoarder enriches every song independently, so tracks of the same
/// real album can end up with different release ids / album names / years; written to disk that way,
/// Navidrome (whose default album key is
/// <c>musicbrainz_albumid|discogs_release_id|albumartistid,album,albumversion,releasedate</c>) splits
/// the album into one entry per distinct release id. This reconciler is the album-level vote the
/// per-song pipeline never takes — the "voters" are the album's own member rows.
/// <para>
/// Every tie-breaker terminates in an ordinal comparison, so election is fully deterministic: the
/// same membership always elects the same identity, across batches, processes, and rebuilds.
/// </para>
/// </summary>
public sealed class AlbumIdentityReconciler : IAlbumIdentityReconciler
{
    public AlbumIdentity Reconcile(IReadOnlyList<SongMetadata> albumMembers)
    {
        ArgumentNullException.ThrowIfNull(albumMembers);
        if (albumMembers.Count == 1)
        {
            return AlbumIdentity.FromSong(albumMembers[0]);
        }

        // Anchor: the release id the most member tracks carry. Most identity fields then travel with
        // that release so the release id and its album/year/types stay internally consistent.
        var releaseId = ElectReleaseId(albumMembers);
        var winners = releaseId is null
            ? albumMembers
            : albumMembers
                .Where(m => string.Equals(m.MusicBrainzReleaseId?.Trim(), releaseId, StringComparison.Ordinal))
                .ToList();

        var albumArtist = Majority(albumMembers.Select(m => m.AlbumArtist), NormalizeKey);
        var albumArtistMbid = ElectAlbumArtistMbid(albumMembers, albumArtist);

        return new AlbumIdentity(
            Album: Majority(winners.Select(m => m.Album), NormalizeKey)
                ?? Majority(albumMembers.Select(m => m.Album), NormalizeKey),
            AlbumArtist: albumArtist,
            Year: MajorityYear(winners) ?? MajorityYear(albumMembers),
            // Compilation is an additive fact: one true VA track shouldn't be de-flagged by a sibling
            // a provider mis-tagged as a single-artist release.
            IsCompilation: albumMembers.Any(m => m.IsCompilation),
            // The album's real disc count is the largest any track knows about (a track that only saw
            // disc 1 under-reports).
            TotalDiscs: MaxPositive(albumMembers.Select(m => m.TotalDiscs)),
            ReleaseTypePrimary: Majority(winners.Select(m => m.ReleaseTypePrimary), NormalizeKey)
                ?? Majority(albumMembers.Select(m => m.ReleaseTypePrimary), NormalizeKey),
            ReleaseTypes: Majority(winners.Select(m => m.ReleaseTypes), NormalizeKey)
                ?? Majority(albumMembers.Select(m => m.ReleaseTypes), NormalizeKey),
            MusicBrainzReleaseId: releaseId,
            MusicBrainzReleaseGroupId: Majority(winners.Select(m => m.MusicBrainzReleaseGroupId), Identity)
                ?? Majority(albumMembers.Select(m => m.MusicBrainzReleaseGroupId), Identity),
            AlbumArtistMusicBrainzId: albumArtistMbid);
    }

    private static string? ElectReleaseId(IReadOnlyList<SongMetadata> members)
    {
        var withId = members
            .Where(m => !string.IsNullOrWhiteSpace(m.MusicBrainzReleaseId))
            .ToList();
        if (withId.Count == 0)
        {
            return null;
        }

        return withId
            .GroupBy(m => m.MusicBrainzReleaseId!.Trim(), StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())          // most member tracks
            .ThenBy(g => g.Min(AnchorKey))              // then the disc-1/track-1 anchor
            .ThenBy(g => g.Key, StringComparer.Ordinal) // then ordinal-lowest id (full determinism)
            .First()
            .Key;
    }

    // Sorts a track to the front of its release when it's the lowest disc, then lowest track; missing
    // numbers sort last so a numbered anchor always wins.
    private static long AnchorKey(SongMetadata m)
    {
        var disc = m.DiscNumber is > 0 ? m.DiscNumber.Value : int.MaxValue;
        var track = m.TrackNumber is > 0 ? m.TrackNumber.Value : int.MaxValue;
        return ((long)disc << 32) | (uint)track;
    }

    private static string? ElectAlbumArtistMbid(IReadOnlyList<SongMetadata> members, string? electedAlbumArtist)
    {
        // Prefer the mbid that travels with the elected album-artist string; fall back to the overall
        // majority so we still emit something if the elected name carried no mbid.
        if (!string.IsNullOrWhiteSpace(electedAlbumArtist))
        {
            var key = NormalizeKey(electedAlbumArtist);
            var matched = members
                .Where(m => !string.IsNullOrWhiteSpace(m.AlbumArtist) && NormalizeKey(m.AlbumArtist!) == key)
                .Select(m => m.AlbumArtistMusicBrainzId);
            if (Majority(matched, Identity) is { } mbid)
            {
                return mbid;
            }
        }

        return Majority(members.Select(m => m.AlbumArtistMusicBrainzId), Identity);
    }

    /// <summary>Most-common non-empty value (ordinal-lowest breaks ties); grouped by <paramref name="keyer"/>.</summary>
    private static string? Majority(IEnumerable<string?> values, Func<string, string> keyer)
    {
        var present = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToList();
        if (present.Count == 0)
        {
            return null;
        }

        return present
            .GroupBy(keyer, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .First()
            .OrderBy(v => v, StringComparer.Ordinal) // deterministic original-cased pick within the group
            .First();
    }

    private static int? MajorityYear(IEnumerable<SongMetadata> members)
    {
        var present = members
            .Where(m => m.Year is > 0)
            .Select(m => m.Year!.Value)
            .ToList();
        if (present.Count == 0)
        {
            return null;
        }

        return present
            .GroupBy(v => v)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key) // tie -> earliest year (prefer the original pressing)
            .First()
            .Key;
    }

    private static int? MaxPositive(IEnumerable<int?> values)
    {
        var present = values.Where(v => v is > 0).Select(v => v!.Value).ToList();
        return present.Count == 0 ? null : present.Max();
    }

    private static string NormalizeKey(string value) => TitleNormalizer.NormalizeForSearch(value);

    private static string Identity(string value) => value;
}

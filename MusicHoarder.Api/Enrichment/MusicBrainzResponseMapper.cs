using MusicHoarder.Api.Metadata;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Pure mapping from the MusicBrainz wire DTOs to the public domain records: artist-credit
/// assembly (display credit, discrete artists, positionally aligned MBIDs, sort credit),
/// genre ranking, release-type flattening and multi-disc tracklist flattening. No IO — the
/// HTTP side lives in <see cref="MusicBrainzWebService"/>.
/// </summary>
internal static class MusicBrainzResponseMapper
{
    internal static MusicBrainzRecording MapRecording(MusicBrainzRecordingDto r)
    {
        var artist = BuildArtistCredit(r.ArtistCredit);
        var release = r.Releases is { Count: > 0 } ? r.Releases[0] : null;
        var releaseGroup = release?.ReleaseGroup;

        var primaryType = string.IsNullOrWhiteSpace(releaseGroup?.PrimaryType)
            ? null
            : releaseGroup!.PrimaryType!.ToLowerInvariant();
        var secondaryTypes = releaseGroup?.SecondaryTypes?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.ToLowerInvariant())
            .ToList() ?? [];
        var releaseTypes = primaryType is null
            ? MultiValue.Join(secondaryTypes)
            : MultiValue.Join(new[] { primaryType }.Concat(secondaryTypes));

        var totalDiscs = release?.Media is { Count: > 0 } media ? media.Count : (int?)null;
        var totalTracks = release?.Media is { Count: > 0 } m
            ? m.Sum(x => x.TrackCount ?? 0) is var sum && sum > 0 ? sum : (int?)null
            : null;

        // Genre: the recording's curated genres (highest-count first, Title Cased, capped at 5), falling
        // back to freeform tags when no genre is set — SpotiFLAC's MusicBrainz-tag approach.
        var genre = BuildGenre(r.Genres) ?? BuildGenre(r.Tags);

        // Label + catalog number off the release's first label-info entry that names a label.
        var labelInfo = release?.LabelInfo?.FirstOrDefault(li => !string.IsNullOrWhiteSpace(li.Label?.Name));
        var catalogNumber = release?.LabelInfo?
            .Select(li => li.CatalogNumber)
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));

        return new MusicBrainzRecording(
            Id: r.Id,
            Title: r.Title ?? string.Empty,
            Artist: artist,
            AlbumArtist: ArtistCreditNormalizer.GetPrimaryArtist(artist),
            ReleaseId: release?.Id,
            ReleaseTitle: release?.Title,
            Year: ReleaseDateParser.ParseYear(release?.Date),
            Isrc: r.Isrcs is { Count: > 0 } ? r.Isrcs[0] : null,
            LengthMs: r.Length,
            Score: r.Score ?? 100,
            CandidateCount: 1,
            Artists: BuildDiscreteArtists(r.ArtistCredit),
            ArtistMusicBrainzIds: BuildArtistIds(r.ArtistCredit),
            AlbumArtistMusicBrainzId: r.ArtistCredit is { Count: > 0 } ? r.ArtistCredit[0].Artist?.Id : null,
            ReleaseGroupId: releaseGroup?.Id,
            ReleaseTypePrimary: primaryType,
            ReleaseTypes: releaseTypes,
            IsCompilation: secondaryTypes.Contains("compilation"),
            TotalDiscs: totalDiscs,
            TotalTracks: totalTracks,
            Genre: genre,
            ReleaseDate: ReleaseDateParser.Normalize(release?.Date),
            OriginalReleaseDate: ReleaseDateParser.Normalize(releaseGroup?.FirstReleaseDate),
            Label: labelInfo?.Label?.Name,
            CatalogNumber: string.IsNullOrWhiteSpace(catalogNumber) ? null : catalogNumber,
            Barcode: string.IsNullOrWhiteSpace(release?.Barcode) ? null : release!.Barcode,
            ArtistSort: BuildArtistSortCredit(r.ArtistCredit),
            AlbumArtistSort: r.ArtistCredit is { Count: > 0 } ? NullIfBlank(r.ArtistCredit[0].Artist?.SortName) : null);
    }

    internal static MusicBrainzRelease MapRelease(MusicBrainzReleaseDetailDto r)
    {
        var artist = BuildArtistCredit(r.ArtistCredit);
        var media = r.Media ?? [];

        var tracks = new List<MusicBrainzReleaseTrack>();
        foreach (var medium in media)
        {
            var disc = medium.Position ?? 1;
            if (medium.Tracks is null) continue;
            foreach (var t in medium.Tracks)
            {
                tracks.Add(new MusicBrainzReleaseTrack(
                    DiscNumber: disc,
                    // `number` is the printed track designation (can be non-numeric on vinyl, e.g. "A1");
                    // `position` is the reliable 1-based ordinal. Prefer position.
                    TrackNumber: t.Position ?? 0,
                    Title: t.Title ?? t.Recording?.Title,
                    LengthMs: t.Length ?? t.Recording?.Length,
                    RecordingId: t.Recording?.Id));
            }
        }

        var totalDiscs = media.Count > 0 ? media.Count : (int?)null;
        var totalTracks = media.Count > 0
            ? media.Sum(m => m.TrackCount ?? (m.Tracks?.Count ?? 0)) is var sum && sum > 0 ? sum : (int?)null
            : null;

        return new MusicBrainzRelease(
            Id: r.Id,
            Title: r.Title,
            AlbumArtist: string.IsNullOrWhiteSpace(artist) ? null : ArtistCreditNormalizer.GetPrimaryArtist(artist),
            Year: ReleaseDateParser.ParseYear(r.Date),
            TotalDiscs: totalDiscs,
            TotalTracks: totalTracks,
            Tracks: tracks);
    }

    internal static IReadOnlyList<MusicBrainzReleaseSearchResult> MapReleaseSearchResults(
        List<MusicBrainzReleaseSearchItemDto>? releases)
    {
        if (releases is null or { Count: 0 })
            return [];

        return releases
            .Where(r => !string.IsNullOrWhiteSpace(r.Id))
            .Select(r => new MusicBrainzReleaseSearchResult(r.Id!, r.Title, ReleaseDateParser.ParseYear(r.Date), r.TrackCount, r.Score ?? 0))
            .ToList();
    }

    /// <summary>
    /// Genres/tags → a ';'-joined multi-value string: highest count first, Title Cased, capped at 5,
    /// de-duplicated. Returns null when there are none.
    /// </summary>
    private static string? BuildGenre(List<MusicBrainzGenreDto>? genres)
    {
        if (genres is null or { Count: 0 })
            return null;

        var names = genres
            .Where(g => !string.IsNullOrWhiteSpace(g.Name))
            .OrderByDescending(g => g.Count ?? 0)
            .Select(g => TitleCase(g.Name!.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();

        return names.Count == 0 ? null : MultiValue.Join(names);
    }

    private static string TitleCase(string value)
        => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());

    // The sort-name credit (ARTISTSORT): each credited artist's sort-name concatenated with the same
    // join phrases as the display credit, so "The Beatles feat. Billy Preston" sorts as
    // "Beatles, The feat. Preston, Billy". Falls back to the display name when a sort-name is absent.
    private static string? BuildArtistSortCredit(List<MusicBrainzArtistCreditDto>? credits)
    {
        if (credits is null or { Count: 0 })
            return null;

        var sort = string.Concat(credits.Select(c =>
            (c.Artist?.SortName ?? c.Name ?? c.Artist?.Name ?? string.Empty) + (c.JoinPhrase ?? string.Empty))).Trim();
        return NullIfBlank(sort);
    }

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string BuildArtistCredit(List<MusicBrainzArtistCreditDto>? credits)
    {
        if (credits is null or { Count: 0 })
            return string.Empty;

        return string.Concat(credits.Select(c => (c.Name ?? c.Artist?.Name ?? string.Empty) + (c.JoinPhrase ?? string.Empty))).Trim();
    }

    // Discrete artist names (one per credited artist, no join phrases) for the multi-value ARTISTS tag.
    private static string? BuildDiscreteArtists(List<MusicBrainzArtistCreditDto>? credits)
        => credits is null or { Count: 0 }
            ? null
            : MultiValue.Join(credits.Select(c => c.Artist?.Name ?? c.Name));

    // Per-artist MBIDs, positionally aligned with BuildDiscreteArtists (one entry per credited artist).
    private static string? BuildArtistIds(List<MusicBrainzArtistCreditDto>? credits)
        => credits is null or { Count: 0 }
            ? null
            : MultiValue.Join(credits.Select(c => c.Artist?.Id));
}

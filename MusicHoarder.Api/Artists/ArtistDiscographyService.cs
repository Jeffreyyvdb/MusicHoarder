using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artists;

/// <summary>The catalog artist behind a discography, and how sure the link is.</summary>
/// <param name="Status">linked, chosen, followed, unconfirmed or not-found.</param>
public sealed record ArtistCatalogDto(string Status, string? DeezerArtistId, string? Name, string? PictureUrl, int Fans);

public sealed record ArtistCandidateDto(string Id, string? Name, string? PictureUrl, int Fans, int AlbumCount);

/// <summary>
/// One release on the discography, with what the owner has of it.
/// <paramref name="LibraryAlbum"/> is the album name as the owner's songs are tagged, so the page can
/// open the album it already shows; <paramref name="TotalTracks"/> is known once the release has a
/// canonical tracklist. The queue counts cover every wishlist item hanging off that tracklist.
/// </summary>
public sealed record ArtistReleaseDto(
    string Key,
    string Kind,
    string Title,
    DateOnly? ReleaseDate,
    string? CoverUrl,
    bool Explicit,
    IReadOnlyList<string> EditionIds,
    bool Upcoming,
    int OwnedTracks,
    int? TotalTracks,
    string? LibraryAlbum,
    int QueuedTracks,
    int DownloadedTracks,
    int FailedTracks);

public sealed record FollowedArtistDto(
    int Id,
    string DeezerArtistId,
    string Name,
    string? PictureUrl,
    IReadOnlyList<string> ReleaseKinds,
    bool AutoDownload,
    DateTime FollowedAtUtc,
    DateTime? LastCheckedAtUtc,
    DateTime? NextCheckAtUtc,
    string? LastError,
    int NewReleaseCount);

public sealed record ArtistDiscographyResponse(
    string Artist,
    ArtistCatalogDto Catalog,
    IReadOnlyList<ArtistCandidateDto> Candidates,
    FollowedArtistDto? Follow,
    IReadOnlyList<ArtistReleaseDto> Releases,
    bool ReleasesUnavailable,
    bool DownloadsEnabled);

/// <summary>
/// An artist's discography as the artist page shows it: every release the trusted catalog artist
/// lists, folded by edition, each annotated with what the owner already has and what is on its way.
/// Reads the caller's own rows through the ambient owner filter.
/// </summary>
public sealed class ArtistDiscographyService(
    MusicHoarderDbContext db,
    IDeezerCatalogService deezer,
    ArtistCatalogResolver resolver,
    IOptions<MusicEnricherOptions> options)
{
    public async Task<ArtistDiscographyResponse> BuildAsync(string artistName, CancellationToken ct)
    {
        var artistKey = TitleNormalizer.NormalizeForSearch(artistName);
        var owned = await ArtistSongs.LoadAsync(db.Songs, artistName, ct);

        // A follow names its catalog artist outright, so it stands in for an unproven link.
        var follow = await db.FollowedArtists.AsNoTracking()
            .Where(f => f.ArtistKey == artistKey)
            .OrderBy(f => f.Id)
            .FirstOrDefaultAsync(ct);

        ArtistCatalogDto catalog;
        IReadOnlyList<ArtistCandidateDto> candidates = [];
        string? deezerArtistId;
        if (follow is not null && await TrustedLinkAsync(artistKey, ct) is null)
        {
            deezerArtistId = follow.DeezerArtistId;
            catalog = new ArtistCatalogDto("followed", follow.DeezerArtistId, follow.Name, follow.PictureUrl, 0);
        }
        else
        {
            var resolution = await resolver.ResolveAsync(artistName, owned, ct);
            var link = resolution.Link;
            deezerArtistId = resolution.IsTrusted ? link.DeezerArtistId : null;
            catalog = new ArtistCatalogDto(StatusName(link.Status), deezerArtistId, link.Name, link.PictureUrl, link.Fans);
            candidates = resolution.Candidates
                .Select(c => new ArtistCandidateDto(c.Id!, c.Name, c.PictureUrl, c.Fans, c.AlbumCount))
                .ToList();
            follow ??= deezerArtistId is null
                ? null
                : await db.FollowedArtists.AsNoTracking().FirstOrDefaultAsync(f => f.DeezerArtistId == deezerArtistId, ct);
        }

        var followDto = follow is null ? null : await ToDtoAsync(follow, ct);
        var downloadsEnabled = options.Value.EnableWishlistDownloads;
        if (deezerArtistId is null)
            return new ArtistDiscographyResponse(artistName, catalog, candidates, followDto, [], false, downloadsEnabled);

        var listing = await deezer.GetArtistReleasesAsync(deezerArtistId, ct);
        if (listing is null)
            return new ArtistDiscographyResponse(artistName, catalog, candidates, followDto, [], true, downloadsEnabled);

        var releases = await AnnotateAsync(ArtistDiscography.Group(listing), owned, artistKey, catalog.Name, ct);
        return new ArtistDiscographyResponse(artistName, catalog, candidates, followDto, releases, false, downloadsEnabled);
    }

    private async Task<ArtistCatalogLink?> TrustedLinkAsync(string artistKey, CancellationToken ct) =>
        await db.ArtistCatalogLinks.AsNoTracking()
            .FirstOrDefaultAsync(l => l.ArtistKey == artistKey
                && (l.Status == ArtistCatalogLinkStatus.Linked || l.Status == ArtistCatalogLinkStatus.Chosen), ct);

    private async Task<List<ArtistReleaseDto>> AnnotateAsync(
        IReadOnlyList<DiscographyRelease> releases,
        IReadOnlyList<ArtistSongFacts> owned,
        string artistKey,
        string? catalogName,
        CancellationToken ct)
    {
        // The owner's albums of this artist by album key, with the spelling most of the songs carry.
        var ownedAlbums = owned
            .Where(s => !string.IsNullOrWhiteSpace(s.Album))
            .GroupBy(s => TitleNormalizer.NormalizeForSearch(s.Album))
            .Where(g => g.Key.Length > 0)
            .ToDictionary(
                g => g.Key,
                g => (Count: g.Count(), Name: g.GroupBy(s => s.Album!).OrderByDescending(n => n.Count()).First().Key),
                StringComparer.Ordinal);
        var ownedTitles = owned
            .Select(s => TitleNormalizer.NormalizeForSearch(s.Title))
            .Where(k => k.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        // Canonical tracklists for these releases, under either spelling of the artist.
        var artistKeys = new[] { artistKey, TitleNormalizer.NormalizeForSearch(catalogName) }
            .Where(k => k.Length > 0).Distinct().ToList();
        var albumKeys = releases.Select(r => TitleNormalizer.NormalizeForSearch(r.Title)).Distinct().ToList();
        var canonical = await db.CanonicalAlbums.AsNoTracking()
            .Where(a => artistKeys.Contains(a.ArtistKey) && albumKeys.Contains(a.AlbumKey))
            .Select(a => new { a.Id, a.AlbumKey, a.ResolvedTrackCount, a.Status })
            .ToListAsync(ct);
        var canonicalByAlbum = canonical
            .GroupBy(a => a.AlbumKey)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var canonicalIds = canonical.Select(a => a.Id).ToList();
        var queue = await db.WishlistItems.AsNoTracking()
            .Where(w => w.CanonicalAlbumId != null && canonicalIds.Contains(w.CanonicalAlbumId!.Value))
            .GroupBy(w => new { CanonicalAlbumId = w.CanonicalAlbumId!.Value, w.Status })
            .Select(g => new { g.Key.CanonicalAlbumId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var result = new List<ArtistReleaseDto>(releases.Count);
        foreach (var release in releases)
        {
            var albumKey = TitleNormalizer.NormalizeForSearch(release.Title);
            var ownedAlbum = ownedAlbums.TryGetValue(albumKey, out var a) ? a : default;
            var ownedTracks = ownedAlbum.Count;
            // A single is named after its song: owning the song (on any album) is owning the single.
            if (ownedTracks == 0 && release.Kind == ArtistReleaseKinds.Single && ownedTitles.Contains(albumKey))
                ownedTracks = 1;

            var rows = canonicalByAlbum.GetValueOrDefault(albumKey) ?? [];
            var ids = rows.Select(r => r.Id).ToHashSet();
            var fetched = rows.Where(r => r.Status == CanonicalAlbumStatus.Fetched && r.ResolvedTrackCount > 0).ToList();
            int? total = fetched.Count > 0 ? fetched.Max(r => r.ResolvedTrackCount) : null;
            if (total is null && release.Kind == ArtistReleaseKinds.Single && ownedTracks > 0) total = ownedTracks;

            var mine = queue.Where(q => ids.Contains(q.CanonicalAlbumId)).ToList();
            int Count(params WishlistItemStatus[] statuses) => mine.Where(q => statuses.Contains(q.Status)).Sum(q => q.Count);

            result.Add(new ArtistReleaseDto(
                release.Key,
                ArtistDiscography.WireKind(release.Kind),
                release.Title,
                release.ReleaseDate,
                release.CoverUrl,
                release.Explicit,
                release.EditionIds,
                Upcoming: release.ReleaseDate is { } d && d > today,
                OwnedTracks: ownedTracks,
                TotalTracks: total,
                LibraryAlbum: ownedAlbum.Name,
                QueuedTracks: Count(WishlistItemStatus.Pending, WishlistItemStatus.Downloading),
                DownloadedTracks: Count(WishlistItemStatus.Downloaded),
                FailedTracks: Count(WishlistItemStatus.Failed, WishlistItemStatus.NotFound)));
        }

        return result;
    }

    public async Task<FollowedArtistDto> ToDtoAsync(FollowedArtist follow, CancellationToken ct)
    {
        var newReleases = await db.FollowedArtistReleases.AsNoTracking()
            .CountAsync(r => r.FollowedArtistId == follow.Id
                && FollowedReleaseDecisions.News.Contains(r.Decision), ct);
        return ToDto(follow, newReleases);
    }

    public static FollowedArtistDto ToDto(FollowedArtist follow, int newReleaseCount) => new(
        follow.Id,
        follow.DeezerArtistId,
        follow.Name,
        follow.PictureUrl,
        KindNames(follow.ReleaseKinds),
        follow.AutoDownload,
        follow.FollowedAtUtc,
        follow.LastCheckedAtUtc,
        follow.NextCheckAtUtc,
        follow.LastError,
        newReleaseCount);

    public static IReadOnlyList<string> KindNames(ArtistReleaseKinds kinds) =>
        new[] { ArtistReleaseKinds.Album, ArtistReleaseKinds.Ep, ArtistReleaseKinds.Single, ArtistReleaseKinds.Compilation }
            .Where(k => kinds.HasFlag(k))
            .Select(ArtistDiscography.WireKind)
            .ToList();

    public static ArtistReleaseKinds ParseKinds(IEnumerable<string>? names)
    {
        var kinds = ArtistReleaseKinds.None;
        foreach (var name in names ?? [])
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            kinds |= ArtistDiscography.KindOf(name);
        }

        return kinds;
    }

    private static string StatusName(ArtistCatalogLinkStatus status) => status switch
    {
        ArtistCatalogLinkStatus.Linked => "linked",
        ArtistCatalogLinkStatus.Chosen => "chosen",
        ArtistCatalogLinkStatus.Unconfirmed => "unconfirmed",
        _ => "not-found",
    };
}

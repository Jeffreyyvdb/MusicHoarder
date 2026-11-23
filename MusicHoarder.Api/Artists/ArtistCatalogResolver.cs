using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artists;

/// <summary>What the resolver knows about a library artist's catalog counterpart.</summary>
/// <param name="Candidates">The catalog artists of a similar name, offered for a manual pick while unproven.</param>
public sealed record ArtistCatalogResolution(ArtistCatalogLink Link, IReadOnlyList<DeezerArtistCandidate> Candidates)
{
    /// <summary>Only a proven or a chosen link may offer a discography.</summary>
    public bool IsTrusted => Link.Status is ArtistCatalogLinkStatus.Linked or ArtistCatalogLinkStatus.Chosen
        && !string.IsNullOrEmpty(Link.DeezerArtistId);
}

/// <summary>
/// Decides which catalog artist a library artist is. A search for a name always answers, and two
/// artists share names all the time, so a candidate is accepted only when its catalog lists something
/// the owner already has from this artist (<see cref="ArtistDiscography.ProvesIdentity"/>). Anything
/// less is <see cref="ArtistCatalogLinkStatus.Unconfirmed"/>: the page shows the candidates and a
/// person picks one, rather than offering a stranger's catalog for download.
/// </summary>
public sealed class ArtistCatalogResolver(
    MusicHoarderDbContext db,
    IDeezerCatalogService deezer,
    IOptions<MusicEnricherOptions> options,
    ILogger<ArtistCatalogResolver> logger)
{
    /// <summary>How many same-name candidates have their catalogs read before giving up.</summary>
    private const int MaxCandidatesToProve = 3;

    /// <summary>How long an unproven answer stands before the catalog is asked again.</summary>
    private static readonly TimeSpan UnprovenRetry = TimeSpan.FromDays(3);

    public async Task<ArtistCatalogResolution> ResolveAsync(
        string artistName, IReadOnlyList<ArtistSongFacts> ownedByArtist, CancellationToken ct)
    {
        var key = TitleNormalizer.NormalizeForSearch(artistName);
        var now = DateTime.UtcNow;
        var link = await db.ArtistCatalogLinks.FirstOrDefaultAsync(l => l.ArtistKey == key, ct);

        if (link is { Status: ArtistCatalogLinkStatus.Linked or ArtistCatalogLinkStatus.Chosen })
            return new ArtistCatalogResolution(link, []);

        // Inside the retry window nothing is re-proven. A not-found answer needs no search at all;
        // an unconfirmed one still shows its candidates.
        var waiting = link is not null && link.NextCheckAfterUtc is { } next && next > now;
        if (waiting && link!.Status == ArtistCatalogLinkStatus.NotFound)
            return new ArtistCatalogResolution(link, []);

        var candidates = await CandidatesAsync(artistName, ct);
        if (waiting)
            return new ArtistCatalogResolution(link!, candidates);

        link ??= db.ArtistCatalogLinks.Add(new ArtistCatalogLink { ArtistKey = key }).Entity;
        link.CheckedAtUtc = now;

        if (candidates.Count == 0)
        {
            link.Status = ArtistCatalogLinkStatus.NotFound;
            link.NextCheckAfterUtc = now + UnprovenRetry;
            await db.SaveChangesAsync(ct);
            return new ArtistCatalogResolution(link, candidates);
        }

        foreach (var candidate in candidates.Take(MaxCandidatesToProve))
        {
            var releases = await deezer.GetArtistReleasesAsync(candidate.Id!, ct);
            if (releases is null || !ArtistDiscography.ProvesIdentity(releases, ownedByArtist)) continue;

            Apply(link, candidate, ArtistCatalogLinkStatus.Linked, now);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Linked artist {Artist} to Deezer artist {DeezerId} ({Name})",
                artistName, candidate.Id, candidate.Name);
            return new ArtistCatalogResolution(link, []);
        }

        // Keep the likeliest candidate's face for the page, but not its id: unproven means untrusted.
        link.Status = ArtistCatalogLinkStatus.Unconfirmed;
        link.DeezerArtistId = null;
        link.Name = candidates[0].Name;
        link.PictureUrl = candidates[0].PictureUrl;
        link.Fans = candidates[0].Fans;
        link.NextCheckAfterUtc = now + UnprovenRetry;
        await db.SaveChangesAsync(ct);
        return new ArtistCatalogResolution(link, candidates);
    }

    /// <summary>A person's pick wins over any search, and is never re-checked.</summary>
    public async Task<ArtistCatalogLink?> ChooseAsync(string artistName, string deezerArtistId, CancellationToken ct)
    {
        var artist = await deezer.GetArtistAsync(deezerArtistId, ct);
        if (artist is null) return null;

        var key = TitleNormalizer.NormalizeForSearch(artistName);
        var link = await db.ArtistCatalogLinks.FirstOrDefaultAsync(l => l.ArtistKey == key, ct)
            ?? db.ArtistCatalogLinks.Add(new ArtistCatalogLink { ArtistKey = key }).Entity;
        Apply(link, artist with { Id = artist.Id ?? deezerArtistId }, ArtistCatalogLinkStatus.Chosen, DateTime.UtcNow);
        await db.SaveChangesAsync(ct);
        return link;
    }

    /// <summary>Same-name catalog artists, the exact spelling first, then the most followed.</summary>
    private async Task<IReadOnlyList<DeezerArtistCandidate>> CandidatesAsync(string artistName, CancellationToken ct)
    {
        var key = TitleNormalizer.NormalizeForSearch(artistName);
        // Links are stored under this key, and every symbol-only name ("¥$") shares the empty one, so a
        // link made for one would answer for all of them.
        if (key.Length == 0)
            return [];

        var threshold = options.Value.IdentityArtistThreshold;
        var found = await deezer.SearchArtistCandidatesAsync(artistName, ct);
        return found
            .Where(c => !string.IsNullOrEmpty(c.Id) && ArtistNameMatch.SameSpelling(artistName, c.Name, threshold))
            .OrderByDescending(c => TitleNormalizer.NormalizeForSearch(c.Name) == key)
            .ThenByDescending(c => c.Fans)
            .ToList();
    }

    private static void Apply(ArtistCatalogLink link, DeezerArtistCandidate artist, ArtistCatalogLinkStatus status, DateTime now)
    {
        link.Status = status;
        link.DeezerArtistId = artist.Id;
        link.Name = artist.Name;
        link.PictureUrl = artist.PictureUrl;
        link.Fans = artist.Fans;
        link.CheckedAtUtc = now;
        link.NextCheckAfterUtc = null;
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Artists;
using MusicHoarder.Api.Deezer;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Tests.Deezer;
using static MusicHoarder.Api.Tests.Artists.ArtistTestKit;

namespace MusicHoarder.Api.Tests.Artists;

public class ArtistCatalogResolverTests
{
    private static readonly ArtistSongFacts OwnsDiscovery =
        new(1, "Digital Love", "Discovery", "Daft Punk", "Daft Punk", null, null, 1, 3, SongAcquisitionIntent.Explicit);

    [Fact]
    public async Task Resolve_LinksTheCandidateWhoseCatalogListsAnOwnedAlbum()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        // The most-followed namesake is not the one: only the second lists "Discovery".
        deezer.ArtistSearchResults.AddRange(
        [
            new DeezerArtistCandidate("Daft Punk", "p1", "impostor", Fans: 9_000_000),
            new DeezerArtistCandidate("Daft Punk", "p2", "real", Fans: 10),
        ]);
        deezer.ArtistReleases["impostor"] = [Release("x", "Cover Versions")];
        deezer.ArtistReleases["real"] = [Release("d", "Discovery")];

        var resolution = await Resolver(db, deezer).ResolveAsync("Daft Punk", [OwnsDiscovery], default);

        Assert.True(resolution.IsTrusted);
        Assert.Equal(ArtistCatalogLinkStatus.Linked, resolution.Link.Status);
        Assert.Equal("real", resolution.Link.DeezerArtistId);
    }

    [Fact]
    public async Task Resolve_WithoutProof_IsUnconfirmed_AndOffersTheCandidates()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.ArtistSearchResults.Add(new DeezerArtistCandidate("Daft Punk", "p", "stranger"));
        deezer.ArtistReleases["stranger"] = [Release("x", "Nothing You Own")];

        var resolution = await Resolver(db, deezer).ResolveAsync("Daft Punk", [OwnsDiscovery], default);

        Assert.False(resolution.IsTrusted);
        Assert.Equal(ArtistCatalogLinkStatus.Unconfirmed, resolution.Link.Status);
        // An unproven answer keeps no id — nothing may treat it as the artist.
        Assert.Null(resolution.Link.DeezerArtistId);
        Assert.Equal(["stranger"], resolution.Candidates.Select(c => c.Id));
    }

    [Fact]
    public async Task Resolve_IgnoresCandidatesWithAnotherName()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.ArtistSearchResults.Add(new DeezerArtistCandidate("Daft Punk Tribute Orchestra", null, "tribute"));
        deezer.ArtistReleases["tribute"] = [Release("d", "Discovery")];

        var resolution = await Resolver(db, deezer).ResolveAsync("Daft Punk", [OwnsDiscovery], default);

        Assert.Equal(ArtistCatalogLinkStatus.NotFound, resolution.Link.Status);
    }

    [Fact]
    public async Task Choose_APersonsPickIsTrusted_AndStays()
    {
        await using var db = NewContext();
        var deezer = new FakeDeezerCatalogService();
        deezer.ArtistSearchResults.Add(new DeezerArtistCandidate("Daft Punk", "p", "picked"));
        deezer.ArtistReleases["picked"] = [Release("x", "Nothing You Own")];
        var resolver = Resolver(db, deezer);

        await resolver.ChooseAsync("Daft Punk", "picked", default);
        var resolution = await resolver.ResolveAsync("Daft Punk", [OwnsDiscovery], default);

        Assert.True(resolution.IsTrusted);
        Assert.Equal(ArtistCatalogLinkStatus.Chosen, resolution.Link.Status);
        Assert.Equal("picked", resolution.Link.DeezerArtistId);
    }

    private static ArtistCatalogResolver Resolver(MusicHoarderDbContext db, FakeDeezerCatalogService deezer) =>
        new(db, deezer, Microsoft.Extensions.Options.Options.Create(EnricherOptions()), NullLogger<ArtistCatalogResolver>.Instance);
}

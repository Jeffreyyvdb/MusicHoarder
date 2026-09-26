using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class ArtistAliasMapTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    // Every merge onto "Nas" stores the canonical's own key, so this row exists wherever Nas was
    // merged — and under the search key it matched every featuring credit of his too.
    [Theory]
    [InlineData("Nas feat. AZ")]
    [InlineData("Nas (feat. AZ)")]
    [InlineData("Nas (featuring AZ)")]
    [InlineData("Nas (feat. Lauryn Hill)")]
    [InlineData("Nas ft AZ")]
    [InlineData("Nas & AZ")]
    public async Task ResolveName_NeverRewritesAMultiArtistCredit(string credit)
    {
        var map = await MapWith(("nas", "Nas"));

        Assert.Null(map.ResolveName(Owner, credit));
    }

    [Fact]
    public async Task ResolveName_StillMapsAPlainRespelling()
    {
        var map = await MapWith(("nas", "Nas"));

        Assert.Equal("Nas", map.ResolveName(Owner, "NAS"));
        Assert.Null(map.ResolveName(Owner, "Nas")); // already the canonical spelling
    }

    [Fact]
    public async Task ResolveName_RefusesACreditItsDiscreteListShowsJoined()
    {
        // "y" is no joiner the splitter knows; the match's own discrete list is the proof.
        var map = await MapWith(("anuel aa y ozuna", "Anuel AA"));

        Assert.Null(map.ResolveName(Owner, "Anuel AA y Ozuna", "Anuel AA; Ozuna"));
    }

    [Theory]
    [InlineData("hef met jayh", "Hef", "Hef met Jayh")]          // a credit collapsed onto its lead
    [InlineData("2pac outlawz", "2Pac", "2Pac + Outlawz")]
    [InlineData("nas", "Nas (featuring AZ)", "NAS")]             // a name expanded into a credit
    [InlineData("hef", "Hef met Jayh", "HEF")]
    public async Task ResolveName_IgnoresLegacyAliasesThatAreNotSpellings(string key, string canonical, string name)
    {
        // Merges made before the collab guards stored these; applying one on the next heal or
        // re-enrichment would strip (or add) artists again, whatever the library now says.
        var map = await MapWith((key, canonical));

        Assert.Null(map.ResolveName(Owner, name));
    }

    [Fact]
    public async Task ResolveName_LegacySearchKeyedRowsResolvePlainNames()
    {
        // Rows from before the artist key: for a plain name the search and artist keys coincide.
        var map = await MapWith(("ms lauryn hill", "Lauryn Hill"), ("jayz", "JAY-Z"));

        Assert.Equal("Lauryn Hill", map.ResolveName(Owner, "Ms. Lauryn Hill"));
        Assert.Equal("JAY-Z", map.ResolveName(Owner, "Jaÿ-z"));
    }

    [Fact]
    public async Task ResolveName_NeverFallsBackToTheSearchKey()
    {
        // The search key drops bracketed text, so a fallback would have the "future" row every
        // merge onto "Future" stores erase the qualifier from rows nobody merged.
        var map = await MapWith(("future", "Future"));

        Assert.Null(map.ResolveName(Owner, "Future (rapper)"));
    }

    [Fact]
    public async Task MapList_MapsPlainSegments_KeepsCreditsAndCount()
    {
        var map = await MapWith(("nas", "Nas"), ("hef met jayh", "Hef"));

        Assert.Equal("Nas; AZ; Hef met Jayh", map.MapList(Owner, "NAS; AZ; Hef met Jayh"));
        Assert.Null(map.MapList(Owner, "Nas feat. AZ; AZ"));
    }

    [Fact]
    public async Task ResolveName_IsScopedToTheOwner()
    {
        var map = await MapWith(("nas", "Nas"));

        Assert.Null(map.ResolveName(Guid.NewGuid(), "NAS"));
    }

    private static async Task<ArtistAliasMap> MapWith(params (string Key, string Canonical)[] aliases)
    {
        var db = new MusicHoarderDbContext(new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);
        await using (db)
        {
            db.ArtistAliases.AddRange(aliases.Select(a => new ArtistAlias
            {
                OwnerUserId = Owner,
                AliasKey = a.Key,
                CanonicalName = a.Canonical,
                CreatedAtUtc = DateTime.UtcNow,
            }));
            await db.SaveChangesAsync();
            return await ArtistAliasMap.LoadAsync(db, CancellationToken.None);
        }
    }
}

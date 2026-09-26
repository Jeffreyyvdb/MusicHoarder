using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Metadata;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class ArtistDuplicateServiceTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;

    // --- Detect ---

    [Fact]
    public async Task Detect_ClustersSpellingsThatNormalizeIdentically()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "JAY-Z"),
            Song("/a/2.mp3", artist: "JAY-Z"),
            Song("/a/3.mp3", artist: "JAYZ"),
            Song("/a/4.mp3", artist: "Jaÿ-z"),
            Song("/a/5.mp3", artist: "Kanye West"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        var cluster = Assert.Single(report.Clusters);
        Assert.Equal("JAY-Z", cluster.SuggestedCanonical); // majority spelling
        Assert.Equal(
            ["JAY-Z", "JAYZ", "Jaÿ-z"],
            cluster.Variants.Select(v => v.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Detect_BridgesDifferentKeysViaSharedMbid()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Lauryn Hill", artists: "Lauryn Hill", artistMbids: "mbid-lauryn"),
            Song("/a/2.mp3", artist: "Ms. Lauryn Hill", artists: "Ms. Lauryn Hill", artistMbids: "mbid-lauryn"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        var cluster = Assert.Single(report.Clusters);
        Assert.Contains("same MusicBrainz artist id", cluster.Evidence);
        Assert.Equal(2, cluster.Variants.Count);
    }

    [Fact]
    public async Task Detect_NeverBridgesUnlikeNamesThatShareAnMbid()
    {
        // The Marvin Gaye incident, with the cast the live library actually produced. Rows carry
        // ids that are not theirs (an elected album artist keeps the previous row's id), so one
        // shared id must never be evidence on its own — the closest of these pairs scores 51.
        await using var db = NewContext();
        string[] cast =
        [
            "Marvin Gaye", "Lijpe", "LouiVos", "Kid Cudi", "Mula B", "Dominic Fike",
            "Various Artists", "Verschiedene Interpreten",
        ];
        db.Songs.AddRange(cast.Select((name, i) =>
            Song($"/a/{i}.mp3", artist: name, albumArtist: name, artists: name,
                artistMbids: "mbid-stray", albumArtistMbid: "mbid-stray")));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        Assert.Empty(report.Clusters);
    }

    [Fact]
    public async Task Detect_NeverChainsUnlikeNamesThroughAMiddleSpelling()
    {
        // "Kanye West" ~ "Kanye" ~ "Kanye Omari" are two corroborated edges, but the ENDS are not
        // alike. A merge rewrites every variant to one canonical, so a variant two hops away must
        // not ride along — that transitivity is how one cluster swallowed a whole library.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Kanye West", artists: "Kanye West", artistMbids: "mbid-kanye"),
            Song("/a/2.mp3", artist: "Kanye West", artists: "Kanye West", artistMbids: "mbid-kanye"),
            Song("/a/3.mp3", artist: "Kanye", artists: "Kanye", artistMbids: "mbid-kanye"),
            Song("/a/4.mp3", artist: "Kanye Omari", artists: "Kanye Omari", artistMbids: "mbid-kanye"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        var cluster = Assert.Single(report.Clusters);
        Assert.Equal("Kanye West", cluster.SuggestedCanonical);
        Assert.Equal(["Kanye", "Kanye West"], cluster.Variants.Select(v => v.Name).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Detect_NeverClustersVariousArtistsPlaceholders()
    {
        // "Various Artists" is a slot, not an artist: it sits under every album artist in the
        // library and collects all their ids. Merging it would rename every compilation.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Various Artists", artists: "Various Artists", artistMbids: "mbid-x"),
            Song("/a/2.mp3", artist: "Various Artist", artists: "Various Artist", artistMbids: "mbid-x"),
            Song("/a/3.mp3", artist: "VA", artists: "VA", artistMbids: "mbid-x"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        Assert.Empty(report.Clusters);
    }

    [Fact]
    public async Task Detect_FuzzyClustersNearSpellings()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "The Notorious B.I.G."),
            Song("/a/2.mp3", artist: "Notorious BIG"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        var cluster = Assert.Single(report.Clusters);
        Assert.Contains("similar spelling", cluster.Evidence);
    }

    [Fact]
    public async Task Detect_RespectsDismissedPairs()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Lauryn Hill", artists: "Lauryn Hill", artistMbids: "mbid-lauryn"),
            Song("/a/2.mp3", artist: "Ms. Lauryn Hill", artists: "Ms. Lauryn Hill", artistMbids: "mbid-lauryn"));
        await db.SaveChangesAsync();

        await Service(db).DismissAsync(Owner, ["Lauryn Hill", "Ms. Lauryn Hill"]);
        var report = await Service(db).DetectAsync(Owner);

        Assert.Empty(report.Clusters);
    }

    [Fact]
    public async Task Detect_ReportsCombinedCreditCandidates()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "JAY-Z & Kanye West"),          // no discrete Artists → credit-only
            Song("/a/2.mp3", artist: "JAY-Z", artists: "JAY-Z"),
            Song("/a/3.mp3", artist: "Kanye West", artists: "Kanye West"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        var candidate = Assert.Single(report.CombinedCredits);
        Assert.Equal("JAY-Z & Kanye West", candidate.Credit);
        Assert.Equal(["JAY-Z", "Kanye West"], candidate.Parts);
        Assert.Equal(1, candidate.SongCount);
    }

    [Fact]
    public async Task Detect_NeverClustersFeaturingCreditsAsSpellingVariants()
    {
        // "Kanye West feat. Kid Cudi" normalizes to "kanye west" — clustering it with "Kanye West"
        // would suggest a merge that deletes the featuring credit (seen on real library data).
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Kanye West", artists: "Kanye West"),
            Song("/a/2.mp3", artist: "Kanye West feat. Kid Cudi"),
            Song("/a/3.mp3", artist: "Kanye West feat. Bon Iver"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        Assert.Empty(report.Clusters);
        // The featuring credits surface as split-credit candidates instead (when parts are known).
        Assert.Contains(report.CombinedCredits, c => c.Credit == "Kanye West feat. Kid Cudi");
    }

    [Fact]
    public async Task Detect_CombinedCredit_FoundEvenWhenPartsAlsoAppearAsCreditOnlySongs()
    {
        // "Kanye West" here exists ONLY as a display credit (no discrete Artists) — it must still
        // count as a standalone part for the combined-credit check.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "JAY-Z & Kanye West"),
            Song("/a/2.mp3", artist: "JAY-Z"),
            Song("/a/3.mp3", artist: "Kanye West"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        var candidate = Assert.Single(report.CombinedCredits, c => c.Credit == "JAY-Z & Kanye West");
        Assert.Equal(["JAY-Z", "Kanye West"], candidate.Parts);
    }

    [Fact]
    public async Task Detect_NeverClustersACollabAlbumArtistWithItsLead_OffersASplitInstead()
    {
        // The live shape: MusicBrainz credits "Hef met Jayh", the mapper kept the joined text as the
        // album artist but gave it the LEAD's id, so the id "corroborated" a spelling merge that
        // would delete Jayh. The row's own discrete list is what says it is two artists.
        await using var db = NewContext();
        db.Songs.AddRange(HefShape());
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        Assert.Empty(report.Clusters);
        var credit = Assert.Single(report.CombinedCredits);
        Assert.Equal("Hef met Jayh", credit.Credit);
        Assert.Equal(["Hef", "Jayh"], credit.Parts);
        Assert.Equal(1, credit.SongCount);
        Assert.Equal(1, credit.AlbumArtistSongCount);
    }

    [Fact]
    public async Task Detect_ProviderJoinPhrases_AreCombinedCreditsNotSpellings()
    {
        // "+" and "and" are MusicBrainz join phrases here, not part of a name: the discrete lists
        // prove it without the detector knowing any language.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Anuel AA", albumArtist: "Anuel AA", artists: "Anuel AA",
                artistMbids: "mbid-anuel", albumArtistMbid: "mbid-anuel"),
            Song("/a/2.mp3", artist: "Anuel AA", albumArtist: "Anuel AA", artists: "Anuel AA",
                artistMbids: "mbid-anuel", albumArtistMbid: "mbid-anuel"),
            Song("/a/3.mp3", artist: "Anuel AA + Bad Bunny", albumArtist: "Anuel AA + Bad Bunny",
                artists: "Anuel AA; Bad Bunny", artistMbids: "mbid-anuel; mbid-bunny", albumArtistMbid: "mbid-anuel"),
            Song("/a/4.mp3", artist: "Anuel AA + Farruko", albumArtist: "Anuel AA + Farruko",
                artists: "Anuel AA; Farruko", artistMbids: "mbid-anuel; mbid-farruko", albumArtistMbid: "mbid-anuel"),
            Song("/a/5.mp3", artist: "50 Cent", albumArtist: "50 Cent", artists: "50 Cent",
                artistMbids: "mbid-50", albumArtistMbid: "mbid-50"),
            Song("/a/6.mp3", artist: "50 Cent and Olivia", albumArtist: "50 Cent and Olivia",
                artists: "50 Cent; Olivia", artistMbids: "mbid-50; mbid-olivia", albumArtistMbid: "mbid-50"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        Assert.Empty(report.Clusters);
        Assert.Equal(
            ["50 Cent and Olivia", "Anuel AA + Bad Bunny", "Anuel AA + Farruko"],
            report.CombinedCredits.Select(c => c.Credit).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task Detect_BracketedFeaturingCredit_IsACombinedCreditNotASpelling()
    {
        // The search key strips "(featuring AZ)", so the credit keyed as plain "nas" and clustered
        // as "same name after normalization" — a merge that deleted AZ.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Nas", artists: "Nas"),
            Song("/a/2.mp3", artist: "Nas (featuring AZ)"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        Assert.Empty(report.Clusters);
        var credit = Assert.Single(report.CombinedCredits);
        Assert.Equal("Nas (featuring AZ)", credit.Credit);
        Assert.Equal(["Nas", "AZ"], credit.Parts);
    }

    [Fact]
    public async Task Detect_TagOnlyAmbiguousJoin_WithStandaloneParts_IsACombinedCredit()
    {
        // No discrete list to go on: "+" names two artists here because both exist on their own.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "2Pac + Outlawz"),
            Song("/a/2.mp3", artist: "2Pac", artists: "2Pac"),
            Song("/a/3.mp3", artist: "2Pac", artists: "2Pac"),
            Song("/a/4.mp3", artist: "Outlawz", artists: "Outlawz"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        Assert.DoesNotContain(report.Clusters, c => c.Variants.Any(v => v.Name == "2Pac + Outlawz"));
        var credit = Assert.Single(report.CombinedCredits);
        Assert.Equal("2Pac + Outlawz", credit.Credit);
        Assert.Equal(["2Pac", "Outlawz"], credit.Parts);
        Assert.Equal(1, credit.SongCount);
    }

    [Fact]
    public async Task Detect_AmbiguousJoinWithoutStandaloneParts_StaysOneArtistsName()
    {
        // "Florence + the Machine" is one band: nothing in the library says "Florence" and "the
        // Machine" are artists of their own, so it keeps clustering as a spelling and is never
        // offered as a split.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Florence + the Machine", artists: "Florence + the Machine"),
            Song("/a/2.mp3", artist: "Florence + The Machine", artists: "Florence + The Machine"),
            Song("/a/3.mp3", artist: "Florence + the Machine", albumArtist: "Florence + the Machine"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        var cluster = Assert.Single(report.Clusters);
        Assert.Equal(
            ["Florence + The Machine", "Florence + the Machine"],
            cluster.Variants.Select(v => v.Name).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(report.CombinedCredits);
    }

    [Fact]
    public async Task Detect_StillClustersPlainSpellings_AlongsideTheirCollabs()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "OutKast", artists: "OutKast", artistMbids: "mbid-outkast"),
            Song("/a/2.mp3", artist: "Outkast", artists: "Outkast", artistMbids: "mbid-outkast"),
            Song("/a/3.mp3", artist: "Pusha T", artists: "Pusha T"),
            Song("/a/4.mp3", artist: "PUSHA T", artists: "PUSHA T"),
            Song("/a/5.mp3", artist: "Boef", artists: "Boef"),
            Song("/a/6.mp3", artist: "BOEF", artists: "BOEF"),
            Song("/a/7.mp3", artist: "Boef met Ronnie Flex", albumArtist: "Boef met Ronnie Flex",
                artists: "Boef; Ronnie Flex"));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        Assert.Equal(
            ["BOEF|Boef", "OutKast|Outkast", "PUSHA T|Pusha T"],
            report.Clusters
                .Select(c => string.Join('|', c.Variants.Select(v => v.Name).Order(StringComparer.Ordinal)))
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Contains(report.CombinedCredits, c => c.Credit == "Boef met Ronnie Flex");
    }

    [Fact]
    public async Task Merge_NeverRewritesMultiPartDisplayCredit()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/a/1.mp3", artist: "JAYZ feat. Pharrell", artists: "JAYZ; Pharrell"));
        await db.SaveChangesAsync();

        await Service(db).MergeAsync(Owner, "JAY-Z", ["JAYZ"]);

        var song = await db.Songs.SingleAsync();
        // The display credit keeps its featuring clause; only the discrete list segment maps.
        Assert.Equal("JAYZ feat. Pharrell", song.Artist);
        Assert.Equal("JAY-Z; Pharrell", song.Artists);
    }

    [Fact]
    public async Task Detect_IgnoresOtherOwnersSongs()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "JAY-Z"),
            Song("/b/1.mp3", artist: "JAYZ", owner: Guid.NewGuid()));
        await db.SaveChangesAsync();

        var report = await Service(db).DetectAsync(Owner);

        Assert.Empty(report.Clusters);
    }

    // --- Merge ---

    [Fact]
    public async Task Merge_RewritesAllArtistFields_AuditsAndRequeues()
    {
        await using var db = NewContext();
        var built = Song("/a/1.mp3", artist: "JAYZ", albumArtist: "JAYZ", artists: "JAYZ",
            buildStatus: LibraryBuildStatus.Done, destinationPath: "/dest/JAYZ/x.mp3");
        var pending = Song("/a/2.mp3", artist: "Jaÿ-z", albumArtist: "Jaÿ-z");
        var untouched = Song("/a/3.mp3", artist: "Kanye West", albumArtist: "Kanye West");
        db.Songs.AddRange(built, pending, untouched);
        await db.SaveChangesAsync();

        var result = await Service(db).MergeAsync(Owner, "JAY-Z", ["JAYZ", "Jaÿ-z"]);

        Assert.Equal(2, result.SongsUpdated);
        Assert.Equal(1, result.SongsRequeued);

        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal("JAY-Z", songs[0].Artist);
        Assert.Equal("JAY-Z", songs[0].AlbumArtist);
        Assert.Equal("JAY-Z", songs[0].Artists);
        Assert.Equal("JAY-Z", songs[1].Artist);
        Assert.Equal("Kanye West", songs[2].Artist);

        // The built row was re-queued for an in-place re-tag.
        Assert.NotEqual(LibraryBuildStatus.Done, songs[0].LibraryBuildStatus);
        Assert.NotNull(songs[0].PreviousDestinationPath);

        var changes = await db.SongMetadataChanges.Where(c => c.Source == "artist-merge").ToListAsync();
        Assert.NotEmpty(changes);
        Assert.All(changes, c => Assert.NotNull(c.AppliedAtUtc));

        // Aliases persisted for every variant key AND the canonical's own key.
        var aliases = await db.ArtistAliases.ToListAsync();
        Assert.Single(aliases); // "jayz" == key of JAYZ, Jaÿ-z AND JAY-Z — one shared key
        Assert.Equal("jayz", aliases[0].AliasKey);
        Assert.Equal("JAY-Z", aliases[0].CanonicalName);
    }

    [Fact]
    public async Task Merge_RewritesMbidsToTheCanonicals_SoTheClusterCannotRegenerate()
    {
        // Leaving the variants' ids under the canonical spelling is what made the last merge
        // self-perpetuating: the canonical then carried every variant's id, and the next detect run
        // proposed the very same merge again.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Marvin Gaye", albumArtist: "Marvin Gaye", artists: "Marvin Gaye",
                artistMbids: "mbid-marvin", albumArtistMbid: "mbid-marvin"),
            Song("/a/2.mp3", artist: "Marvin Gay", albumArtist: "Marvin Gay", artists: "Marvin Gay; Tammi Terrell",
                artistMbids: "mbid-lijpe; mbid-tammi", albumArtistMbid: "mbid-lijpe"));
        await db.SaveChangesAsync();

        await Service(db).MergeAsync(Owner, "Marvin Gaye", ["Marvin Gay"]);

        var song = await db.Songs.OrderBy(s => s.Id).Skip(1).FirstAsync();
        Assert.Equal("Marvin Gaye", song.AlbumArtist);
        Assert.Equal("mbid-marvin", song.AlbumArtistMusicBrainzId);
        Assert.Equal("Marvin Gaye; Tammi Terrell", song.Artists);
        Assert.Equal("mbid-marvin; mbid-tammi", song.ArtistMusicBrainzIds); // the guest keeps its own

        // Both id rewrites are audited, so the revert restores them.
        var changes = await db.SongMetadataChanges.Where(c => c.Source == "artist-merge").ToListAsync();
        Assert.Contains(changes, c => c.FieldName == nameof(SongMetadata.AlbumArtistMusicBrainzId) && c.OldValue == "mbid-lijpe");
        Assert.Contains(changes, c => c.FieldName == nameof(SongMetadata.ArtistMusicBrainzIds));
    }

    [Fact]
    public async Task Merge_KeepsTheRowsOwnMbid_WhenTheCanonicalHasNone()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/a/1.mp3", artist: "JAYZ", albumArtist: "JAYZ", artists: "JAYZ",
            artistMbids: "mbid-jay", albumArtistMbid: "mbid-jay"));
        await db.SaveChangesAsync();

        await Service(db).MergeAsync(Owner, "JAY-Z", ["JAYZ"]);

        var song = await db.Songs.SingleAsync();
        Assert.Equal("mbid-jay", song.AlbumArtistMusicBrainzId);
        Assert.Equal("mbid-jay", song.ArtistMusicBrainzIds);
    }

    [Fact]
    public async Task Merge_RejectsPlaceholderCanonical_AndNeverAliasesAPlaceholderVariant()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/a/1.mp3", artist: "Various Artists", albumArtist: "Various Artists"));
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(
            () => Service(db).MergeAsync(Owner, "Various Artists", ["VA"]));

        // A placeholder listed as a variant is dropped, not rewritten: aliasing "various artists"
        // onto a real name would have the identity heal rename every compilation to that artist.
        var result = await Service(db).MergeAsync(Owner, "Marvin Gaye", ["Various Artists"]);

        Assert.Equal(0, result.SongsUpdated);
        var song = await db.Songs.SingleAsync();
        Assert.Equal("Various Artists", song.AlbumArtist);
        Assert.DoesNotContain(await db.ArtistAliases.ToListAsync(), a => a.AliasKey == "various artists");
    }

    [Fact]
    public async Task Merge_IsIdempotent()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/a/1.mp3", artist: "JAYZ"));
        await db.SaveChangesAsync();

        var first = await Service(db).MergeAsync(Owner, "JAY-Z", ["JAYZ"]);
        Assert.Equal(1, first.SongsUpdated);

        var second = await Service(db).MergeAsync(Owner, "JAY-Z", ["JAYZ"]);
        Assert.Equal(0, second.SongsUpdated);
        Assert.Equal(0, second.AliasesStored);
    }

    [Fact]
    public async Task Merge_CollapsesArtistsListSegments_KeepsMbidAlignment()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/a/1.mp3", artist: "JAY-Z",
            artists: "JAYZ; Kanye West; JAY-Z",
            artistMbids: "mbid-jay; mbid-kanye; mbid-jay"));
        await db.SaveChangesAsync();

        await Service(db).MergeAsync(Owner, "JAY-Z", ["JAYZ"]);

        var song = await db.Songs.SingleAsync();
        Assert.Equal("JAY-Z; Kanye West", song.Artists);
        Assert.Equal("mbid-jay; mbid-kanye", song.ArtistMusicBrainzIds);
    }

    [Fact]
    public async Task Merge_NeverTouchesOtherOwnersOrDemoRows()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "JAYZ"),
            Song("/b/1.mp3", artist: "JAYZ", owner: Guid.NewGuid()),
            Song("/demo/1.mp3", artist: "JAYZ", owner: WellKnownUsers.DemoId));
        await db.SaveChangesAsync();

        var result = await Service(db).MergeAsync(Owner, "JAY-Z", ["JAYZ"]);

        Assert.Equal(1, result.SongsUpdated);
        var others = await db.Songs.IgnoreQueryFilters()
            .Where(s => s.OwnerUserId != Owner)
            .ToListAsync();
        Assert.All(others, s => Assert.Equal("JAYZ", s.Artist));
    }

    [Fact]
    public async Task Merge_NeverRewritesAnUnselectedFeaturingCredit()
    {
        // Every merge adds the canonical's own key. Under the search key that was "nas" — which
        // "Nas (featuring AZ)" also keyed as — so merging NAS → Nas rewrote a row nobody selected
        // and deleted AZ.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "NAS", albumArtist: "NAS", artists: "NAS"),
            Song("/a/2.mp3", artist: "Nas", albumArtist: "Nas", artists: "Nas"),
            Song("/a/3.mp3", artist: "Nas (featuring AZ)"),
            Song("/a/4.mp3", artist: "Nas", albumArtist: "Nas (featuring AZ)", artists: "Nas; AZ"),
            // A list that registered the whole credit as one artist: segments were never guarded.
            Song("/a/5.mp3", artist: "Nas (featuring AZ)", artists: "Nas (featuring AZ)"));
        await db.SaveChangesAsync();

        var result = await Service(db).MergeAsync(Owner, "Nas", ["NAS"]);

        Assert.Equal(1, result.SongsUpdated);
        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal("Nas", songs[0].Artist);
        Assert.Equal("Nas (featuring AZ)", songs[2].Artist);
        Assert.Null(songs[2].Artists);
        Assert.Equal("Nas (featuring AZ)", songs[3].AlbumArtist);
        Assert.Equal("Nas; AZ", songs[3].Artists);
        Assert.Equal("Nas (featuring AZ)", songs[4].Artists);
        Assert.Equal(["nas"], (await db.ArtistAliases.ToListAsync()).Select(a => a.AliasKey).ToArray());
    }

    [Fact]
    public async Task Merge_NeverRewritesOrAliasesACollabVariant_EvenWhenPassedExplicitly()
    {
        await using var db = NewContext();
        db.Songs.AddRange(HefShape());
        db.Songs.Add(Song("/a/5.mp3", artist: "HEF", albumArtist: "HEF", artists: "HEF"));
        await db.SaveChangesAsync();

        var result = await Service(db).MergeAsync(Owner, "Hef", ["Hef met Jayh", "HEF"]);

        Assert.Equal(1, result.SongsUpdated); // only the HEF row
        var collab = await db.Songs.SingleAsync(s => s.SourcePath == "/a/4.mp3");
        Assert.Equal("Hef met Jayh", collab.Artist);
        Assert.Equal("Hef met Jayh", collab.AlbumArtist);
        Assert.Equal("Hef;Jayh", collab.Artists);
        Assert.Equal("Hef", (await db.Songs.SingleAsync(s => s.SourcePath == "/a/5.mp3")).Artist);
        // No alias for the collab either: it would strip Jayh on every re-enrichment.
        Assert.Equal(["hef"], (await db.ArtistAliases.ToListAsync()).Select(a => a.AliasKey).ToArray());
    }

    [Fact]
    public async Task Merge_IgnoresAVariantThatJoinsTheCanonicalAsAPart_WithoutAnyEvidence()
    {
        // Tag-only, and "Major" exists nowhere else: the library can't prove the collab, but a name
        // that is the canonical plus " met X" is never a spelling of it.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Hef", albumArtist: "Hef", artists: "Hef"),
            Song("/a/2.mp3", artist: "Hef met Major", albumArtist: "Hef met Major"));
        await db.SaveChangesAsync();

        var result = await Service(db).MergeAsync(Owner, "Hef", ["Hef met Major"]);

        Assert.Equal(0, result.SongsUpdated);
        Assert.Equal("Hef met Major", (await db.Songs.SingleAsync(s => s.SourcePath == "/a/2.mp3")).AlbumArtist);
        Assert.DoesNotContain(await db.ArtistAliases.ToListAsync(), a => a.AliasKey == "hef met major");
    }

    [Fact]
    public async Task Merge_RefusesACombinedCreditAsTheCanonical()
    {
        // Bug 3 backwards: merging the lead onto a collab would rewrite every plain "Hef" row.
        await using var db = NewContext();
        db.Songs.AddRange(HefShape());
        await db.SaveChangesAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).MergeAsync(Owner, "Hef met Jayh", ["Hef"]));
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).MergeAsync(Owner, "Hef feat. Jayh", ["Hef"]));

        Assert.All(await db.Songs.Where(s => s.SourcePath != "/a/4.mp3").ToListAsync(), s => Assert.Equal("Hef", s.Artist));
        Assert.Empty(await db.ArtistAliases.ToListAsync());
    }

    [Fact]
    public async Task Merge_UpdatesALegacySearchKeyedAliasInPlace()
    {
        // Rows from before the artist key were keyed by the search form — the same string for a
        // plain name — so a new merge must find and update that row, never add a second one under
        // the unique (OwnerUserId, AliasKey) index.
        await using var db = NewContext();
        db.Songs.Add(Song("/a/1.mp3", artist: "JAYZ"));
        db.ArtistAliases.Add(new ArtistAlias
        {
            OwnerUserId = Owner, AliasKey = "jayz", CanonicalName = "Jay Z", CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
        });
        await db.SaveChangesAsync();

        var result = await Service(db).MergeAsync(Owner, "JAY-Z", ["JAYZ"]);

        Assert.Equal(1, result.AliasesStored);
        var alias = Assert.Single(await db.ArtistAliases.ToListAsync());
        Assert.Equal("jayz", alias.AliasKey);
        Assert.Equal("JAY-Z", alias.CanonicalName);
    }

    // --- Split credit ---

    [Fact]
    public async Task SplitCredit_BackfillsDiscreteArtists()
    {
        await using var db = NewContext();
        var combined = Song("/a/1.mp3", artist: "JAY-Z & Kanye West",
            buildStatus: LibraryBuildStatus.Done, destinationPath: "/dest/x.mp3");
        var alreadyDiscrete = Song("/a/2.mp3", artist: "JAY-Z & Kanye West", artists: "JAY-Z; Kanye West");
        db.Songs.AddRange(combined, alreadyDiscrete);
        await db.SaveChangesAsync();

        var result = await Service(db).SplitCreditAsync(Owner, "JAY-Z & Kanye West");

        Assert.Equal(1, result.SongsUpdated);
        Assert.Equal(1, result.SongsRequeued);
        var song = await db.Songs.OrderBy(s => s.Id).FirstAsync();
        Assert.Equal("JAY-Z; Kanye West", song.Artists);
        Assert.Equal("JAY-Z & Kanye West", song.Artist); // display credit is preserved

        var change = Assert.Single(await db.SongMetadataChanges.ToListAsync());
        Assert.Equal("artist-credit-split", change.Source);
    }

    [Fact]
    public async Task SplitCredit_RejectsSingleArtistName()
    {
        await using var db = NewContext();
        await Assert.ThrowsAsync<ArgumentException>(
            () => Service(db).SplitCreditAsync(Owner, "Kanye West"));
    }

    [Fact]
    public async Task SplitCredit_FeatCredit_LeavesTheLeadsSoloRows_AndNeverWritesTheDelimiter()
    {
        // Rows matched by search key, and a feat credit search-keys as its lead: splitting it wrote
        // "Kanye West; feat.; Kid Cudi" (a capturing split) onto every blank-list "Kanye West" row.
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "Kanye West feat. Kid Cudi"),
            Song("/a/2.mp3", artist: "Kanye West"),
            Song("/a/3.mp3", artist: "Kanye West", artists: "Kanye West"));
        await db.SaveChangesAsync();

        var result = await Service(db).SplitCreditAsync(Owner, "Kanye West feat. Kid Cudi");

        Assert.Equal(1, result.SongsUpdated);
        var songs = await db.Songs.OrderBy(s => s.Id).ToListAsync();
        Assert.Equal(["Kanye West", "Kid Cudi"], MultiValue.Split(songs[0].Artists));
        Assert.Null(songs[1].Artists);
        Assert.Equal("Kanye West", songs[2].Artists);
    }

    [Fact]
    public async Task SplitCredit_MovesACollabAlbumArtistToTheLead_AndTheRevertRestoresIt()
    {
        await using var db = NewContext();
        db.Songs.AddRange(HefShape(collabBuilt: true));
        await db.SaveChangesAsync();

        var result = await Service(db).SplitCreditAsync(Owner, "Hef met Jayh");

        Assert.Equal(1, result.SongsUpdated);
        Assert.Equal(1, result.SongsRequeued);
        var song = await db.Songs.SingleAsync(s => s.SourcePath == "/a/4.mp3");
        Assert.Equal("Hef", song.AlbumArtist);
        Assert.Equal("mbid-hef", song.AlbumArtistMusicBrainzId);
        Assert.Equal("Hef met Jayh", song.Artist);  // the display credit is right as it is
        Assert.Equal("Hef;Jayh", song.Artists);     // already discrete: never rewritten
        Assert.Equal("Hef met Jayh", song.OriginalAlbumArtist);
        // Re-queued so the builder re-tags it and moves it out of "Hef met Jayh/".
        Assert.NotEqual(LibraryBuildStatus.Done, song.LibraryBuildStatus);
        Assert.Equal("/dest/Hef met Jayh/Album/04.mp3", song.PreviousDestinationPath);
        var change = Assert.Single(await db.SongMetadataChanges.ToListAsync());
        Assert.Equal(("artist-credit-split", nameof(SongMetadata.AlbumArtist), "Hef met Jayh", "Hef"),
            (change.Source, change.FieldName, change.OldValue, change.NewValue));

        // Split once: nothing left to offer, and a second split changes nothing.
        Assert.Empty((await Service(db).DetectAsync(Owner)).CombinedCredits);
        Assert.Equal(0, (await Service(db).SplitCreditAsync(Owner, "Hef met Jayh")).SongsUpdated);

        var revert = await new DedupActionHistoryService(db, NullLogger<DedupActionHistoryService>.Instance)
            .RevertAsync(Owner, "artist-credit-split", change.CreatedAtUtc.Ticks);

        Assert.Equal(1, revert.ChangesReverted);
        Assert.Equal("Hef met Jayh", (await db.Songs.SingleAsync(s => s.SourcePath == "/a/4.mp3")).AlbumArtist);
    }

    [Fact]
    public async Task SplitCredit_AlbumArtistTakesTheLeadsId_NeverAGuests()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            // Aligned ids: the lead's id is the list's first.
            Song("/a/1.mp3", artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
                artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: "mbid-jayh"),
            // Tag-only: no list, so an id aligned elsewhere with the guest (and never the lead) goes.
            Song("/a/2.mp3", artist: "2Pac + Outlawz", albumArtist: "2Pac + Outlawz", albumArtistMbid: "mbid-outlawz"),
            Song("/a/3.mp3", artist: "2Pac", artists: "2Pac", artistMbids: "mbid-2pac"),
            Song("/a/4.mp3", artist: "Outlawz", artists: "Outlawz", artistMbids: "mbid-outlawz"));
        await db.SaveChangesAsync();

        await Service(db).SplitCreditAsync(Owner, "Hef met Jayh");
        await Service(db).SplitCreditAsync(Owner, "2Pac + Outlawz");

        var hef = await db.Songs.SingleAsync(s => s.SourcePath == "/a/1.mp3");
        Assert.Equal(("Hef", "mbid-hef"), (hef.AlbumArtist, hef.AlbumArtistMusicBrainzId));
        var pac = await db.Songs.SingleAsync(s => s.SourcePath == "/a/2.mp3");
        Assert.Equal(("2Pac", null), (pac.AlbumArtist, pac.AlbumArtistMusicBrainzId));
        Assert.Equal("2Pac; Outlawz", pac.Artists);

        // Every field is audited, so the revert restores the ids too.
        var stamp = (await db.SongMetadataChanges.FirstAsync(c => c.SongId == pac.Id)).CreatedAtUtc;
        await new DedupActionHistoryService(db, NullLogger<DedupActionHistoryService>.Instance)
            .RevertAsync(Owner, "artist-credit-split", stamp.Ticks);
        pac = await db.Songs.SingleAsync(s => s.SourcePath == "/a/2.mp3");
        Assert.Equal(("2Pac + Outlawz", "mbid-outlawz", null), (pac.AlbumArtist, pac.AlbumArtistMusicBrainzId, pac.Artists));
    }

    [Fact]
    public async Task SplitCredit_TagOnlyAmbiguousJoin_NeedsEveryPartToExistStandalone()
    {
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.mp3", artist: "2Pac + Outlawz"),
            Song("/a/2.mp3", artist: "2Pac", artists: "2Pac"));
        await db.SaveChangesAsync();

        // "Outlawz" is nowhere on its own yet: this could be one artist's name.
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).SplitCreditAsync(Owner, "2Pac + Outlawz"));

        db.Songs.Add(Song("/a/3.mp3", artist: "Outlawz", artists: "Outlawz"));
        await db.SaveChangesAsync();
        var result = await Service(db).SplitCreditAsync(Owner, "2Pac + Outlawz");

        Assert.Equal(1, result.SongsUpdated);
        Assert.Equal("2Pac; Outlawz", (await db.Songs.SingleAsync(s => s.SourcePath == "/a/1.mp3")).Artists);
    }

    [Fact]
    public async Task SplitCredit_UsesTheRowsOwnList_ForAJoinPhraseItDoesNotKnow()
    {
        // Spanish "y" is no joiner the splitter knows; the row's discrete list proves the join.
        await using var db = NewContext();
        db.Songs.Add(Song("/a/1.mp3", artist: "Anuel AA y Ozuna", albumArtist: "Anuel AA y Ozuna",
            artists: "Anuel AA; Ozuna", artistMbids: "mbid-anuel; mbid-ozuna", albumArtistMbid: "mbid-anuel"));
        await db.SaveChangesAsync();

        var result = await Service(db).SplitCreditAsync(Owner, "Anuel AA y Ozuna");

        Assert.Equal(1, result.SongsUpdated);
        Assert.Equal("Anuel AA", (await db.Songs.SingleAsync()).AlbumArtist);
    }

    // --- Oscillation regression: heal must not undo a merge ---

    [Fact]
    public async Task AlbumSplitHealer_RespectsArtistAlias_MergeSurvivesRepeatedHeals()
    {
        await using var db = NewContext();
        // The canonical album pipeline says "Ms. Lauryn Hill", but the user merged onto
        // "Lauryn Hill". Without the alias mapping, every idle heal would rewrite AlbumArtist back
        // to the canonical spelling — silently un-doing the merge, forever.
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "lauryn hill",
            AlbumKey = "the miseducation of lauryn hill",
            DisplayTitle = "The Miseducation of Lauryn Hill",
            DisplayArtist = "Ms. Lauryn Hill",
            Year = 1998,
            Status = CanonicalAlbumStatus.Fetched,
        });
        db.Songs.AddRange(
            AlbumSong("/a/01.mp3", "Lost Ones", 1),
            AlbumSong("/a/02.mp3", "Ex-Factor", 2));
        await db.SaveChangesAsync();

        await Service(db).MergeAsync(Owner, "Lauryn Hill", ["Ms. Lauryn Hill"]);

        var healer = Healer(db);
        var first = await healer.HealAsync();
        var second = await healer.HealAsync();

        Assert.Equal(0, first.SongsCorrected);
        Assert.Equal(0, second.SongsCorrected);
        var songs = await db.Songs.ToListAsync();
        Assert.All(songs, s => Assert.Equal("Lauryn Hill", s.AlbumArtist));
    }

    private static SongMetadata AlbumSong(string path, string title, int track) => new()
    {
        OwnerUserId = Owner,
        SourcePath = path,
        FileName = Path.GetFileName(path),
        Extension = Path.GetExtension(path),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        OriginalMetadataCaptured = true,
        Artist = "Lauryn Hill",
        AlbumArtist = "Lauryn Hill",
        Album = "The Miseducation of Lauryn Hill",
        Title = title,
        TrackNumber = track,
        Year = 1998,
    };

    private static IAlbumSplitHealer Healer(MusicHoarderDbContext db) => new AlbumSplitHealer(
        db,
        new AlbumIdentityReconciler(),
        new DestinationPathResolver(Microsoft.Extensions.Options.Options.Create(
            new MusicEnricherOptions { SourceDirectory = "/source", DestinationDirectory = "/dest" })),
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
        {
            SourceDirectory = "/source",
            DestinationDirectory = "/dest",
            EnableCanonicalDrivenBuild = true,
        }),
        NullLogger<AlbumSplitHealer>.Instance);

    // The live "Hef met Jayh" shape: three plain Hef rows, and one MusicBrainz-credited collab whose
    // album artist kept the joined credit text while carrying the LEAD's id.
    private static SongMetadata[] HefShape(bool collabBuilt = false) =>
    [
        Song("/a/1.mp3", artist: "Hef", albumArtist: "Hef", artists: "Hef", artistMbids: "mbid-hef", albumArtistMbid: "mbid-hef"),
        Song("/a/2.mp3", artist: "Hef", albumArtist: "Hef", artists: "Hef", artistMbids: "mbid-hef", albumArtistMbid: "mbid-hef"),
        Song("/a/3.mp3", artist: "Hef", albumArtist: "Hef", artists: "Hef", artistMbids: "mbid-hef", albumArtistMbid: "mbid-hef"),
        Song("/a/4.mp3", artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef;Jayh",
            artistMbids: "mbid-hef;mbid-jayh", albumArtistMbid: "mbid-hef",
            buildStatus: collabBuilt ? LibraryBuildStatus.Done : LibraryBuildStatus.Pending,
            destinationPath: collabBuilt ? "/dest/Hef met Jayh/Album/04.mp3" : null),
    ];

    private static ArtistDuplicateService Service(MusicHoarderDbContext db) => new(
        db,
        Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions()),
        NullLogger<ArtistDuplicateService>.Instance);

    private static MusicHoarderDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new MusicHoarderDbContext(options);
    }

    private static SongMetadata Song(
        string sourcePath,
        string artist,
        string? albumArtist = null,
        string? artists = null,
        string? artistMbids = null,
        string? albumArtistMbid = null,
        Guid? owner = null,
        LibraryBuildStatus buildStatus = LibraryBuildStatus.Pending,
        string? destinationPath = null) => new()
    {
        OwnerUserId = owner ?? Owner,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        AlbumArtist = albumArtist,
        Artists = artists,
        ArtistMusicBrainzIds = artistMbids,
        AlbumArtistMusicBrainzId = albumArtistMbid,
        Title = Path.GetFileNameWithoutExtension(sourcePath),
        LibraryBuildStatus = buildStatus,
        DestinationPath = destinationPath,
    };
}

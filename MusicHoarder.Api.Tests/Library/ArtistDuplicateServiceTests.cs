using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
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

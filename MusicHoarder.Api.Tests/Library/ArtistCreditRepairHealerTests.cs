using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The repair for rows the first artist-dedup release damaged. Every damaged row here is seeded as the
/// buggy code left it — values plus the audit rows it wrote — never produced by running that code,
/// which is being fixed alongside and would stop producing the damage.
/// </summary>
public class ArtistCreditRepairHealerTests
{
    private static readonly Guid Owner = WellKnownUsers.OwnerId;
    private static readonly DateTime EnrichedAt = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime AliasAt = new(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ReEnrichedAt = new(2026, 2, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ActionAt = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    // --- (a) album artist ---

    [Fact]
    public async Task HealAsync_CollabAlbumWithAGuestOnOneTrack_MovesEveryTrackTogether()
    {
        // "2Pac + Outlawz feat. Big Syke" was mapped to album artist "2Pac + Outlawz" like its
        // siblings, but its discrete list carries the guest. Judging it against the whole list would
        // leave that one track behind in the collab's folder while the rest move to "2Pac".
        await using var db = NewContext();
        db.Songs.AddRange(
            Song("/a/1.flac", artist: "2Pac + Outlawz", albumArtist: "2Pac + Outlawz", artists: "2Pac; Outlawz",
                artistMbids: "mbid-2pac; mbid-outlawz", albumArtistMbid: "mbid-2pac"),
            Song("/a/9.flac", artist: "2Pac + Outlawz feat. Big Syke", albumArtist: "2Pac + Outlawz",
                artists: "2Pac; Outlawz; Big Syke", artistMbids: "mbid-2pac; mbid-outlawz; mbid-syke",
                albumArtistMbid: "mbid-2pac"));
        await db.SaveChangesAsync();

        await Healer(db).HealAsync();

        Assert.All(await db.Songs.ToListAsync(), s => Assert.Equal("2Pac", s.AlbumArtist));
    }

    [Theory]
    [InlineData("Dance With the Dead")]
    [InlineData(null)]
    public async Task HealAsync_BareWithInsideABandName_Untouched(string? artists)
    {
        // " with " is a featuring joiner to SplitArtists, but also a word inside real names; an
        // unattended cut would move the whole album into a "Dance" folder.
        await using var db = NewContext();
        db.Songs.Add(Song(artist: "Dance With the Dead", albumArtist: "Dance With the Dead", artists: artists));
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal("Dance With the Dead", (await db.Songs.SingleAsync()).AlbumArtist);
    }

    [Fact]
    public async Task HealAsync_FeaturingNameTheRowsOwnListCallsOneArtist_Untouched()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(artist: "Feat Ft. Friends", albumArtist: "Feat Ft. Friends", artists: "Feat Ft. Friends"));
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
    }

    [Fact]
    public async Task HealAsync_UserUndidASplitsAlbumArtistMove_Untouched()
    {
        // Split moved the album artist to the lead and the user tapped Undo: the restored collab is
        // their decision, and the repair must not silently re-apply what they just undid.
        await using var db = NewContext();
        var song = Song(artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
            artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: "mbid-hef");
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.AlbumArtist), "artist-credit-split",
            "Hef met Jayh", "Hef", ActionAt, reverted: true);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal("Hef met Jayh", (await db.Songs.SingleAsync()).AlbumArtist);
    }

    [Theory]
    [InlineData("Hef met Jayh", "Hef; Jayh", "Hef")]
    [InlineData("2Pac + Outlawz", "2Pac; Outlawz", "2Pac")]
    [InlineData("50 Cent and Olivia", "50 Cent; Olivia", "50 Cent")]
    public async Task HealAsync_CollabAlbumArtistCarryingLeadId_BecomesLead(string credit, string artists, string lead)
    {
        await using var db = NewContext();
        var song = Song(artist: credit, albumArtist: credit, artists: artists,
            artistMbids: "mbid-lead; mbid-guest", albumArtistMbid: "mbid-lead");
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(new ArtistCreditRepairResult(1, 1, 1), result);
        var healed = await db.Songs.SingleAsync();
        Assert.Equal(lead, healed.AlbumArtist);
        Assert.Equal("mbid-lead", healed.AlbumArtistMusicBrainzId);
        // The display credit is right as it is; only the album-level field was wrong.
        Assert.Equal(credit, healed.Artist);

        // Re-queued with the force-rebuild signal (the file also has to leave the collab's folder),
        // and no grade staleness.
        Assert.Equal(LibraryBuildStatus.Pending, healed.LibraryBuildStatus);
        Assert.Equal(healed.DestinationPath, healed.PreviousDestinationPath);
        Assert.Equal(EnrichedAt, healed.EnrichedAtUtc);

        var change = await db.SongMetadataChanges.SingleAsync();
        Assert.Equal(ArtistCreditRepairHealer.AlbumArtistSource, change.Source);
        Assert.Equal((nameof(SongMetadata.AlbumArtist), credit, lead), (change.FieldName, change.OldValue, change.NewValue));
    }

    [Fact]
    public async Task HealAsync_CollabAlbumArtistWithoutId_TakesTheLeadsAlignedId()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
            artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: null));
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(2, result.FieldsRepaired);
        var healed = await db.Songs.SingleAsync();
        Assert.Equal("Hef", healed.AlbumArtist);
        Assert.Equal("mbid-hef", healed.AlbumArtistMusicBrainzId);
    }

    [Fact]
    public async Task HealAsync_CollabAlbumArtistCarryingAnotherId_IsSomebodysValue()
    {
        // The bug's fingerprint is the LEAD's id under the joined name. A group entity's own id, or
        // the release artist's, means someone chose that album artist on purpose.
        await using var db = NewContext();
        db.Songs.Add(Song(artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
            artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: "mbid-duo"));
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal("Hef met Jayh", (await db.Songs.SingleAsync()).AlbumArtist);
    }

    [Theory]
    // One artist whose name contains a joiner: its discrete list is the one name, so nothing says two.
    [InlineData("Simon & Garfunkel", "Simon & Garfunkel")]
    [InlineData("Florence + the Machine", "Florence + the Machine")]
    [InlineData("Earth, Wind & Fire", "Earth, Wind & Fire")]
    // A joiner SplitArtists already splits never came from the mapping bug — that album artist was
    // written by someone else (tags, a catalog, the canonical album) and stays theirs.
    [InlineData("Marvin Gaye & Tammi Terrell", "Marvin Gaye; Tammi Terrell")]
    public async Task HealAsync_AmbiguousJoinerAlbumArtist_Untouched(string albumArtist, string artists)
    {
        await using var db = NewContext();
        db.Songs.Add(Song(artist: albumArtist, albumArtist: albumArtist, artists: artists));
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal(albumArtist, (await db.Songs.SingleAsync()).AlbumArtist);
    }

    [Theory]
    [InlineData("Nas (featuring AZ)", "Nas")]
    [InlineData("Nas [feat. AZ]", "Nas")]
    [InlineData("Kanye West feat. Kid Cudi", "Kanye West")]
    public async Task HealAsync_FeaturingAlbumArtist_BecomesLead(string albumArtist, string lead)
    {
        // The scanner's `albumArtist ??= GetPrimaryArtist(artist)` wrote the bracketed form whole before
        // brackets were understood. No discrete list needed — a guest is never an album artist.
        await using var db = NewContext();
        db.Songs.Add(Song(artist: albumArtist, albumArtist: albumArtist, artists: null));
        await db.SaveChangesAsync();

        Assert.Equal(1, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal(lead, (await db.Songs.SingleAsync()).AlbumArtist);
    }

    [Theory]
    [InlineData("album-identity-heal")]
    [InlineData("canonical-album")]
    [InlineData("artist-merge")]
    public async Task HealAsync_AlbumLevelWriterChoseTheCollab_Deferred(string source)
    {
        // The split heal's election, the canonical consolidation or a user's merge put the collab
        // spelling on this row: fighting it is the album-artist ping-pong.
        await using var db = NewContext();
        var song = Song(artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
            artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: "mbid-hef");
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.AlbumArtist), source, "Hef", "Hef met Jayh", ActionAt);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal("Hef met Jayh", (await db.Songs.SingleAsync()).AlbumArtist);
    }

    [Fact]
    public async Task HealAsync_LegacyAliasFoldingTheLeadIntoTheCollab_IsIgnored_AndTheLeadWins()
    {
        // "Hef" merged INTO "Hef met Jayh" before merges refused collab canonicals. The alias map
        // ignores an alias that swaps a name for a credit containing it (ArtistAliasMap.JoinsAsPart),
        // and the split heal applies aliases through that same map, so nothing maps the lead back:
        // the repair is safe, and would be the only thing that ever fixes the row.
        await using var db = NewContext();
        db.Songs.Add(Song(artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
            artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: "mbid-hef"));
        db.ArtistAliases.Add(new ArtistAlias
        {
            OwnerUserId = Owner, AliasKey = "hef", CanonicalName = "Hef met Jayh", CreatedAtUtc = AliasAt,
        });
        await db.SaveChangesAsync();

        Assert.Equal(1, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal("Hef", (await db.Songs.SingleAsync()).AlbumArtist);
    }

    [Fact]
    public async Task HealAsync_MergeAliasRespellsTheLead_WritesTheCanonicalSpelling()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
            artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: "mbid-hef"));
        db.ArtistAliases.Add(new ArtistAlias
        {
            OwnerUserId = Owner, AliasKey = "hef", CanonicalName = "HEF", CreatedAtUtc = AliasAt,
        });
        await db.SaveChangesAsync();

        await Healer(db).HealAsync();

        Assert.Equal("HEF", (await db.Songs.SingleAsync()).AlbumArtist);
    }

    [Fact]
    public async Task HealAsync_CanonicalAlbumNamesTheCollab_Untouched()
    {
        // The split heal overlays the canonical album's artist over the members' own spellings; when
        // the album the repaired row would land in says the collab, the repair would be overlaid back.
        await using var db = NewContext();
        db.Songs.Add(Song(artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
            artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: "mbid-hef"));
        db.CanonicalAlbums.Add(new CanonicalAlbum
        {
            ArtistKey = "hef", AlbumKey = "album", DisplayArtist = "Hef met Jayh",
            Status = CanonicalAlbumStatus.Fetched,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db, canonicalDrivenBuild: true).HealAsync()).SongsRepaired);
        // The overlay only runs with canonical-driven builds on, and so does the guard.
        Assert.Equal(1, (await Healer(db, canonicalDrivenBuild: false).HealAsync()).SongsRepaired);
    }

    [Fact]
    public async Task HealAsync_ManuallyApprovedRow_Untouched()
    {
        await using var db = NewContext();
        var song = Song(artist: "Nas (featuring AZ)", albumArtist: "Nas (featuring AZ)", artists: null);
        song.LockManualApproval();
        db.Songs.Add(song);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
    }

    [Fact]
    public async Task HealAsync_RevertedRepair_NeverReapplied()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
            artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: "mbid-hef"));
        await db.SaveChangesAsync();
        Assert.Equal(1, (await Healer(db).HealAsync()).SongsRepaired);

        // The user reverts the repair from the song's change history.
        var song = await db.Songs.SingleAsync();
        var repair = await db.SongMetadataChanges.SingleAsync();
        SongFieldReverter.Apply(song, repair.FieldName, repair.OldValue);
        repair.RevertedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal("Hef met Jayh", (await db.Songs.SingleAsync()).AlbumArtist);
    }

    // --- (b) artist merge ---

    [Theory]
    [InlineData("Nas (featuring AZ)", "Nas", "Nas; AZ")]
    [InlineData("Nas feat. AZ", "Nas", null)]
    [InlineData("Hef met Jayh", "Hef", "Hef; Jayh")]
    public async Task HealAsync_MergeCutAMultiArtistCreditToItsLead_Restored(string credit, string canonical, string? artists)
    {
        await using var db = NewContext();
        var song = Song(artist: canonical, albumArtist: canonical, artists: artists);
        db.Songs.Add(song);
        // The merge also wrote the lead into AlbumArtist — the correct album artist, which stays.
        AddChange(db, song, nameof(SongMetadata.Artist), "artist-merge", credit, canonical, ActionAt);
        AddChange(db, song, nameof(SongMetadata.AlbumArtist), "artist-merge", credit, canonical, ActionAt);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(new ArtistCreditRepairResult(1, 1, 1), result);
        var healed = await db.Songs.SingleAsync();
        Assert.Equal(credit, healed.Artist);
        Assert.Equal(canonical, healed.AlbumArtist);
        var repair = await db.SongMetadataChanges.SingleAsync(c => c.Source == ArtistCreditRepairHealer.MergeSource);
        Assert.Equal((nameof(SongMetadata.Artist), canonical, credit), (repair.FieldName, repair.OldValue, repair.NewValue));
    }

    [Fact]
    public async Task HealAsync_RevertedMerge_NotRepaired()
    {
        await using var db = NewContext();
        var song = Song(artist: "Nas", albumArtist: "Nas", artists: null);
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.Artist), "artist-merge", "Nas (featuring AZ)", "Nas", ActionAt,
            reverted: true);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal("Nas", (await db.Songs.SingleAsync()).Artist);
    }

    [Fact]
    public async Task HealAsync_RowEditedSinceTheMerge_Untouched()
    {
        await using var db = NewContext();
        var edited = Song("/a.flac", artist: "Nas & AZ", albumArtist: "Nas", artists: null);
        var reDecided = Song("/b.flac", artist: "Nas", albumArtist: "Nas", artists: null);
        db.Songs.AddRange(edited, reDecided);
        AddChange(db, edited, nameof(SongMetadata.Artist), "artist-merge", "Nas (featuring AZ)", "Nas", ActionAt);
        AddChange(db, reDecided, nameof(SongMetadata.Artist), "artist-merge", "Nas (featuring AZ)", "Nas", ActionAt);
        // A later enrichment wrote the field again — the merge is no longer its last word.
        AddChange(db, reDecided, nameof(SongMetadata.Artist), "MusicBrainzWeb", "Nas", "Nas", ActionAt.AddDays(1));
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
    }

    [Fact]
    public async Task HealAsync_ManualApprovalSinceTheMerge_VouchedForTheValue()
    {
        // A reviewer's edits leave no audit row, so an approval after the merge may have typed "Nas"
        // on purpose. One before it stopped nothing — the merge ignored the lock — and is restored.
        await using var db = NewContext();
        var approvedAfter = Song("/after.flac", artist: "Nas", albumArtist: "Nas", artists: null);
        approvedAfter.IsManuallyApproved = true;
        approvedAfter.ManuallyApprovedAtUtc = ActionAt.AddDays(1);
        var approvedBefore = Song("/before.flac", artist: "Nas", albumArtist: "Nas", artists: null);
        approvedBefore.IsManuallyApproved = true;
        approvedBefore.ManuallyApprovedAtUtc = ActionAt.AddDays(-1);
        db.Songs.AddRange(approvedAfter, approvedBefore);
        AddChange(db, approvedAfter, nameof(SongMetadata.Artist), "artist-merge", "Nas (featuring AZ)", "Nas", ActionAt);
        AddChange(db, approvedBefore, nameof(SongMetadata.Artist), "artist-merge", "Nas (featuring AZ)", "Nas", ActionAt);
        await db.SaveChangesAsync();

        Assert.Equal(1, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal("Nas", (await db.Songs.SingleAsync(s => s.SourcePath == "/after.flac")).Artist);
        Assert.Equal("Nas (featuring AZ)", (await db.Songs.SingleAsync(s => s.SourcePath == "/before.flac")).Artist);
    }

    [Theory]
    // A spelling fix between two forms of one name — never a guest to restore.
    [InlineData("Simon and Garfunkel", "Simon & Garfunkel")]
    [InlineData("Simon & Garfunkel", "Simon and Garfunkel")]
    [InlineData("JAYZ", "JAY-Z")]
    public async Task HealAsync_MergeOfOneArtistsSpellings_Untouched(string oldValue, string canonical)
    {
        await using var db = NewContext();
        var song = Song(artist: canonical, albumArtist: canonical, artists: canonical);
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.Artist), "artist-merge", oldValue, canonical, ActionAt);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal(canonical, (await db.Songs.SingleAsync()).Artist);
    }

    // --- (c) credit split ---

    [Fact]
    public async Task HealAsync_SplitWroteThePhantomDelimiterArtist_Dropped()
    {
        await using var db = NewContext();
        var song = Song(artist: "Kanye West feat. Kid Cudi", albumArtist: "Kanye West", artists: "Kanye West; feat.; Kid Cudi");
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.Artists), "artist-credit-split", null, "Kanye West; feat.; Kid Cudi", ActionAt);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(new ArtistCreditRepairResult(1, 1, 1), result);
        Assert.Equal("Kanye West; Kid Cudi", (await db.Songs.SingleAsync()).Artists);
        Assert.Single(await db.SongMetadataChanges.Where(c => c.Source == ArtistCreditRepairHealer.SplitSource).ToListAsync());
    }

    [Fact]
    public async Task HealAsync_SplitOnASoloRow_Undone()
    {
        // The split matched rows by search key, and a featuring credit's key is its lead's — so a plain
        // "Kanye West" row with a blank list got the guest as well.
        await using var db = NewContext();
        var song = Song(artist: "Kanye West", albumArtist: "Kanye West", artists: "Kanye West; feat.; Kid Cudi");
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.Artists), "artist-credit-split", null, "Kanye West; feat.; Kid Cudi", ActionAt);
        await db.SaveChangesAsync();

        Assert.Equal(1, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Null((await db.Songs.SingleAsync()).Artists);
    }

    [Fact]
    public async Task HealAsync_SplitOnARowWithADifferentGuest_Undone()
    {
        await using var db = NewContext();
        var song = Song(artist: "Kanye West feat. Jay-Z", albumArtist: "Kanye West", artists: "Kanye West; feat.; Kid Cudi");
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.Artists), "artist-credit-split", null, "Kanye West; feat.; Kid Cudi", ActionAt);
        await db.SaveChangesAsync();

        await Healer(db).HealAsync();

        Assert.Null((await db.Songs.SingleAsync()).Artists);
    }

    [Theory]
    [InlineData("Kanye West & Kid Cudi", "Kanye West; Kid Cudi")]
    [InlineData("Kanye West (feat. Kid Cudi)", "Kanye West; Kid Cudi")]
    [InlineData("A, B", "A; B")]
    public async Task HealAsync_CorrectSplit_Untouched(string credit, string artists)
    {
        await using var db = NewContext();
        var song = Song(artist: credit, albumArtist: "Kanye West", artists: artists);
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.Artists), "artist-credit-split", null, artists, ActionAt);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal(artists, (await db.Songs.SingleAsync()).Artists);
    }

    [Fact]
    public async Task HealAsync_PhantomDelimiterAfterALaterMerge_StillDroppedWithItsAlignedId()
    {
        // A later merge renamed another segment (and gave the list aligned ids): the split is no
        // longer the list's last word, so it is not undone wholesale — but a "feat." segment is never
        // an artist, and its id slot goes with it so the list stays aligned.
        await using var db = NewContext();
        var song = Song(artist: "Kanye West feat. Kid Cudi", albumArtist: "Kanye West",
            artists: "Kanye West; feat.; KiD CuDi", artistMbids: "mbid-ye; mbid-junk; mbid-cudi");
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.Artists), "artist-credit-split", null, "Kanye West; feat.; Kid Cudi", ActionAt);
        AddChange(db, song, nameof(SongMetadata.Artists), "artist-merge",
            "Kanye West; feat.; Kid Cudi", "Kanye West; feat.; KiD CuDi", ActionAt.AddDays(1));
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(2, result.FieldsRepaired);
        var healed = await db.Songs.SingleAsync();
        Assert.Equal("Kanye West; KiD CuDi", healed.Artists);
        Assert.Equal("mbid-ye; mbid-cudi", healed.ArtistMusicBrainzIds);
    }

    [Fact]
    public async Task HealAsync_SplitNoLongerTheListsLastWord_NotUndoneWholesale()
    {
        // A later writer re-decided the list — even to the very same value — so the split's blank is no
        // longer what the list replaced. Only the phantom segment, which is never an artist, goes.
        await using var db = NewContext();
        var song = Song(artist: "Kanye West", albumArtist: "Kanye West", artists: "Kanye West; feat.; Kid Cudi");
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.Artists), "artist-credit-split", null, "Kanye West; feat.; Kid Cudi", ActionAt);
        AddChange(db, song, nameof(SongMetadata.Artists), "MusicBrainzWeb",
            "Kanye West; feat.; Kid Cudi", "Kanye West; feat.; Kid Cudi", ActionAt.AddDays(1));
        await db.SaveChangesAsync();

        await Healer(db).HealAsync();

        Assert.Equal("Kanye West; Kid Cudi", (await db.Songs.SingleAsync()).Artists);
    }

    [Fact]
    public async Task HealAsync_RevertedSplit_NotRepaired()
    {
        await using var db = NewContext();
        var song = Song(artist: "Kanye West", albumArtist: "Kanye West", artists: "Kanye West; feat.; Kid Cudi");
        db.Songs.Add(song);
        AddChange(db, song, nameof(SongMetadata.Artists), "artist-credit-split", null, "Kanye West; feat.; Kid Cudi", ActionAt,
            reverted: true);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
    }

    // --- (d) merge alias applied during enrichment ---

    [Theory]
    [InlineData("Nas feat. AZ")]
    [InlineData("Nas (feat. Lauryn Hill)")]
    public async Task HealAsync_AliasCutTheEnrichedCreditToItsLead_RestoresTheProvidersCredit(string credit)
    {
        await using var db = NewContext();
        var song = AliasDamagedSong(db, providerCredit: credit);
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(1, result.SongsRepaired);
        Assert.Equal(credit, (await db.Songs.SingleAsync()).Artist);
        var repair = await db.SongMetadataChanges.SingleAsync(c => c.Source == ArtistCreditRepairHealer.AliasSource);
        Assert.Equal((nameof(SongMetadata.Artist), "Nas", credit), (repair.FieldName, repair.OldValue, repair.NewValue));
        Assert.Equal(song.Id, repair.SongId);
    }

    [Fact]
    public async Task HealAsync_EnrichedBeforeTheAliasExisted_Untouched()
    {
        await using var db = NewContext();
        AliasDamagedSong(db, providerCredit: "Nas feat. AZ", aliasAt: ReEnrichedAt.AddDays(1));
        // An older, unrelated alias: the enrichment write is recent enough to be a candidate, so the
        // timing is decided per alias, not by the earliest one.
        db.ArtistAliases.Add(new ArtistAlias
        {
            OwnerUserId = Owner, AliasKey = "jayz", CanonicalName = "JAY-Z", CreatedAtUtc = EnrichedAt,
        });
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal("Nas", (await db.Songs.SingleAsync()).Artist);
    }

    [Theory]
    // The alias did what the user merged it for: a spelling of one artist.
    [InlineData("NAS")]
    // One artist whose name has a joiner — no featuring clause, no discrete list saying two.
    [InlineData("Nas & Friends")]
    public async Task HealAsync_AliasFoldingASingleArtist_Untouched(string providerCredit)
    {
        await using var db = NewContext();
        AliasDamagedSong(db, providerCredit: providerCredit,
            aliasKey: TitleNormalizer.NormalizeForSearch(providerCredit));
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
    }

    [Fact]
    public async Task HealAsync_NoAuditedEnrichmentWrite_Untouched()
    {
        // The row already said "Nas" and the merger kept it (same search key as the provider's
        // credit) — that is what it would have done without the alias too. Nothing to undo.
        await using var db = NewContext();
        AliasDamagedSong(db, providerCredit: "Nas feat. AZ", auditWrite: false);
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
    }

    // --- all repairs ---

    [Fact]
    public async Task HealAsync_SecondPass_NoOp()
    {
        await using var db = NewContext();
        SeedOneOfEach(db);
        await db.SaveChangesAsync();

        Assert.Equal(5, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Equal(new ArtistCreditRepairResult(0, 0, 0), await Healer(db).HealAsync());
        Assert.Empty(await Healer(db).DetectAsync());
    }

    [Fact]
    public async Task HealAsync_OnlyBuiltRowsRequeued()
    {
        await using var db = NewContext();
        db.Songs.Add(Song("/built.flac", artist: "Nas", albumArtist: "Nas (featuring AZ)", artists: null));
        db.Songs.Add(Song("/pending.flac", artist: "Nas", albumArtist: "Nas (featuring AZ)", artists: null,
            buildStatus: LibraryBuildStatus.Pending, destinationPath: null));
        await db.SaveChangesAsync();

        var result = await Healer(db).HealAsync();

        Assert.Equal(new ArtistCreditRepairResult(2, 2, 1), result);
        var pending = await db.Songs.SingleAsync(s => s.SourcePath == "/pending.flac");
        Assert.Equal("Nas", pending.AlbumArtist);
        Assert.Null(pending.PreviousDestinationPath);
    }

    [Theory]
    [InlineData("demo")]
    [InlineData("synthetic")]
    [InlineData("deleted")]
    public async Task HealAsync_DemoSyntheticAndDeletedRows_Excluded(string exclusion)
    {
        // Demo rows are seeded terminal with DestinationPath == SourcePath: a re-queue would point the
        // builder's delete at the read-only source mount.
        await using var db = NewContext();
        var songs = SeedOneOfEach(db);
        foreach (var song in songs)
        {
            switch (exclusion)
            {
                case "demo": song.OwnerUserId = WellKnownUsers.DemoId; break;
                case "synthetic": song.IsSynthetic = true; break;
                case "deleted": song.SoftDelete(); break;
            }
        }
        await db.SaveChangesAsync();

        Assert.Equal(0, (await Healer(db).HealAsync()).SongsRepaired);
        Assert.Empty(await Healer(db).DetectAsync());
        Assert.DoesNotContain(await db.SongMetadataChanges.IgnoreQueryFilters().ToListAsync(),
            c => c.Source.EndsWith("-repair", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DetectAsync_ReportsEveryFieldWithoutMutating()
    {
        await using var db = NewContext();
        db.Songs.Add(Song(artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
            artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: null));
        await db.SaveChangesAsync();

        var repairs = await Healer(db).DetectAsync();

        Assert.Equal(
            new (string Kind, string Field, string? From, string? To)[]
            {
                (ArtistCreditRepairHealer.AlbumArtistSource, nameof(SongMetadata.AlbumArtist), "Hef met Jayh", "Hef"),
                (ArtistCreditRepairHealer.AlbumArtistSource, nameof(SongMetadata.AlbumArtistMusicBrainzId), null, "mbid-hef"),
            },
            repairs.Select(r => (r.Kind, r.Field, r.From, r.To)).ToArray());

        var untouched = await db.Songs.SingleAsync();
        Assert.Equal("Hef met Jayh", untouched.AlbumArtist);
        Assert.Equal(LibraryBuildStatus.Done, untouched.LibraryBuildStatus);
        Assert.Empty(await db.SongMetadataChanges.ToListAsync());
    }

    // One damaged row per repair kind (the collab album artist, the featuring album artist, a merge,
    // a split, an alias).
    private static List<SongMetadata> SeedOneOfEach(MusicHoarderDbContext db)
    {
        var collab = Song("/1.flac", artist: "Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh",
            artistMbids: "mbid-hef; mbid-jayh", albumArtistMbid: "mbid-hef");
        var featuring = Song("/2.flac", artist: "Nas (featuring AZ)", albumArtist: "Nas (featuring AZ)", artists: null);
        var merged = Song("/3.flac", artist: "Nas", albumArtist: "Nas", artists: null);
        var split = Song("/4.flac", artist: "Kanye West", albumArtist: "Kanye West", artists: "Kanye West; feat.; Kid Cudi");
        db.Songs.AddRange(collab, featuring, merged, split);
        AddChange(db, merged, nameof(SongMetadata.Artist), "artist-merge", "Nas (featuring AZ)", "Nas", ActionAt);
        AddChange(db, split, nameof(SongMetadata.Artists), "artist-credit-split", null, "Kanye West; feat.; Kid Cudi", ActionAt);
        var aliased = AliasDamagedSong(db, providerCredit: "Nas feat. AZ", sourcePath: "/5.flac");
        return [collab, featuring, merged, split, aliased];
    }

    // A song enriched after a merge stored alias "nas" → "Nas": the winning MusicBrainz candidate said
    // `providerCredit`, the alias turned it into "Nas", and the merger filled the blank credit with it.
    private static SongMetadata AliasDamagedSong(
        MusicHoarderDbContext db,
        string providerCredit,
        DateTime? aliasAt = null,
        string aliasKey = "nas",
        bool auditWrite = true,
        string sourcePath = "/aliased.flac")
    {
        var song = Song(sourcePath, artist: "Nas", albumArtist: "Nas", artists: "Nas; AZ",
            musicBrainzId: "rec-1", enrichedAt: ReEnrichedAt);
        song.MatchedBy = "MusicBrainzWeb";
        song.ProviderAttempts.Add(new SongProviderAttempt
        {
            Provider = EnrichmentProvider.MusicBrainzWeb,
            Status = ProviderAttemptStatus.Matched,
            AttemptedAtUtc = ReEnrichedAt,
            MatchedDataJson = JsonSerializer.Serialize(new EnrichmentProviderResult(
                providerCredit, "Nas", "Title", 2020, 1, "rec-1", null, null, null, null,
                "MusicBrainzWeb", 0.95, [], EnrichmentStatus.Matched, Artists: "Nas; AZ")),
        });
        db.Songs.Add(song);
        if (!db.ArtistAliases.Local.Any(a => a.AliasKey == aliasKey))
        {
            db.ArtistAliases.Add(new ArtistAlias
            {
                OwnerUserId = Owner, AliasKey = aliasKey, CanonicalName = "Nas", CreatedAtUtc = aliasAt ?? AliasAt,
            });
        }
        if (auditWrite)
            AddChange(db, song, nameof(SongMetadata.Artist), "MusicBrainzWeb", null, "Nas", ReEnrichedAt);
        return song;
    }

    private static void AddChange(
        MusicHoarderDbContext db, SongMetadata song, string field, string source,
        string? oldValue, string? newValue, DateTime at, bool reverted = false)
        => db.SongMetadataChanges.Add(new SongMetadataChange
        {
            Song = song,
            FieldName = field,
            OldValue = oldValue,
            NewValue = newValue,
            Source = source,
            Confidence = 1.0,
            CreatedAtUtc = at,
            AppliedAtUtc = at,
            RevertedAtUtc = reverted ? at.AddHours(1) : null,
        });

    private static MusicHoarderDbContext NewContext() => new(
        new DbContextOptionsBuilder<MusicHoarderDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static IArtistCreditRepairHealer Healer(MusicHoarderDbContext db, bool canonicalDrivenBuild = true)
        => new ArtistCreditRepairHealer(
            db,
            Microsoft.Extensions.Options.Options.Create(new MusicEnricherOptions
            {
                SourceDirectory = "/source",
                DestinationDirectory = "/dest",
                EnableCanonicalDrivenBuild = canonicalDrivenBuild,
            }),
            NullLogger<ArtistCreditRepairHealer>.Instance);

    private static SongMetadata Song(
        string sourcePath = "/a.flac",
        string? artist = null,
        string? albumArtist = null,
        string? artists = null,
        string? artistMbids = null,
        string? albumArtistMbid = null,
        string? musicBrainzId = null,
        DateTime? enrichedAt = null,
        LibraryBuildStatus buildStatus = LibraryBuildStatus.Done,
        string? destinationPath = "unset") => new()
    {
        OwnerUserId = Owner,
        SourcePath = sourcePath,
        FileName = Path.GetFileName(sourcePath),
        Extension = Path.GetExtension(sourcePath),
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        EnrichmentStatus = EnrichmentStatus.Matched,
        EnrichedAtUtc = enrichedAt ?? EnrichedAt,
        OriginalMetadataCaptured = true,
        Artist = artist,
        AlbumArtist = albumArtist,
        Album = "Album",
        Title = "Title",
        TrackNumber = 1,
        Year = 2020,
        Artists = artists,
        ArtistMusicBrainzIds = artistMbids,
        AlbumArtistMusicBrainzId = albumArtistMbid,
        MusicBrainzId = musicBrainzId,
        LibraryBuildStatus = buildStatus,
        DestinationPath = destinationPath == "unset" ? $"/dest/{albumArtist}/2020 - Album{sourcePath}" : destinationPath,
    };
}

using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

public class AlbumGroupKeyTests
{
    [Fact]
    public void For_YearDivergence_SameKey()
    {
        // Year is the most common split (the destination folder is "{Year} - {Album}"), so it must
        // not participate in the logical-album key.
        var a = Song(album: "Graduation", year: 2007);
        var b = Song(album: "Graduation", year: 2008);

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Fact]
    public void For_NormalizedTitleAndArtistVariants_SameKey()
    {
        var a = Song(album: "Believe", albumArtist: "Beyoncé");
        var b = Song(album: "Belíeve", albumArtist: "Beyonce");

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Theory]
    [InlineData("Graduation", "Graduation (Deluxe Edition)")]
    [InlineData("Graduation", "Graduation (Live)")]
    [InlineData("Graduation", "Graduation (Remastered)")]
    [InlineData("Graduation (Deluxe)", "Graduation (Live)")]
    public void For_EditionQualifiers_DistinctKeys(string albumA, string albumB)
    {
        // NormalizeForSearch strips parenthesized qualifiers, so without the VersionQualifier
        // discriminator a deluxe/live edition would merge into the standard album — the one
        // dangerous direction.
        var a = Song(album: albumA);
        var b = Song(album: albumB);

        Assert.NotEqual(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Fact]
    public void For_SameQualifierDifferentSpelling_SameKey()
    {
        var a = Song(album: "Graduation (Deluxe)");
        var b = Song(album: "Graduation (Deluxe Edition)");

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Fact]
    public void For_VariousArtistsCompilations_GroupAcrossSentinelSpellings()
    {
        var a = Song(album: "Now That's Music", albumArtist: "Various Artists", isCompilation: true);
        var b = Song(album: "Now That's Music", albumArtist: "VA", isCompilation: true);
        var c = Song(album: "Now That's Music", albumArtist: null, isCompilation: true);
        // A provider answering in another locale names the same slot ("Verschiedene Interpreten"),
        // so it must land in the same group rather than splitting the compilation in two.
        var d = Song(album: "Now That's Music", albumArtist: "Verschiedene Interpreten", isCompilation: true);

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(c));
        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(d));
        Assert.Equal(AlbumGroupKey.VariousArtistsKey, AlbumGroupKey.For(a)!.ArtistKey);
    }

    [Fact]
    public void For_CompilationFlaggedSingleArtistRelease_StaysUnderArtist()
    {
        // A greatest-hits a provider flagged "compilation" but with a real album artist must not be
        // exiled into the Various Artists group (mirrors the DestinationPathResolver routing).
        var song = Song(album: "Greatest Hits", albumArtist: "Queen", isCompilation: true);

        Assert.Equal("queen", AlbumGroupKey.For(song)!.ArtistKey);
    }

    [Fact]
    public void For_MissingAlbumArtist_FallsBackToPrimaryArtist()
    {
        var a = Song(album: "Watch the Throne", albumArtist: null, artist: "JAY-Z & Kanye West");
        var b = Song(album: "Watch the Throne", albumArtist: "JAY-Z");

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Theory]
    [InlineData("Marvin Gaye", "Marvin Gaye & Tammi Terrell")]
    [InlineData("Domo Genesis", "Domo Genesis, The Alchemist")]
    [InlineData("JAY-Z", "JAY-Z & Kanye West")]
    [InlineData("Juice WRLD", "Juice WRLD feat. The Weeknd")]
    public void For_CollaboratorSuffixSpellings_ShareOneKey(string solo, string collaboration)
    {
        // The prod flip: per-song enrichment credited half of an album to the lead artist alone and
        // half to the full collaboration. While those were two keys the album-identity election never
        // saw the halves together, so each half elected its own spelling and the album lived in two
        // artist folders at once.
        var a = Song(album: "United", albumArtist: solo, artist: collaboration);
        var b = Song(album: "United", albumArtist: collaboration, artist: collaboration);

        Assert.Equal(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Fact]
    public void For_IsInvariantUnderTheAlbumIdentityElection()
    {
        // The property that stops the oscillation. Both writers of the album identity — the
        // build-time election in LibraryBuilderService and the persisting AlbumSplitHealer — group on
        // this key and then write AlbumArtist. If that write can move a member into a different
        // group, the other group elects the other spelling and the album ping-pongs forever, each
        // flip a full re-tag + relocate + sync push. So: whatever the election writes back, every
        // member must still key to the group that elected it.
        var members = new[]
        {
            Song(album: "United", albumArtist: "Marvin Gaye", artist: "Marvin Gaye & Tammi Terrell"),
            Song(album: "United", albumArtist: "Marvin Gaye", artist: "Marvin Gaye & Tammi Terrell"),
            Song(album: "United", albumArtist: "Marvin Gaye & Tammi Terrell", artist: "Marvin Gaye & Tammi Terrell"),
        };
        var key = AlbumGroupKey.For(members[0])!;
        Assert.All(members, m => Assert.Equal(key, AlbumGroupKey.For(m)));

        var elected = new AlbumIdentityReconciler().Reconcile(members);
        foreach (var member in members)
        {
            member.ApplyIdentityCorrection(elected);
        }

        Assert.Single(members.Select(m => m.AlbumArtist).Distinct(StringComparer.Ordinal));
        Assert.All(members, m => Assert.Equal(key, AlbumGroupKey.For(m)));
    }

    [Fact]
    public void For_DifferentLeadArtists_StayDistinct()
    {
        // The lead-artist fold must not merge two artists who merely share an album title.
        var a = Song(album: "Greatest Hits", albumArtist: "Marvin Gaye");
        var b = Song(album: "Greatest Hits", albumArtist: "Tammi Terrell");

        Assert.NotEqual(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(((")] // normalizes to empty
    public void For_NoUsableAlbum_ReturnsNull(string? album)
    {
        Assert.Null(AlbumGroupKey.For(Song(album: album)));
    }

    [Fact]
    public void For_DifferentOwners_DistinctKeys()
    {
        var a = Song(album: "Graduation");
        var b = Song(album: "Graduation");
        b.OwnerUserId = Guid.NewGuid();

        Assert.NotEqual(AlbumGroupKey.For(a), AlbumGroupKey.For(b));
    }

    [Fact]
    public void ComputeKeys_MatchSongSideKeys_ForEndpointLookups()
    {
        // The rebuild endpoint computes keys from the display artist/album the album view shows —
        // those must land in the same group as the song rows themselves.
        var song = Song(album: "Graduation (Deluxe)", albumArtist: "Kanye West");
        var key = AlbumGroupKey.For(song)!;

        Assert.Equal(key.ArtistKey, AlbumGroupKey.ComputeArtistKey("Kanye West"));
        Assert.Equal(key.AlbumKey, AlbumGroupKey.ComputeAlbumKey("Graduation (Deluxe)"));
    }

    [Fact]
    public void ComputeArtistKey_CollaborationCredit_MatchesEitherSpelling()
    {
        // The rebuild / album-dedup endpoints are handed whichever spelling the album view happens to
        // be showing; both must resolve to the group the song rows are in.
        var song = Song(album: "United", albumArtist: "Marvin Gaye & Tammi Terrell");
        var key = AlbumGroupKey.For(song)!;

        Assert.Equal(key.ArtistKey, AlbumGroupKey.ComputeArtistKey("Marvin Gaye & Tammi Terrell"));
        Assert.Equal(key.ArtistKey, AlbumGroupKey.ComputeArtistKey("Marvin Gaye"));
    }

    [Fact]
    public void ComputeArtistKey_LeadFoldsToNothing_KeepsTheWholeCredit()
    {
        // A credit whose lead segment is pure punctuation would otherwise key to the empty string and
        // drop the song out of every group.
        Assert.Equal("marvin gaye", AlbumGroupKey.ComputeArtistKey("?, Marvin Gaye"));
    }

    private static SongMetadata Song(
        string? album,
        int? year = null,
        string? albumArtist = "Kanye West",
        string? artist = "Kanye West",
        bool isCompilation = false) => new()
    {
        OwnerUserId = WellKnownUsers.OwnerId,
        SourcePath = "/src/x.flac",
        FileName = "x.flac",
        Extension = ".flac",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Album = album,
        Year = year,
        AlbumArtist = albumArtist,
        Artist = artist,
        IsCompilation = isCompilation,
    };
}

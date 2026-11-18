using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The credit rules on their own, over rows in memory: which names the library shows to be several
/// artists, which parts a split is offered with, and exactly what a split writes.
/// </summary>
public class ArtistCreditEvidenceTests
{
    [Fact]
    public void SingleArtistNames_LeaveOutSplitCreditsPlaceholdersAndDelimiterTokens()
    {
        var evidence = ArtistCreditEvidence.Build(
            [],
            ["Kanye West", "JAY-Z & Kanye West", "Nas feat. AZ", "Various Artists", "Unknown Artist", "feat.", "Hef met Jayh"]);

        // "Hef met Jayh" stays: SplitArtists leaves it whole, and only IsCollabCredit says otherwise.
        Assert.Equal(["Kanye West", "Hef met Jayh"], evidence.SingleArtistNames);
    }

    public static TheoryData<string, bool> CollabCases => new()
    {
        { "JAY-Z & Kanye West", true },      // (i) SplitArtists splits it
        { "Nas (featuring AZ)", true },      // (i) a bracketed featuring clause
        { "Anuel AA y Ozuna", true },        // (ii) a row's discrete list is its joined form
        { "ANUEL AA Y OZUNA", true },        // (ii) matched by artist key
        { "2Pac + Outlawz", true },          // (iii) every part stands alone
        { "Florence + the Machine", false }, // (iii) "the Machine" stands alone only as "The Machine (live)"
        { "Simon and Garfunkel", false },    // (iii) neither part stands alone
        { "Kanye West", false },
    };

    [Theory]
    [MemberData(nameof(CollabCases))]
    public void IsCollabCredit_FollowsTheThreeRules(string credit, bool expected) =>
        Assert.Equal(expected, Library().IsCollabCredit(credit));

    [Fact]
    public void OfferedParts_ComeFromTheProvingList_ThenTheText_ThenACorroboratedJoin()
    {
        var evidence = Library();

        Assert.Equal(["Anuel AA", "Ozuna"], evidence.OfferedParts("Anuel AA y Ozuna"));
        Assert.Equal(["JAY-Z", "Kanye West"], evidence.OfferedParts("JAY-Z & Kanye West"));
        Assert.Equal(["2Pac", "Outlawz"], evidence.OfferedParts("2Pac + Outlawz"));
        // A featuring credit needs only its lead to stand alone; "&" needs every part.
        Assert.Equal(["Kanye West", "Kid Cudi"], evidence.OfferedParts("Kanye West feat. Kid Cudi"));
        Assert.Null(evidence.OfferedParts("JAY-Z & Beyoncé"));
        Assert.Null(evidence.OfferedParts("Florence + the Machine"));
        Assert.Null(evidence.OfferedParts("Kanye West"));
    }

    [Theory]
    [InlineData("Nobody feat. Someone", true)]  // any featuring credit, corroborated or not
    [InlineData("JAY-Z & Kanye West", true)]    // offered as a split
    [InlineData("Simon & Garfunkel", false)]    // a bare "&" name nothing corroborates
    [InlineData("Kanye West", false)]
    public void IsCombinedCredit_IsWhatMergeRefusesAsItsCanonical(string credit, bool expected) =>
        Assert.Equal(expected, Library().IsCombinedCredit(credit));

    [Theory]
    [InlineData("Simon & Garfunkel", true)]      // the text alone splits it
    [InlineData("Anuel AA y Ozuna", true)]       // a row's list proves it
    [InlineData("2Pac + Outlawz", true)]         // a corroborated join
    [InlineData("Simon and Garfunkel", false)]   // an uncorroborated join
    [InlineData("Kanye West", false)]
    public void CanSplit_NeedsTextPartsOrAProvingList(string credit, bool expected) =>
        Assert.Equal(expected, Library().CanSplit(credit));

    [Fact]
    public void ProvingLists_TheMostCommonWins_ThenTheOrdinalFirst()
    {
        SongMetadata[] rows =
        [
            Song("Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Jayh; Hef"),
            Song("Hef met Jayh", albumArtist: "Hef met Jayh", artists: "Hef; Jayh"),
            Song("Hef met Jayh", albumArtist: "Hef met Jayh", artists: "HEF; Jayh"),
        ];

        // "Jayh; Hef" never proves the credit: its parts are out of order.
        Assert.False(ArtistCreditEvidence.Build(rows[..1], []).IsCollabCredit("Hef met Jayh"));
        // One each: the ordinal first list wins the tie; one more "Hef; Jayh" outvotes it.
        Assert.Equal(["HEF", "Jayh"], ArtistCreditEvidence.Build(rows, []).OfferedParts("Hef met Jayh"));
        Assert.Equal(["Hef", "Jayh"], ArtistCreditEvidence.Build([.. rows, rows[1]], []).OfferedParts("Hef met Jayh"));
    }

    [Fact]
    public void PlanSplit_WritesExactlyTheseChanges()
    {
        SongMetadata[] rows =
        [
            // Blank list, credit as Artist: backfilled from the text parts.
            Song("2Pac + Outlawz"),
            // Album artist only: moves to the lead, its list stays blank, and the guest's id goes.
            Song("2Pac", albumArtist: "2Pac + Outlawz", albumArtistMbid: "mbid-outlawz"),
            // The row's own list proves the join: its spelling of the lead, and its aligned lead id.
            Song("2PAC + Outlawz", albumArtist: "2PAC + Outlawz", artists: "2PAC; Outlawz",
                artistMbids: "mbid-2pac; mbid-outlawz", albumArtistMbid: "mbid-outlawz"),
            // Not the credit by artist key.
            Song("2Pac"),
        ];
        var evidence = ArtistCreditEvidence.Build(
            [.. rows, Song("2Pac", artists: "2Pac; Outlawz", artistMbids: "mbid-2pac; mbid-outlawz")],
            ["2Pac", "Outlawz", "2Pac + Outlawz"]);

        var plan = evidence.PlanSplit("2Pac + Outlawz", rows);

        Assert.Equal(
            [
                "0 moves=False Artists: <null> -> 2Pac; Outlawz",
                "1 moves=True AlbumArtist: 2Pac + Outlawz -> 2Pac | AlbumArtistMusicBrainzId: mbid-outlawz -> <null>",
                "2 moves=True AlbumArtist: 2PAC + Outlawz -> 2PAC | AlbumArtistMusicBrainzId: mbid-outlawz -> mbid-2pac",
            ],
            plan.Select(e => $"{Array.IndexOf(rows, e.Song)} moves={e.MovesAlbumArtist} "
                + string.Join(" | ", e.Changes.Select(c => $"{c.Field}: {c.Old ?? "<null>"} -> {c.New ?? "<null>"}"))));
    }

    [Fact]
    public void PlanSplit_KeepsAnIdTheLeadAlsoCarries()
    {
        var row = Song("Hef", albumArtist: "Hef met Jayh", albumArtistMbid: "mbid-shared");
        var evidence = ArtistCreditEvidence.Build(
            [row, Song("Hef", artists: "Hef", artistMbids: "mbid-shared"), Song("Jayh", artists: "Jayh", artistMbids: "mbid-shared")],
            ["Hef", "Jayh"]);

        var edit = Assert.Single(evidence.PlanSplit("Hef met Jayh", [row]));

        Assert.Equal(("AlbumArtist", "Hef met Jayh", "Hef"), Assert.Single(edit.Changes));
    }

    [Fact]
    public void PlanSplit_MatchesRowsByArtistKey_NeverTheLeadsOwnRows()
    {
        // Under the search key a featuring credit keys as its lead.
        SongMetadata[] rows = [Song("Kanye West feat. Kid Cudi"), Song("Kanye West")];
        var evidence = ArtistCreditEvidence.Build(rows, ["Kanye West"]);

        var edit = Assert.Single(evidence.PlanSplit("Kanye West feat. Kid Cudi", rows));

        Assert.Same(rows[0], edit.Song);
    }

    [Fact]
    public void PlanSplit_DropsAGuestsId_StoredWithSurroundingSpaces()
    {
        var row = Song("Hef", albumArtist: "Hef met Jayh", albumArtistMbid: " mbid-jayh ");
        var evidence = ArtistCreditEvidence.Build(
            [row, Song("Hef", artists: "Hef", artistMbids: "mbid-hef"), Song("Jayh", artists: "Jayh", artistMbids: "mbid-jayh")],
            ["Hef", "Jayh"]);

        var edit = Assert.Single(evidence.PlanSplit("Hef met Jayh", [row]));

        Assert.Equal(("AlbumArtistMusicBrainzId", " mbid-jayh ", null), edit.Changes[1]);
    }

    [Fact]
    public void PlanSplit_OfAnUnsplittableCredit_IsEmpty()
    {
        var row = Song("Simon and Garfunkel");

        Assert.Empty(ArtistCreditEvidence.Build([row], ["Simon and Garfunkel"]).PlanSplit("Simon and Garfunkel", [row]));
    }

    [Theory]
    [InlineData("Various Artists", true)]
    [InlineData(" verschiedene interpreten ", true)]
    [InlineData("Unknown Artist", true)]
    [InlineData("UNKNOWN", true)]
    [InlineData("[unknown]", true)]
    [InlineData("Unknown Mortal Orchestra", false)]
    [InlineData("Kanye West", false)]
    public void IsPlaceholderName_CoversTheSentinelsAndTheUnknowns(string name, bool expected) =>
        Assert.Equal(expected, ArtistCreditEvidence.IsPlaceholderName(name));

    [Fact]
    public void DiscreteArtists_DropsPhantomDelimiterSegments()
    {
        Assert.Equal(["Kanye West", "Kid Cudi"], ArtistCreditEvidence.DiscreteArtists(Song("x", artists: "Kanye West; feat.; Kid Cudi")));
        Assert.Empty(ArtistCreditEvidence.DiscreteArtists(Song("x")));
    }

    // One library for the rule tables: standalone artists, a list-proven join the splitter can't
    // read, and a qualified name that must not vouch for a bare part.
    private static ArtistCreditEvidence Library()
    {
        SongMetadata[] rows =
        [
            Song("Anuel AA y Ozuna", albumArtist: "Anuel AA y Ozuna", artists: "Anuel AA; Ozuna"),
            Song("Kanye West", artists: "Kanye West"),
            Song("JAY-Z", artists: "JAY-Z"),
            Song("2Pac", artists: "2Pac"),
            Song("Outlawz", artists: "Outlawz"),
            Song("Florence", artists: "Florence"),
            Song("The Machine (live)", artists: "The Machine (live)"),
        ];
        return ArtistCreditEvidence.Build(
            rows,
            ["Anuel AA", "Ozuna", "Anuel AA y Ozuna", "Kanye West", "JAY-Z", "2Pac", "Outlawz", "Florence", "The Machine (live)"]);
    }

    private static SongMetadata Song(
        string artist,
        string? albumArtist = null,
        string? artists = null,
        string? artistMbids = null,
        string? albumArtistMbid = null) => new()
    {
        SourcePath = "/a/x.mp3",
        FileName = "x.mp3",
        Extension = ".mp3",
        FileSizeBytes = 1,
        LastModifiedUtc = DateTime.UtcNow,
        IndexedAtUtc = DateTime.UtcNow,
        Artist = artist,
        AlbumArtist = albumArtist,
        Artists = artists,
        ArtistMusicBrainzIds = artistMbids,
        AlbumArtistMusicBrainzId = albumArtistMbid,
    };
}

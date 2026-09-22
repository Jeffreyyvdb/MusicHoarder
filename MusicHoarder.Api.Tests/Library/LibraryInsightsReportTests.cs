using System.Text.Json;
using System.Text.Json.Nodes;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The insights payload rules, driven from plain inputs — no database. The endpoint-level pins in
/// <c>Endpoints/InsightsPayloadTests</c> keep covering the queries, the owner scoping and the exact
/// wire shape; these cover the arithmetic, normalisation and ordering in isolation.
/// </summary>
public class LibraryInsightsReportTests
{
    private static readonly DateTime Now = new(2025, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private static JsonNode Build(LibraryInsightsInputs inputs)
    {
        var report = LibraryInsightsReport.Build(inputs, Now);
        return JsonSerializer.SerializeToNode(report, report.GetType())!;
    }

    private static string[] Pairs(JsonNode? array, string label, string value) =>
        array!.AsArray().Select(x => $"{x![label]}={x[value]}").ToArray();

    [Fact]
    public void NoRows_EveryPercentageIsZero_AndTheDatesAreNull()
    {
        var json = Build(new LibraryInsightsInputs());

        Assert.Equal(Now, json["generatedAtUtc"]!.GetValue<DateTime>());
        Assert.Equal(0, json["source"]!["inLibraryPct"]!.GetValue<double>());
        Assert.All(json["funnel"]!.AsArray(), s => Assert.Equal(0, s!["pct"]!.GetValue<double>()));
        Assert.All(json["wishlist"]!["funnel"]!.AsArray(), s => Assert.Equal(0, s!["pct"]!.GetValue<double>()));
        Assert.Equal(0, json["covers"]!["coveragePct"]!.GetValue<double>());
        Assert.Null(json["totals"]!["oldestIndexedUtc"]);
        Assert.Null(json["totals"]!["newestIndexedUtc"]);
    }

    [Fact]
    public void Percentages_OfAnEmptyDenominator_AreZero_EvenWithANonZeroPart()
    {
        var json = Build(new LibraryInsightsInputs
        {
            Built = new InsightsBuiltCounts { InLibrary = 4 },
            Liked = new InsightsLikedCounts { Downloaded = 3, InLibrary = 1 },
        });

        Assert.Equal(0, json["source"]!["inLibraryPct"]!.GetValue<double>());
        Assert.Equal(["Indexed=0", "Fingerprinted=0", "Matched=0", "In library=0"], Pairs(json["funnel"], "stage", "pct"));
        Assert.Equal(["Liked / wishlisted=0", "Downloaded=0", "In library=0"], Pairs(json["wishlist"]!["funnel"], "stage", "pct"));
    }

    [Fact]
    public void Percentages_AreOfTheirOwnDenominator_RoundedToOneDecimal()
    {
        var json = Build(new LibraryInsightsInputs
        {
            Songs = new InsightsSongCounts { Indexed = 3, Fingerprinted = 2, Matched = 1, WithIsrc = 1 },
            Built = new InsightsBuiltCounts { InLibrary = 8, WithCover = 1, WithLyrics = 7 },
        });

        Assert.Equal(266.7, json["source"]!["inLibraryPct"]!.GetValue<double>()); // 8 of 3, unclamped
        Assert.Equal(0, json["source"]!["notYetBuilt"]!.GetValue<int>());          // clamped at zero
        Assert.Equal(["Indexed=100", "Fingerprinted=66.7", "Matched=33.3", "In library=266.7"], Pairs(json["funnel"], "stage", "pct"));
        Assert.Equal(12.5, json["covers"]!["coveragePct"]!.GetValue<double>());   // of built, not indexed
        Assert.Equal(87.5, json["lyrics"]!["coveragePct"]!.GetValue<double>());
        Assert.Equal(33.3, json["quality"]!["coverage"]!["isrc"]!["pct"]!.GetValue<double>());
    }

    [Fact]
    public void Totals_ConvertSecondsToHoursAndBytesToGiB()
    {
        var json = Build(new LibraryInsightsInputs
        {
            Built = new InsightsBuiltCounts { InLibrary = 1, DurationSeconds = 5_400, Bytes = 3L * 512 * 1024 * 1024 },
        });

        Assert.Equal(1.5, json["totals"]!["totalHours"]!.GetValue<double>());
        Assert.Equal(1.5, json["totals"]!["totalGiB"]!.GetValue<double>());
    }

    [Fact]
    public void Formats_FoldCaseAndLeadingDot_DropBlanks_LargestFirst()
    {
        var json = Build(new LibraryInsightsInputs
        {
            SongsByExtension =
            [
                new(".mp3", 2), new(".FLAC", 1), new("flac", 4), new("", 9), new(".", 9), new("MP3", 1), new(".ogg", 3),
            ],
        });

        Assert.Equal(["flac=5", "mp3=3", "ogg=3"], Pairs(json["totals"]!["byFormat"], "format", "count"));
    }

    [Fact]
    public void Providers_AreNamed_MostMatchedFirst_ThenMostAttempted()
    {
        var json = Build(new LibraryInsightsInputs
        {
            AttemptsByProvider =
            [
                new(EnrichmentProvider.Deezer, 1, 0),
                new(EnrichmentProvider.Tracker, 1, 1),
                new(EnrichmentProvider.MusicBrainzWeb, 5, 0),
                new(EnrichmentProvider.AcoustID, 4, 1),
                new(EnrichmentProvider.SpotifyAPI, 2, 2),
            ],
        });

        Assert.Equal(
            ["SpotifyAPI=2", "AcoustID=1", "Tracker=1", "MusicBrainzWeb=0", "Deezer=0"],
            Pairs(json["quality"]!["byProvider"], "provider", "matched"));
    }

    [Fact]
    public void Artists_PreferAlbumArtist_Trimmed_CaseInsensitive_FirstSpellingWins()
    {
        var json = Build(new LibraryInsightsInputs
        {
            BuiltTracks =
            [
                new("Band", "Band feat. Guest", null),
                new(null, "  band ", null),
                new("BAND", null, null),
                new(null, null, null),
                new("   ", "ignored", null), // blank album artist still wins over the artist tag
                new(null, "Solo", null),
            ],
        });

        Assert.Equal(["Band=3", "Solo=1"], Pairs(json["top"]!["artists"], "name", "tracks"));
        Assert.Equal(2, json["totals"]!["distinctArtists"]!.GetValue<int>());
    }

    [Fact]
    public void Albums_GroupOnTrimmedAlbumAndArtist_CaseInsensitive_SkippingUntitled()
    {
        var json = Build(new LibraryInsightsInputs
        {
            BuiltTracks =
            [
                new("Band", null, " Debut "),
                new(null, "band", "debut"),
                new("Band", null, "Debut"),
                new("Other", null, "Debut"),   // same title, different artist: its own album
                new("Band", null, "  "),       // untitled: skipped
                new("Band", null, null),
            ],
        });

        Assert.Equal(
            ["Debut/Band=3", "Debut/Other=1"],
            json["top"]!["albums"]!.AsArray().Select(x => $"{x!["album"]}/{x["artist"]}={x["tracks"]}"));
        Assert.Equal(2, json["totals"]!["distinctAlbums"]!.GetValue<int>());
    }

    [Fact]
    public void LikedStatusBreakdown_SplitsDownloadedFromInLibrary_NeverBelowZero()
    {
        var json = Build(new LibraryInsightsInputs
        {
            Liked = new InsightsLikedCounts
            {
                Total = 10, Downloaded = 2, InLibrary = 3, SkippedOwned = 1, Downloading = 1, Pending = 2, NotFound = 1, Failed = 1,
            },
        });

        Assert.Equal(
            ["In library=3", "Downloaded=0", "Already owned=1", "Downloading=1", "Pending=2", "Not found=1", "Failed=1"],
            Pairs(json["wishlist"]!["statusBreakdown"], "status", "count"));
        Assert.Equal(
            ["Liked / wishlisted=100", "Downloaded=20", "In library=30"],
            Pairs(json["wishlist"]!["funnel"], "stage", "pct"));
    }
}

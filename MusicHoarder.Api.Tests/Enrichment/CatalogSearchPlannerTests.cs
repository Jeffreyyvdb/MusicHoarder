using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Enrichment.Providers;

namespace MusicHoarder.Api.Tests.Enrichment;

/// <summary>
/// Exercises the shared catalog search-query plan in isolation — the concrete payoff of pulling the
/// query-selection decision out of the Deezer / Apple Music providers into one place.
/// </summary>
public class CatalogSearchPlannerTests
{
    [Fact]
    public void PlanQueries_TaggedWithoutAlbum_SingleArtistTitleQuery()
    {
        var resolved = new SongSearchText.Resolved("Juice WRLD", Album: null, "Lucid Dreams", TrackNumber: null);

        var queries = CatalogSearchPlanner.PlanQueries(resolved);

        Assert.Equal(["Juice WRLD Lucid Dreams"], queries);
    }

    [Fact]
    public void PlanQueries_TaggedWithAlbum_NarrowedFirstThenUnnarrowedFallback()
    {
        var resolved = new SongSearchText.Resolved(
            "Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams", TrackNumber: null);

        var queries = CatalogSearchPlanner.PlanQueries(resolved);

        Assert.Equal(
            ["Juice WRLD Lucid Dreams Goodbye & Good Riddance", "Juice WRLD Lucid Dreams"],
            queries);
    }

    [Fact]
    public void PlanQueries_PathDerivedIdentity_UsesFilenameFreeTextAloneNoAlbum()
    {
        // A path-derived identity must query on the cleaned filename free-text (PathQuery) only, never
        // the positional folder guess, and must not be narrowed by a path-guessed album.
        var resolved = new SongSearchText.Resolved("Bucket", "Some Folder", "Lucid Dreams", TrackNumber: null)
        {
            ArtistFromPath = true,
            TitleFromPath = true,
            RawSearchText = "Lucid Dreams",
        };

        var queries = CatalogSearchPlanner.PlanQueries(resolved);

        var single = Assert.Single(queries);
        Assert.Contains("Lucid Dreams", single);
        Assert.DoesNotContain("Some Folder", single);
    }

    [Fact]
    public async Task SearchAsync_StopsAtFirstNonEmptyResult_NoExtraCall()
    {
        var resolved = new SongSearchText.Resolved(
            "Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams", TrackNumber: null);
        var calls = new List<string>();

        var results = await CatalogSearchPlanner.SearchAsync<string>(
            resolved,
            (query, _) =>
            {
                calls.Add(query);
                return Task.FromResult<IReadOnlyList<string>>(["hit"]);
            });

        Assert.Equal(["hit"], results);
        Assert.Single(calls); // the narrowed query hit, so the fallback query is never issued
    }

    [Fact]
    public async Task SearchAsync_FallsThroughToLaterQuery_WhenEarlierReturnsEmpty()
    {
        var resolved = new SongSearchText.Resolved(
            "Juice WRLD", "Goodbye & Good Riddance", "Lucid Dreams", TrackNumber: null);
        var calls = new List<string>();

        var results = await CatalogSearchPlanner.SearchAsync<string>(
            resolved,
            (query, _) =>
            {
                calls.Add(query);
                // Album-narrowed query finds nothing; the un-narrowed fallback hits.
                IReadOnlyList<string> hits = query.Contains("Goodbye") ? [] : ["hit"];
                return Task.FromResult(hits);
            });

        Assert.Equal(["hit"], results);
        Assert.Equal(2, calls.Count);
        Assert.Contains("Goodbye", calls[0]);
        Assert.DoesNotContain("Goodbye", calls[1]);
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmpty_WhenNoQueryHits()
    {
        var resolved = new SongSearchText.Resolved("A", Album: null, "B", TrackNumber: null);

        var results = await CatalogSearchPlanner.SearchAsync<string>(
            resolved, (_, _) => Task.FromResult<IReadOnlyList<string>>([]));

        Assert.Empty(results);
    }
}

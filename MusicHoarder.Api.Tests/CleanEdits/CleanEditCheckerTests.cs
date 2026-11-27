using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MusicHoarder.Api.CleanEdits;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.CleanEdits;

public class CleanEditCheckerTests
{
    private static readonly double[] Words = [14, 20, 26, 32, 38];
    private static readonly float[] Bed = SyntheticSong.Bed(60);

    private static readonly CleanEditSubject Song = new("Kanye West", "Stronger", "USUM70741299", 60, "/music/stronger.flac");

    private static readonly EditionReference ExplicitPreview = new(Edition.Explicit, "deezer:1", "https://p/explicit");
    private static readonly EditionReference CleanPreview = new(Edition.Clean, "itunes:2", "https://p/clean");

    private static float[] Explicit => SyntheticSong.WithWords(Bed, Words, SyntheticSong.ExplicitWordLevel);
    private static float[] Clean => SyntheticSong.WithWords(Bed, Words, SyntheticSong.CleanWordLevel);

    private static CleanEditChecker Checker(float[]? file, FakeReferences references, params (string Url, float[] Pcm)[] previews)
    {
        var audio = previews.ToDictionary(p => p.Url, p => p.Pcm);
        return new CleanEditChecker(new FakeDecoder(file, audio), references, NullLogger<CleanEditChecker>.Instance);
    }

    private static FakeReferences Catalog(params EditionReference[] found) => new(new ReferenceLookup(found, false));

    private static (string Url, float[] Pcm)[] Previews() =>
    [
        (ExplicitPreview.PreviewUrl, SyntheticSong.Excerpt(Explicit, 10, 30)),
        (CleanPreview.PreviewUrl, SyntheticSong.Excerpt(Clean, 10, 30)),
    ];

    [Fact]
    public async Task A_clean_master_is_found_and_the_evidence_names_the_previews_it_rests_on()
    {
        var outcome = await Checker(Clean, Catalog(ExplicitPreview, CleanPreview), Previews())
            .CheckAsync(Song, CancellationToken.None);

        Assert.Equal(CleanEditVerdict.CleanMaster, outcome.Verdict);
        Assert.True(outcome.Margin >= 0.4);
        Assert.True(outcome.EditedSeconds > 0);
        var stored = CleanEditEvidence.FromJson(outcome.Evidence!.ToJson())!;
        Assert.Equal(["deezer:1", "itunes:2"], stored.References.Select(r => r.Id).Order().ToArray());
        Assert.All(stored.References, r => Assert.InRange(r.OffsetSeconds, 9.5, 10.5));
        Assert.Equal("deezer:1~itunes:2", stored.ComparedPair);
    }

    [Fact]
    public async Task An_explicit_file_is_called_explicit()
    {
        var outcome = await Checker(Explicit, Catalog(ExplicitPreview, CleanPreview), Previews())
            .CheckAsync(Song, CancellationToken.None);

        Assert.Equal(CleanEditVerdict.Explicit, outcome.Verdict);
    }

    [Fact]
    public async Task When_the_catalogs_know_the_song_but_have_no_aligned_preview_it_has_no_reference()
    {
        var outcome = await Checker(Explicit, Catalog()).CheckAsync(Song, CancellationToken.None);

        Assert.Equal(CleanEditVerdict.NoReference, outcome.Verdict);
    }

    [Fact]
    public async Task A_catalog_that_could_not_be_asked_is_a_failure_to_retry_not_a_verdict()
    {
        var references = new FakeReferences(new ReferenceLookup([], Incomplete: true));

        var outcome = await Checker(Explicit, references).CheckAsync(Song, CancellationToken.None);

        Assert.Equal(CleanEditVerdict.Failed, outcome.Verdict);
    }

    [Fact]
    public async Task A_preview_that_will_not_download_cannot_turn_an_unsettled_check_into_a_verdict()
    {
        var outcome = await Checker(Explicit, Catalog(ExplicitPreview, CleanPreview),
                (ExplicitPreview.PreviewUrl, SyntheticSong.Excerpt(Explicit, 10, 30)))
            .CheckAsync(Song, CancellationToken.None);

        Assert.Equal(CleanEditVerdict.Failed, outcome.Verdict);
    }

    [Fact]
    public async Task A_decisive_verdict_stands_even_when_a_lookup_was_incomplete()
    {
        var references = new FakeReferences(new ReferenceLookup([ExplicitPreview, CleanPreview], Incomplete: true));

        var outcome = await Checker(Clean, references, Previews()).CheckAsync(Song, CancellationToken.None);

        Assert.Equal(CleanEditVerdict.CleanMaster, outcome.Verdict);
    }

    [Fact]
    public async Task A_file_that_cannot_be_decoded_is_a_failure()
    {
        var outcome = await Checker(null, Catalog(ExplicitPreview, CleanPreview), Previews())
            .CheckAsync(Song, CancellationToken.None);

        Assert.Equal(CleanEditVerdict.Failed, outcome.Verdict);
    }

    [Fact]
    public async Task The_catalogs_are_asked_for_the_length_of_the_file_when_the_song_has_no_duration()
    {
        var references = Catalog(ExplicitPreview, CleanPreview);

        await Checker(Clean, references, Previews()).CheckAsync(Song with { DurationSeconds = null }, CancellationToken.None);

        Assert.Equal(60, references.Asked.Single().DurationSeconds, 1);
    }

    private sealed class FakeReferences(ReferenceLookup lookup) : IEditionReferenceClient
    {
        public List<ReferenceQuery> Asked { get; } = [];

        public Task<ReferenceLookup> FindAsync(ReferenceQuery query, CancellationToken ct)
        {
            Asked.Add(query);
            return Task.FromResult(lookup);
        }

        public Task<byte[]?> DownloadPreviewAsync(string url, CancellationToken ct) =>
            Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(url));
    }

    /// <summary>Decodes the song by its path and a preview by the URL its staged file holds; a URL it has no audio for fails to decode.</summary>
    private sealed class FakeDecoder(float[]? file, Dictionary<string, float[]> previews) : IMonoDecoder
    {
        public async Task<float[]?> DecodeAsync(string path, CancellationToken ct)
        {
            if (path == Song.Path)
                return file;
            return previews.GetValueOrDefault(await File.ReadAllTextAsync(path, ct));
        }
    }
}

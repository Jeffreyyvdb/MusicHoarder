using MusicHoarder.Api.CleanEdits;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Tests.CleanEdits;

public class CleanEditDeciderTests
{
    private const double SongSeconds = 60;
    private const double PreviewFrom = 10;
    private static readonly double[] WordsInPreview = [14, 20, 26, 32, 38];
    private static readonly double[] WordsOutsidePreview = [50, 54];

    private static readonly float[] Bed = SyntheticSong.Bed(SongSeconds);

    private static float[] Explicit(IEnumerable<double> words) => SyntheticSong.WithWords(Bed, words, SyntheticSong.ExplicitWordLevel);
    private static float[] Clean(IEnumerable<double> words) => SyntheticSong.WithWords(Bed, words, SyntheticSong.CleanWordLevel);
    private static float[] Muted(IEnumerable<double> words) => SyntheticSong.WithWords(Bed, words, 0);

    private static CleanEditAnalysis Analyze(float[] file, IEnumerable<double> words, bool explicitPreview = true, bool cleanPreview = true)
    {
        var test = BandSpectrogram.FromPcm(file);
        var references = new List<LocatedReference>();
        if (explicitPreview)
            references.Add(SyntheticSong.Reference(test, Edition.Explicit, "deezer:1", Explicit(words), PreviewFrom));
        if (cleanPreview)
            references.Add(SyntheticSong.Reference(test, Edition.Clean, "itunes:2", Clean(words), PreviewFrom));
        return CleanEditDecider.Analyze(test, references);
    }

    [Fact]
    public void A_file_that_sings_the_words_is_explicit()
    {
        var result = Analyze(Explicit(WordsInPreview), WordsInPreview);

        Assert.Equal(CleanEditVerdict.Explicit, result.Verdict);
        Assert.True(result.Pairwise!.Margin >= CleanEditDecider.DecisiveMargin);
        Assert.Empty(result.MutedWords!.SpanStartSeconds);
    }

    [Fact]
    public void A_file_that_follows_the_clean_edition_is_a_clean_master()
    {
        var result = Analyze(Clean(WordsInPreview), WordsInPreview);

        Assert.Equal(CleanEditVerdict.CleanMaster, result.Verdict);
        Assert.Equal(Edition.Clean, result.Pairwise!.Closer);
        Assert.True(result.Pairwise.EditedSeconds >= WordsInPreview.Length * SyntheticSong.WordSeconds * 0.5);
    }

    [Fact]
    public void A_file_with_its_words_silenced_has_muted_words_even_when_a_clean_edition_exists()
    {
        var result = Analyze(Muted(WordsInPreview), WordsInPreview);

        Assert.Equal(CleanEditVerdict.MutedWords, result.Verdict);
        Assert.Equal(WordsInPreview.Length, result.MutedWords!.SpanStartSeconds.Count);
        Assert.All(
            result.MutedWords.SpanStartSeconds.Zip(WordsInPreview),
            pair => Assert.InRange(pair.First, pair.Second - 0.2, pair.Second + 0.2));
    }

    [Fact]
    public void Another_explicit_mix_that_sings_more_does_not_make_an_exact_match_look_clean_or_muted()
    {
        double[] extraAdLibs = [17, 23, 29];
        var file = Explicit(WordsInPreview);
        var test = BandSpectrogram.FromPcm(file);
        var otherMix = SyntheticSong.Reference(test, Edition.Explicit, "itunes:3", Explicit(WordsInPreview.Concat(extraAdLibs)), PreviewFrom);
        Assert.True(otherMix.Correlation >= 0.85, "the other mix must align tightly enough to be heard");

        var result = CleanEditDecider.Analyze(test,
        [
            SyntheticSong.Reference(test, Edition.Explicit, "deezer:1", file, PreviewFrom),
            otherMix,
            SyntheticSong.Reference(test, Edition.Clean, "itunes:2", Clean(WordsInPreview), PreviewFrom),
        ]);

        Assert.NotEqual(CleanEditVerdict.CleanMaster, result.Verdict);
        Assert.NotEqual(CleanEditVerdict.MutedWords, result.Verdict);
        Assert.Empty(result.MutedWords!.SpanStartSeconds);
    }

    [Fact]
    public void Muted_words_heard_by_every_explicit_preview_count()
    {
        var test = BandSpectrogram.FromPcm(Muted(WordsInPreview));

        var result = CleanEditDecider.Analyze(test,
        [
            SyntheticSong.Reference(test, Edition.Explicit, "deezer:1", Explicit(WordsInPreview), PreviewFrom),
            SyntheticSong.Reference(test, Edition.Explicit, "itunes:3", Explicit(WordsInPreview), PreviewFrom + 5),
        ]);

        Assert.Equal(CleanEditVerdict.MutedWords, result.Verdict);
    }

    [Fact]
    public void An_explicit_preview_that_did_not_align_proves_nothing_even_with_no_clean_edition_sold()
    {
        var test = BandSpectrogram.FromPcm(Muted(WordsInPreview));

        var result = CleanEditDecider.Analyze(test, [], [Edition.Explicit]);

        Assert.Equal(CleanEditVerdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public void A_clean_edition_the_catalogs_list_but_that_did_not_align_leaves_the_verdict_open()
    {
        var test = BandSpectrogram.FromPcm(Explicit(WordsOutsidePreview));

        var result = CleanEditDecider.Analyze(test,
            [SyntheticSong.Reference(test, Edition.Explicit, "deezer:1", Explicit(WordsOutsidePreview), PreviewFrom)],
            [Edition.Explicit, Edition.Clean]);

        Assert.Equal(CleanEditVerdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public void Muted_words_need_no_clean_edition_to_be_found()
    {
        var result = Analyze(Muted(WordsInPreview), WordsInPreview, cleanPreview: false);

        Assert.Equal(CleanEditVerdict.MutedWords, result.Verdict);
    }

    [Fact]
    public void One_muted_word_is_not_enough_to_call_a_file_muted()
    {
        var result = Analyze(SyntheticSong.WithWords(Bed, WordsInPreview.Skip(1), SyntheticSong.ExplicitWordLevel), WordsInPreview);

        Assert.NotEqual(CleanEditVerdict.MutedWords, result.Verdict);
    }

    [Fact]
    public void Editions_that_only_differ_outside_the_preview_window_settle_nothing()
    {
        var result = Analyze(Explicit(WordsOutsidePreview), WordsOutsidePreview);

        Assert.Equal(CleanEditVerdict.Inconclusive, result.Verdict);
    }

    [Fact]
    public void An_explicit_only_release_has_no_clean_edition_to_confuse_the_file_with()
    {
        var result = Analyze(Explicit(WordsInPreview), WordsInPreview, cleanPreview: false);

        Assert.Equal(CleanEditVerdict.NoCleanEdition, result.Verdict);
    }

    [Fact]
    public void Without_an_explicit_preview_there_is_nothing_to_compare_with()
    {
        Assert.Equal(CleanEditVerdict.NoReference, Analyze(Explicit(WordsInPreview), WordsInPreview, explicitPreview: false, cleanPreview: false).Verdict);
        Assert.Equal(CleanEditVerdict.NoReference, Analyze(Clean(WordsInPreview), WordsInPreview, explicitPreview: false).Verdict);
    }

    [Fact]
    public void A_preview_is_found_where_it_was_cut_from()
    {
        var test = BandSpectrogram.FromPcm(Explicit(WordsInPreview));
        var preview = BandSpectrogram.FromPcm(SyntheticSong.Excerpt(Explicit(WordsInPreview), PreviewFrom, 30));

        var alignment = EditionAligner.Locate(test, preview)!.Value;

        var expectedFrame = PreviewFrom * SyntheticSong.Rate / BandSpectrogram.Hop;
        Assert.InRange(alignment.OffsetFrames, expectedFrame - 1.5, expectedFrame + 1.5);
        Assert.True(alignment.Correlation > 0.95);
    }

    [Fact]
    public void A_different_song_is_not_believed_to_be_in_the_file()
    {
        var test = BandSpectrogram.FromPcm(Explicit(WordsInPreview));
        var other = BandSpectrogram.FromPcm(SyntheticSong.Excerpt(SyntheticSong.Bed(SongSeconds, seed: 99), PreviewFrom, 30));

        var alignment = EditionAligner.Locate(test, other)!.Value;

        Assert.True(alignment.Correlation < EditionAligner.MinimumCorrelation);
    }

    [Fact]
    public void A_preview_longer_than_the_file_cannot_be_located()
    {
        var shortFile = BandSpectrogram.FromPcm(SyntheticSong.Excerpt(Bed, 0, 10));
        var preview = BandSpectrogram.FromPcm(SyntheticSong.Excerpt(Bed, 0, 30));

        Assert.Null(EditionAligner.Locate(shortFile, preview));
    }
}

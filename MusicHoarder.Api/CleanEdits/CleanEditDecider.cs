using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.CleanEdits;

/// <summary>What the two tests and the available previews say about one file.</summary>
internal sealed record CleanEditAnalysis(
    CleanEditVerdict Verdict,
    PairwiseResult? Pairwise,
    MutedWordsResult? MutedWords);

/// <summary>Turns the two tests into one verdict, by an ordered table: the first rule that fits wins.</summary>
internal static class CleanEditDecider
{
    /// <summary>The pairwise test is believed only when the file is this much closer to one edition.</summary>
    public const double DecisiveMargin = 0.4;
    public const int MutedSpansForVerdict = 2;

    /// <summary>
    /// <see cref="ListsExplicit"/> and <see cref="ListsClean"/> are what the catalogs list, aligned or
    /// not: a listed edition whose preview did not align leaves the question open rather than saying
    /// the edition does not exist.
    /// </summary>
    private sealed record Facts(
        bool ListsExplicit,
        bool ListsClean,
        int MutedSpans,
        Edition? Closer,
        double Margin,
        bool Contested);

    private static readonly (Func<Facts, bool> Applies, CleanEditVerdict Verdict)[] Rules =
    [
        (f => f.MutedSpans >= MutedSpansForVerdict, CleanEditVerdict.MutedWords),
        (f => f.Contested, CleanEditVerdict.Inconclusive),
        (f => f is { Closer: Edition.Clean, Margin: >= DecisiveMargin }, CleanEditVerdict.CleanMaster),
        (f => f is { Closer: Edition.Explicit, Margin: >= DecisiveMargin }, CleanEditVerdict.Explicit),
        (f => !f.ListsExplicit, CleanEditVerdict.NoReference),
        (f => !f.ListsClean, CleanEditVerdict.NoCleanEdition),
        (_ => true, CleanEditVerdict.Inconclusive),
    ];

    /// <param name="listed">The editions the catalogs listed for the song; by default those that aligned.</param>
    public static CleanEditAnalysis Analyze(
        BandSpectrogram test, IReadOnlyList<LocatedReference> references, IReadOnlyCollection<Edition>? listed = null)
    {
        var hasExplicit = references.Any(r => r.Edition == Edition.Explicit);
        var hasClean = references.Any(r => r.Edition == Edition.Clean);

        var pairwise = hasExplicit && hasClean ? EditionComparison.Pairwise(test, references, DecisiveMargin) : null;
        var muted = hasExplicit ? EditionComparison.MutedWords(test, references) : null;

        var facts = new Facts(
            hasExplicit || listed?.Contains(Edition.Explicit) == true,
            hasClean || listed?.Contains(Edition.Clean) == true,
            muted?.SpanStartSeconds.Count ?? 0,
            pairwise?.Closer,
            pairwise?.Margin ?? 0,
            pairwise?.Contested ?? false);
        var verdict = Rules.First(rule => rule.Applies(facts)).Verdict;
        return new CleanEditAnalysis(verdict, pairwise, muted);
    }
}

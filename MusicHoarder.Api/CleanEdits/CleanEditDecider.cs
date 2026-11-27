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

    private sealed record Facts(
        bool HasExplicit,
        bool HasClean,
        int MutedSpans,
        Edition? Closer,
        double Margin);

    private static readonly (Func<Facts, bool> Applies, CleanEditVerdict Verdict)[] Rules =
    [
        (f => f.MutedSpans >= MutedSpansForVerdict, CleanEditVerdict.MutedWords),
        (f => f is { Closer: Edition.Clean, Margin: >= DecisiveMargin }, CleanEditVerdict.CleanMaster),
        (f => f is { Closer: Edition.Explicit, Margin: >= DecisiveMargin }, CleanEditVerdict.Explicit),
        (f => !f.HasExplicit, CleanEditVerdict.NoReference),
        (f => !f.HasClean, CleanEditVerdict.NoCleanEdition),
        (_ => true, CleanEditVerdict.Inconclusive),
    ];

    public static CleanEditAnalysis Analyze(BandSpectrogram test, IReadOnlyList<LocatedReference> references)
    {
        var hasExplicit = references.Any(r => r.Edition == Edition.Explicit);
        var hasClean = references.Any(r => r.Edition == Edition.Clean);

        var pairwise = hasExplicit && hasClean ? EditionComparison.Pairwise(test, references) : null;
        var muted = hasExplicit ? EditionComparison.MutedWords(test, references) : null;

        var facts = new Facts(
            hasExplicit,
            hasClean,
            muted?.SpanStartSeconds.Count ?? 0,
            pairwise?.Closer,
            pairwise?.Margin ?? 0);
        var verdict = Rules.First(rule => rule.Applies(facts)).Verdict;
        return new CleanEditAnalysis(verdict, pairwise, muted);
    }
}

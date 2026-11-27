namespace MusicHoarder.Api.CleanEdits;

internal enum Edition
{
    Explicit,
    Clean,
}

/// <summary>A preview of one edition, found at <see cref="OffsetFrames"/> inside the song.</summary>
internal sealed record LocatedReference(
    Edition Edition,
    string Id,
    BandSpectrogram Bands,
    int OffsetFrames,
    double Correlation);

/// <summary>
/// Which of an explicit and a clean preview the file follows where those two differ.
/// <see cref="Contested"/>: another pair decisively says the other edition.
/// </summary>
internal sealed record PairwiseResult(
    string ExplicitId,
    string CleanId,
    double EditedSeconds,
    Edition? Closer,
    double Margin,
    bool Contested = false);

/// <summary>Spans where the file is silent but an explicit preview is singing.</summary>
internal sealed record MutedWordsResult(string ReferenceId, IReadOnlyList<double> SpanStartSeconds);

internal static class EditionComparison
{
    private const int MinimumOverlapFrames = 300;
    private const int SmoothingFrames = 5;
    private const double MinimumEditDb = 4.0;
    private const double EditMedianFactor = 4.0;
    private const double MinimumEditedSeconds = 0.1;

    /// <summary>
    /// An edit changes a few words. Two previews that differ over more of their overlap than this are
    /// different mixes or masters, and comparing a file with them says nothing about edits.
    /// </summary>
    private const double MaximumEditedShare = 0.25;

    /// <summary>Muted-word detection trusts only a tight alignment.</summary>
    private const double MutedMinimumCorrelation = 0.85;
    private const int VocalFirstBand = 3;
    private const int VocalLastBand = 17;
    private const double MutedDropDb = -12.0;
    private const double VocalFloorPercentile = 30;
    private const int MinimumMutedFrames = 5;

    /// <summary>A loud frame this short inside a mute is a flicker across the threshold, not a word.</summary>
    private const int MaximumFlickerFrames = 5;

    /// <summary>
    /// Over every explicit and clean preview that overlap, finds the frames where the two previews
    /// differ (the edited words) and asks which one the file is closer to there. The pair with the
    /// most edited audio decides, unless pairs that each pass <paramref name="decisiveMargin"/> disagree:
    /// another master or mix of the explicit edition differs from the clean one in more places, and
    /// can make an exact explicit file look clean. Null when no pair overlaps enough to compare.
    /// </summary>
    public static PairwiseResult? Pairwise(BandSpectrogram test, IReadOnlyList<LocatedReference> references, double decisiveMargin)
    {
        var results = (
            from explicitRef in references.Where(r => r.Edition == Edition.Explicit)
            from cleanRef in references.Where(r => r.Edition == Edition.Clean)
            select ComparePair(test, explicitRef, cleanRef)).OfType<PairwiseResult>().ToList();
        if (results.Count == 0)
            return null;

        var best = results.MaxBy(r => r.EditedSeconds)!;
        var decisive = results.Where(r => r.Closer is not null && r.Margin >= decisiveMargin).Select(r => r.Closer).Distinct().Count();
        return decisive > 1 ? best with { Contested = true } : best;
    }

    private static PairwiseResult? ComparePair(BandSpectrogram test, LocatedReference explicitRef, LocatedReference cleanRef)
    {
        var from = Math.Max(Math.Max(explicitRef.OffsetFrames, cleanRef.OffsetFrames), 0);
        var to = Math.Min(
            Math.Min(explicitRef.OffsetFrames + explicitRef.Bands.Frames, cleanRef.OffsetFrames + cleanRef.Bands.Frames),
            test.Frames);
        if (to - from < MinimumOverlapFrames)
            return null;

        var t = test.Slice(from, to);
        var e = explicitRef.Bands.Slice(from - explicitRef.OffsetFrames, to - explicitRef.OffsetFrames).MatchLevelTo(t);
        var c = cleanRef.Bands.Slice(from - cleanRef.OffsetFrames, to - cleanRef.OffsetFrames).MatchLevelTo(t);

        var editions = Stats.MovingAverage(BandSpectrogram.MeanAbsoluteGap(e, c), SmoothingFrames);
        var toExplicit = Stats.MovingAverage(BandSpectrogram.MeanAbsoluteGap(t, e), SmoothingFrames);
        var toClean = Stats.MovingAverage(BandSpectrogram.MeanAbsoluteGap(t, c), SmoothingFrames);

        var threshold = Math.Max(MinimumEditDb, EditMedianFactor * Stats.Median(editions));
        double explicitSum = 0, cleanSum = 0;
        var edited = 0;
        for (var f = 0; f < editions.Length; f++)
        {
            if (editions[f] <= threshold)
                continue;
            edited++;
            explicitSum += toExplicit[f];
            cleanSum += toClean[f];
        }

        if (edited > MaximumEditedShare * editions.Length)
            return null;

        var editedSeconds = edited * BandSpectrogram.FrameSeconds;
        var rounded = Math.Round(editedSeconds, 2);
        if (editedSeconds < MinimumEditedSeconds)
            return new PairwiseResult(explicitRef.Id, cleanRef.Id, rounded, null, 0);

        var distanceToExplicit = explicitSum / edited;
        var distanceToClean = cleanSum / edited;
        var margin = Math.Abs(distanceToExplicit - distanceToClean) / Math.Max(distanceToExplicit, distanceToClean);
        return new PairwiseResult(
            explicitRef.Id,
            cleanRef.Id,
            rounded,
            distanceToClean < distanceToExplicit ? Edition.Clean : Edition.Explicit,
            Math.Round(margin, 2));
    }

    /// <summary>
    /// Words the file has muted against an explicit preview: runs of frames where the file's vocal
    /// bands sit far below the preview's while the preview has vocals there. This catches a mute that
    /// differs from the label's clean edit, which the pairwise test cannot see. A run counts only when
    /// every other tightly aligned explicit preview covering it also hears the file go quiet there, so
    /// a different master or mix of the explicit edition cannot invent mutes the file's own match does
    /// not show. The preview with the most such runs wins. Null when no explicit preview is aligned
    /// tightly enough.
    /// </summary>
    public static MutedWordsResult? MutedWords(BandSpectrogram test, IReadOnlyList<LocatedReference> references)
    {
        var heard = references
            .Where(r => r.Edition == Edition.Explicit && r.Correlation >= MutedMinimumCorrelation)
            .Select(r => QuietAgainst(test, r))
            .OfType<QuietFrames>()
            .ToList();

        MutedWordsResult? best = null;
        foreach (var candidate in heard)
        {
            var starts = candidate.Runs()
                .Where(run => heard.All(other => other == candidate || other.Agrees(run.Start, run.End)))
                .Select(run => Math.Round(run.Start * BandSpectrogram.FrameSeconds, 1))
                .ToList();
            var result = new MutedWordsResult(candidate.ReferenceId, starts);
            if (best is null || result.SpanStartSeconds.Count > best.SpanStartSeconds.Count)
                best = result;
        }
        return best;
    }

    /// <summary>Where, in the file's own frames, it is quiet against one explicit preview.</summary>
    private sealed record QuietFrames(string ReferenceId, int From, bool[] Quiet)
    {
        private const int Slack = 2;

        /// <summary>
        /// Runs of quiet frames, joined across a loud flicker of up to <see cref="MaximumFlickerFrames"/>.
        /// A run counts when its quiet frames, not its extent, reach <see cref="MinimumMutedFrames"/>,
        /// and they are at least half of it.
        /// </summary>
        public IEnumerable<(int Start, int End)> Runs()
        {
            int runStart = -1, runEnd = -1, quietFrames = 0;
            for (var f = 0; f < Quiet.Length; f++)
            {
                if (!Quiet[f])
                    continue;
                if (runStart >= 0 && f - runEnd > MaximumFlickerFrames)
                {
                    if (Counts(runStart, runEnd, quietFrames))
                        yield return (From + runStart, From + runEnd);
                    runStart = -1;
                }
                if (runStart < 0)
                    (runStart, quietFrames) = (f, 0);
                runEnd = f + 1;
                quietFrames++;
            }
            if (runStart >= 0 && Counts(runStart, runEnd, quietFrames))
                yield return (From + runStart, From + runEnd);
        }

        // Mostly quiet, as Agrees asks of another preview, so a sparse chain of flickers is no word.
        private static bool Counts(int start, int end, int quietFrames) =>
            quietFrames >= MinimumMutedFrames && 2 * quietFrames >= end - start;

        /// <summary>
        /// True when this preview does not cover the run, or hears the file go quiet for at least half
        /// of it too. A lone quiet frame is a framing blip, not agreement.
        /// </summary>
        public bool Agrees(int start, int end)
        {
            if (start < From || end > From + Quiet.Length)
                return true;
            var quiet = 0;
            for (var f = Math.Max(start - Slack, From); f < Math.Min(end + Slack, From + Quiet.Length); f++)
            {
                if (Quiet[f - From])
                    quiet++;
            }
            return 2 * quiet >= end - start;
        }
    }

    private static QuietFrames? QuietAgainst(BandSpectrogram test, LocatedReference reference)
    {
        var from = Math.Max(reference.OffsetFrames, 0);
        var to = Math.Min(reference.OffsetFrames + reference.Bands.Frames, test.Frames);
        if (to - from < MinimumOverlapFrames)
            return null;

        var t = test.Slice(from, to);
        var e = reference.Bands.Slice(from - reference.OffsetFrames, to - reference.OffsetFrames).MatchLevelTo(t);
        var testVocals = t.BandMean(VocalFirstBand, VocalLastBand);
        var referenceVocals = e.BandMean(VocalFirstBand, VocalLastBand);
        var floor = Stats.Percentile(referenceVocals, VocalFloorPercentile);

        var quiet = new bool[t.Frames];
        for (var f = 0; f < t.Frames; f++)
            quiet[f] = testVocals[f] - referenceVocals[f] < MutedDropDb && referenceVocals[f] > floor;
        return new QuietFrames(reference.Id, from, quiet);
    }
}

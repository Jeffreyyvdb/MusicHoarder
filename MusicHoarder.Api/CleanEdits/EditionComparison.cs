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

/// <summary>Which of an explicit and a clean preview the file follows where those two differ.</summary>
internal sealed record PairwiseResult(
    string ExplicitId,
    string CleanId,
    double EditedSeconds,
    Edition? Closer,
    double Margin);

/// <summary>Spans where the file is silent but an explicit preview is singing.</summary>
internal sealed record MutedWordsResult(string ReferenceId, IReadOnlyList<double> SpanStartSeconds);

internal static class EditionComparison
{
    private const int MinimumOverlapFrames = 300;
    private const int SmoothingFrames = 5;
    private const double MinimumEditDb = 4.0;
    private const double EditMedianFactor = 4.0;
    private const double MinimumEditedSeconds = 0.1;

    /// <summary>Muted-word detection trusts only a tight alignment.</summary>
    private const double MutedMinimumCorrelation = 0.85;
    private const int VocalFirstBand = 3;
    private const int VocalLastBand = 17;
    private const double MutedDropDb = -12.0;
    private const double VocalFloorPercentile = 30;
    private const int MinimumMutedFrames = 5;

    /// <summary>
    /// Over every explicit and clean preview that overlap, finds the frames where the two previews
    /// differ (the edited words) and asks which one the file is closer to there. The pair with the
    /// most edited audio decides. Null when no pair overlaps enough to compare.
    /// </summary>
    public static PairwiseResult? Pairwise(BandSpectrogram test, IReadOnlyList<LocatedReference> references)
    {
        PairwiseResult? best = null;
        foreach (var explicitRef in references.Where(r => r.Edition == Edition.Explicit))
        {
            foreach (var cleanRef in references.Where(r => r.Edition == Edition.Clean))
            {
                var result = ComparePair(test, explicitRef, cleanRef);
                if (result is not null && (best is null || result.EditedSeconds > best.EditedSeconds))
                    best = result;
            }
        }
        return best;
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
    /// differs from the label's clean edit, which the pairwise test cannot see. The preview with the
    /// most such runs wins. Null when no explicit preview is aligned tightly enough.
    /// </summary>
    public static MutedWordsResult? MutedWords(BandSpectrogram test, IReadOnlyList<LocatedReference> references)
    {
        MutedWordsResult? best = null;
        foreach (var reference in references)
        {
            if (reference.Edition != Edition.Explicit || reference.Correlation < MutedMinimumCorrelation)
                continue;
            var from = Math.Max(reference.OffsetFrames, 0);
            var to = Math.Min(reference.OffsetFrames + reference.Bands.Frames, test.Frames);
            if (to - from < MinimumOverlapFrames)
                continue;

            var t = test.Slice(from, to);
            var e = reference.Bands.Slice(from - reference.OffsetFrames, to - reference.OffsetFrames).MatchLevelTo(t);
            var testVocals = t.BandMean(VocalFirstBand, VocalLastBand);
            var referenceVocals = e.BandMean(VocalFirstBand, VocalLastBand);
            var floor = Stats.Percentile(referenceVocals, VocalFloorPercentile);

            var starts = new List<double>();
            var runStart = -1;
            for (var f = 0; f <= t.Frames; f++)
            {
                var muted = f < t.Frames
                    && testVocals[f] - referenceVocals[f] < MutedDropDb
                    && referenceVocals[f] > floor;
                if (muted && runStart < 0)
                    runStart = f;
                if (!muted && runStart >= 0)
                {
                    if (f - runStart >= MinimumMutedFrames)
                        starts.Add(Math.Round((from + runStart) * BandSpectrogram.FrameSeconds, 1));
                    runStart = -1;
                }
            }

            var result = new MutedWordsResult(reference.Id, starts);
            if (best is null || result.SpanStartSeconds.Count > best.SpanStartSeconds.Count)
                best = result;
        }
        return best;
    }
}

using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Jobs;

public static class ProgressSnapshotFactory
{
    public static ProgressSnapshot Create(
        JobManager jobManager,
        ScanProgressTracker scanTracker,
        FingerprintProgressTracker fingerprintTracker,
        EnrichmentProgressTracker enrichmentTracker,
        LibraryBuilderProgressTracker buildTracker)
    {
        var scanStep = jobManager.GetStepSnapshot(JobType.Scan);
        var fpStep = jobManager.GetStepSnapshot(JobType.Fingerprint);
        var enrichStep = jobManager.GetStepSnapshot(JobType.Enrich);
        var buildStep = jobManager.GetStepSnapshot(JobType.Build);

        var anyRunning = jobManager.IsAnyRunning();

        var runningLabels = new List<string>();
        if (scanStep.Status == "Running") runningLabels.Add("Scanning");
        if (fpStep.Status == "Running") runningLabels.Add("Fingerprinting");
        if (enrichStep.Status == "Running") runningLabels.Add("Enriching");
        if (buildStep.Status == "Running") runningLabels.Add("Building");

        var statusLabel = runningLabels.Count > 0
            ? string.Join(", ", runningLabels)
            : anyRunning ? "Running" : "Idle";

        var scanState = scanTracker.GetCurrent();
        var fpState = fingerprintTracker.GetCurrent();
        var enrichState = enrichmentTracker.GetCurrent();
        var buildState = buildTracker.GetCurrent();

        var discovered = scanState?.TotalFiles ?? 0;
        var scanned = (scanState?.Processed ?? 0) + (scanState?.SkippedFiles ?? 0);
        var fingerprinted = fpState?.Fingerprinted ?? 0;
        var enriched = enrichState?.Enriched ?? 0;
        var needsReview = enrichState?.NeedsReview ?? 0;
        var built = buildState?.Built ?? 0;
        var failed = (scanState?.FailedFiles ?? 0)
            + (fpState?.Failed ?? 0)
            + (enrichState?.Failed ?? 0)
            + (buildState?.Failed ?? 0);

        return new ProgressSnapshot(
            statusLabel,
            null,
            null,
            null,
            !anyRunning,
            discovered,
            scanned,
            fingerprinted,
            enriched,
            needsReview,
            built,
            failed,
            scanStep,
            fpStep,
            enrichStep,
            buildStep);
    }

    public static bool TryParseJobType(string step, out JobType jobType)
    {
        jobType = step.Trim().ToLowerInvariant() switch
        {
            "scan" => JobType.Scan,
            "fingerprint" => JobType.Fingerprint,
            "enrich" => JobType.Enrich,
            "build" => JobType.Build,
            _ => JobType.None
        };
        return jobType != JobType.None;
    }
}

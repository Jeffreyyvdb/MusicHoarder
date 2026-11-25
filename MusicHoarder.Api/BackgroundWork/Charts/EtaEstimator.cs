namespace MusicHoarder.Api.BackgroundWork.Charts;

public static class EtaEstimator
{
    /// <summary>Longest wait worth a date: further out than this the rate says nothing useful.</summary>
    private const double MaxDays = 3650;

    /// <summary>When <paramref name="backlog"/> runs out at <paramref name="perDay"/>; null without a rate or a backlog.</summary>
    public static DateTime? Eta(double backlog, double perDay, DateTime nowUtc)
    {
        if (backlog <= 0 || perDay <= 0)
            return null;
        var days = backlog / perDay;
        return days > MaxDays ? null : nowUtc.AddDays(days);
    }

    /// <summary>
    /// Work per day over the last <paramref name="windowDays"/>. When the evidence is younger than the window
    /// (a ledger that only started keeping history lately), the total is spread over the days it covers, so a
    /// short record does not read as a slow rate.
    /// </summary>
    public static double RatePerDay(double doneInWindow, DateTime? oldestEvidenceUtc, DateTime nowUtc, int windowDays)
    {
        if (doneInWindow <= 0)
            return 0;
        var covered = oldestEvidenceUtc is { } oldest ? (nowUtc - oldest).TotalDays : windowDays;
        return doneInWindow / Math.Clamp(covered, 1, windowDays);
    }
}

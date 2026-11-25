namespace MusicHoarder.Api.BackgroundWork.Charts;

public static class EtaEstimator
{
    private const double MaxDays = 3650;

    public static DateTime? Eta(double backlog, double perDay, DateTime nowUtc)
    {
        if (backlog <= 0 || perDay <= 0)
            return null;
        var days = backlog / perDay;
        return days > MaxDays ? null : nowUtc.AddDays(days);
    }

    public static double RatePerDay(double doneInWindow, DateTime? oldestEvidenceUtc, DateTime nowUtc, int windowDays)
    {
        if (doneInWindow <= 0)
            return 0;
        var covered = oldestEvidenceUtc is { } oldest ? (nowUtc - oldest).TotalDays : windowDays;
        return doneInWindow / Math.Clamp(covered, 1, windowDays);
    }
}

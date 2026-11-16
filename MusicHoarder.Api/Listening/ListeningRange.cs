namespace MusicHoarder.Api.Listening;

public enum ListeningRange
{
    FourWeeks,
    SixMonths,
    Year,
    Lifetime,
}

public static class ListeningRanges
{
    public static bool TryParse(string? value, out ListeningRange range)
    {
        switch (value)
        {
            case "4w": range = ListeningRange.FourWeeks; return true;
            case "6m": range = ListeningRange.SixMonths; return true;
            case "1y": range = ListeningRange.Year; return true;
            case "all": range = ListeningRange.Lifetime; return true;
            default: range = default; return false;
        }
    }

    public static string Key(this ListeningRange range) => range switch
    {
        ListeningRange.FourWeeks => "4w",
        ListeningRange.SixMonths => "6m",
        ListeningRange.Year => "1y",
        _ => "all",
    };

    /// <summary>The first instant the range covers, or null for lifetime.</summary>
    public static DateTime? FromUtc(this ListeningRange range, DateTime nowUtc) => range switch
    {
        ListeningRange.FourWeeks => nowUtc.AddDays(-28),
        ListeningRange.SixMonths => nowUtc.AddMonths(-6),
        ListeningRange.Year => nowUtc.AddYears(-1),
        _ => null,
    };
}

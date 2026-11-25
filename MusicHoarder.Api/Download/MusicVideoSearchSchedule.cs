using System.Globalization;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Download;

/// <summary>What a candidate search told us about a song.</summary>
public enum MusicVideoSearchOutcome
{
    /// <summary>The search failed or found nothing.</summary>
    Empty,

    /// <summary>Candidates came back, but none passes <see cref="MusicVideoAutoPick"/>'s gates.</summary>
    NoClip,

    /// <summary>At least one candidate passes the gates: a clip could be downloaded as it stands.</summary>
    Clip,
}

/// <summary>
/// When a song's candidates are searched again. A new single's video often comes out weeks after the
/// song, so a recent release is looked at often; an old song with no clip is rarely worth a search.
/// One pure rule, so the prefetch and every writer of a list (<see cref="MusicVideoCandidateCache"/>)
/// agree.
/// </summary>
public static partial class MusicVideoSearchSchedule
{
    /// <summary>A release younger than this is "recent" and looked at more often.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromDays(90);

    private static readonly TimeSpan[] ClipDelay = [TimeSpan.FromDays(30), TimeSpan.FromDays(180)];
    private static readonly TimeSpan[] NoClipDelay = [TimeSpan.FromDays(7), TimeSpan.FromDays(120)];

    [GeneratedRegex(@"^\s*(\d{4})(?:-(\d{1,2})(?:-(\d{1,2}))?)?", RegexOptions.CultureInvariant)]
    private static partial Regex DatePrefix();

    /// <param name="emptySearches">
    /// Consecutive empty searches including this one (1 on the first). Only read for
    /// <see cref="MusicVideoSearchOutcome.Empty"/>, whose wait doubles with each.
    /// </param>
    public static DateTime NextSearchAt(
        MusicVideoSearchOutcome outcome, bool isRecentRelease, int emptySearches, DateTime now)
    {
        var column = isRecentRelease ? 0 : 1;
        return now + outcome switch
        {
            MusicVideoSearchOutcome.Clip => ClipDelay[column],
            MusicVideoSearchOutcome.NoClip => NoClipDelay[column],
            _ => EmptyDelay(isRecentRelease, emptySearches),
        };
    }

    private static TimeSpan EmptyDelay(bool isRecentRelease, int emptySearches)
    {
        var (first, max) = isRecentRelease
            ? (TimeSpan.FromDays(1), TimeSpan.FromDays(14))
            : (TimeSpan.FromDays(7), TimeSpan.FromDays(180));
        // Capped before shifting so a long run of empty searches cannot overflow.
        var doublings = Math.Clamp(emptySearches - 1, 0, 10);
        var delay = first * (1 << doublings);
        return delay < max ? delay : max;
    }

    /// <summary>
    /// Whether a song is a recent release. A partial date counts as its last day, so a bare
    /// current year is recent: it may well be last week. Without a parseable release date the year
    /// stands in (about half the library has only a year), and without either, the day the song was
    /// added.
    /// </summary>
    public static bool IsRecentRelease(string? releaseDate, int? year, DateTime? acquiredAtUtc, DateTime now)
    {
        var since = now - RecentWindow;
        if (TryParseLastDay(releaseDate, out var lastDay))
            return lastDay >= since;
        if (year is > 0 and <= 9999)
            return new DateTime(year.Value, 12, 31, 0, 0, 0, DateTimeKind.Utc) >= since;
        return acquiredAtUtc is { } acquired && acquired >= since;
    }

    /// <summary>The last day a <c>yyyy</c>, <c>yyyy-MM</c> or <c>yyyy-MM-dd</c> date can mean.</summary>
    internal static bool TryParseLastDay(string? text, out DateTime lastDay)
    {
        lastDay = default;
        if (string.IsNullOrWhiteSpace(text) || DatePrefix().Match(text) is not { Success: true } m)
            return false;

        var year = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (year < 1)
            return false;

        if (!m.Groups[2].Success)
        {
            lastDay = new DateTime(year, 12, 31, 0, 0, 0, DateTimeKind.Utc);
            return true;
        }

        var month = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (month is < 1 or > 12)
            return false;

        if (!m.Groups[3].Success)
        {
            lastDay = new DateTime(year, month, DateTime.DaysInMonth(year, month), 0, 0, 0, DateTimeKind.Utc);
            return true;
        }

        var day = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (day < 1 || day > DateTime.DaysInMonth(year, month))
            return false;
        lastDay = new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc);
        return true;
    }
}

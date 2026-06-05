namespace MusicHoarder.Api.Version;

/// <summary>
/// Decides whether a newer release is available by comparing the running build's version against the
/// latest published one. Deliberately conservative: anything it can't confidently parse as "latest is
/// strictly newer" returns <c>false</c>, so an ambiguous comparison never nags the user with a banner.
/// </summary>
public static class SemVerComparer
{
    /// <summary>
    /// True only when <paramref name="latest"/> parses as a strictly higher version than
    /// <paramref name="current"/>. Returns false when either side is missing/unparseable, when
    /// <paramref name="current"/> is the local <c>"dev"</c> sentinel, or when they're equal — all
    /// fail-safe to "no update".
    /// </summary>
    public static bool IsUpdateAvailable(string? current, string? latest)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (string.IsNullOrWhiteSpace(current)) return false;

        // "dev" (the assembly-version fallback for local builds) is never behind a release.
        if (string.Equals(current.Trim(), "dev", StringComparison.OrdinalIgnoreCase)) return false;

        var currentVersion = Parse(current);
        var latestVersion = Parse(latest);
        if (currentVersion is null || latestVersion is null) return false;

        return latestVersion > currentVersion;
    }

    /// <summary>
    /// Parses a clean <c>X.Y.Z</c> (or <c>X.Y</c>) tag into a <see cref="System.Version"/>, tolerating a
    /// leading <c>v</c> and stripping any pre-release (<c>-rc1</c>) / build-metadata (<c>+sha</c>) suffix.
    /// Returns null on anything else (fail-safe = no comparison).
    /// </summary>
    private static System.Version? Parse(string raw)
    {
        var value = raw.Trim();
        if (value.Length > 0 && (value[0] == 'v' || value[0] == 'V'))
            value = value[1..];

        // Drop a "-prerelease" or "+build" suffix so System.Version.TryParse sees only the numeric core.
        var cut = value.IndexOfAny(['-', '+']);
        if (cut >= 0)
            value = value[..cut];

        return System.Version.TryParse(value, out var parsed) ? parsed : null;
    }
}

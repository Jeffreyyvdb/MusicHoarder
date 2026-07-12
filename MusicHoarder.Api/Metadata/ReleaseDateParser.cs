using System.Globalization;

namespace MusicHoarder.Api.Metadata;

/// <summary>
/// Single shared normalizer for the "release year out of a provider release-date string" rule.
/// Catalog providers (Spotify, Deezer, Apple Music, MusicBrainz) all return a release date as a
/// leading-year string — "2019", "2019-03-15", "2019-03" — so the year is the first four characters.
/// Parsing is invariant-culture and the result is range-checked to a plausible window so a malformed
/// or partial date can never surface a nonsense year (e.g. <c>50</c> from <c>"0050-01-01"</c>).
/// </summary>
public static class ReleaseDateParser
{
    /// <summary>
    /// Extracts the release year from a provider release-date string, or <c>null</c> when the value
    /// is missing or does not begin with a plausible four-digit year (1001–2999).
    /// </summary>
    public static int? ParseYear(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate))
            return null;

        var head = releaseDate.Length >= 4 ? releaseDate[..4] : releaseDate;
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
            && year is > 1000 and < 3000
            ? year
            : null;
    }

    /// <summary>
    /// Normalizes a provider release-date string to a clean ISO value: trimmed, any time component
    /// dropped, and validated to begin with a plausible year. Returns the leading <c>YYYY[-MM[-DD]]</c>
    /// portion (whatever precision the source carried) or <c>null</c> when the value is missing or its
    /// leading four characters aren't a plausible year. Keeps partial dates rather than forcing a full one.
    /// </summary>
    public static string? Normalize(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate))
            return null;

        // Drop any time component ("2019-03-15T00:00:00Z" → "2019-03-15") and surrounding whitespace.
        var value = releaseDate.Trim();
        var tIndex = value.IndexOf('T');
        if (tIndex > 0)
            value = value[..tIndex];
        value = value.Trim();

        // Must begin with a plausible year to be meaningful.
        return ParseYear(value) is null ? null : value;
    }
}

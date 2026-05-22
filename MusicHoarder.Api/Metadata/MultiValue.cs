namespace MusicHoarder.Api.Metadata;

/// <summary>
/// Shared semantics for the ';'-delimited multi-value string columns (Artists, ArtistMusicBrainzIds,
/// ReleaseTypes). Keeping split/join in one place ensures the enrichment merger and the tag writer
/// agree on the delimiter. ';' is the only safe delimiter for artist names — '/'/'&'/',' all occur
/// in legitimate names (AC/DC, Simon &amp; Garfunkel, Earth, Wind &amp; Fire).
/// </summary>
public static class MultiValue
{
    public const char Delimiter = ';';

    /// <summary>Splits a stored multi-value string into its trimmed, non-empty parts.</summary>
    public static string[] Split(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(Delimiter, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();
    }

    /// <summary>Joins discrete values into the stored "a; b; c" form, or null when there's nothing.</summary>
    public static string? Join(IEnumerable<string?>? values)
    {
        if (values is null)
        {
            return null;
        }

        var parts = values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .ToArray();

        return parts.Length == 0 ? null : string.Join("; ", parts);
    }
}

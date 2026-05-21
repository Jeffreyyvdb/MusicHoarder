using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Matching;

/// <summary>
/// Decides whether an existing tag value is "low quality" — i.e. junk the pipeline may
/// safely replace, rather than a curated value that must be defended. Deliberately
/// conservative: a value is only low-quality when it is clearly placeholder/garbage,
/// so genuine (if terse) curated tags are never clobbered.
/// </summary>
public static partial class MetadataQualityHeuristics
{
    public static bool IsLowQuality(string? value, string? fileName = null)
    {
        if (string.IsNullOrWhiteSpace(value))
            return true;

        var trimmed = value.Trim();

        // Equals the file name (with or without extension) → scanner fell back to filename.
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            if (string.Equals(trimmed, fileName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, baseName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (PlaceholderPattern().IsMatch(trimmed))
            return true;

        // Bare track designators / pure numbers: "Track 03", "03", "Track03".
        if (TrackNumberPattern().IsMatch(trimmed))
            return true;

        // Contains a URL / scene-release noise.
        if (UrlPattern().IsMatch(trimmed))
            return true;

        return false;
    }

    [GeneratedRegex(@"^(unknown(\s+(artist|album|title))?|untitled|various\s+artists|n/?a)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^(track\s*)?\d{1,3}$", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex TrackNumberPattern();

    [GeneratedRegex(@"(https?://|www\.)", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();
}

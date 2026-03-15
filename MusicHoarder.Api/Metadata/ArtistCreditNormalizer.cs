using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Metadata;

public static partial class ArtistCreditNormalizer
{
    public static string? NormalizeDisplayCredit(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = MultiSpaceRegex().Replace(value.Trim(), " ");
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    public static string? GetPrimaryArtist(string? artistCredit)
    {
        var normalized = NormalizeDisplayCredit(artistCredit);
        if (normalized is null)
        {
            return null;
        }

        var candidates = SplitArtists(normalized);
        return candidates.Count > 0 ? candidates[0] : normalized;
    }

    private static List<string> SplitArtists(string artistCredit)
    {
        if (artistCredit.Contains(';'))
        {
            return artistCredit.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        var featSplit = FeatRegex().Split(artistCredit);
        if (featSplit.Length > 1)
        {
            return featSplit.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
        }

        if (artistCredit.Contains(" & ", StringComparison.OrdinalIgnoreCase))
        {
            return artistCredit.Split(" & ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        if (artistCredit.Contains(" x ", StringComparison.OrdinalIgnoreCase))
        {
            return artistCredit.Split(" x ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        if (artistCredit.Contains(", "))
        {
            return artistCredit.Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }

        return [artistCredit];
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\s+(feat\.|ft\.|featuring|with)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex FeatRegex();
}

using System.Text.RegularExpressions;
using MusicHoarder.Api.Matching;

namespace MusicHoarder.Api.Soulseek;

/// <summary>
/// Builds a Soulseek-friendly search string. Soulseek matches loosely on peers' shared folder/file
/// names, so an over-specified query returns nothing while a few clean tokens hit broadly:
/// <list type="bullet">
/// <item><b>Primary artist only</b> — peers name files with a single artist, so a full multi-artist
/// credit ("Beyoncé, JAY-Z, Kanye West") over-constrains to zero results.</item>
/// <item><b>Normalized title/album</b> — strips featured credits, parentheticals, brackets
/// ("(feat. …)", "[Platinum Edition]"), punctuation, and diacritics via
/// <see cref="TitleNormalizer.NormalizeForSearch"/>.</item>
/// </list>
/// The candidate selector re-filters strictly (title tokens + duration + extension) afterward, so a
/// broad search here is safe.
/// </summary>
public static partial class SoulseekSearchQuery
{
    public static string Build(string? artist, string? term)
    {
        var cleanArtist = TitleNormalizer.NormalizeForSearch(FirstArtist(artist));
        var cleanTerm = TitleNormalizer.NormalizeForSearch(term);
        return string.Join(' ', new[] { cleanArtist, cleanTerm }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>
    /// The first credited artist — split on the EARLIEST separator (comma / semicolon / slash /
    /// &amp; / x / feat / ft / featuring), unlike <c>ArtistCreditNormalizer.GetPrimaryArtist</c> which
    /// applies a fixed precedence (that leaves "A, B, C &amp; D" as the whole comma list). Broad by
    /// design — the candidate selector re-filters by title tokens + duration afterward.
    /// </summary>
    internal static string? FirstArtist(string? artistCredit)
    {
        if (string.IsNullOrWhiteSpace(artistCredit))
            return null;
        var first = ArtistSeparators().Split(artistCredit).FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
        return first?.Trim();
    }

    [GeneratedRegex(@"\s*[,;/]\s*|\s+(?:&|x|feat\.?|ft\.?|featuring)\s+", RegexOptions.IgnoreCase)]
    private static partial Regex ArtistSeparators();
}

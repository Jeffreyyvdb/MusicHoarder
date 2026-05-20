using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Matching;

/// <summary>
/// Single shared normalizer for artist/title strings used across all enrichment
/// providers and library comparison. Strips parenthetical/bracket qualifiers,
/// featuring credits, and punctuation; folds diacritics and common Cyrillic/Latin
/// lookalikes so "Beyoncé" and "Beyonce", or "KoЯn" and "Korn", compare equal.
/// </summary>
public static partial class TitleNormalizer
{
    /// <summary>
    /// Produces a normalized form suitable for fuzzy comparison and search queries.
    /// </summary>
    public static string NormalizeForSearch(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        var result = value.ToLowerInvariant();
        result = ParenthesesPattern().Replace(result, "");
        result = BracketsPattern().Replace(result, "");
        result = FeaturingPattern().Replace(result, "");
        result = FoldDiacritics(result);
        result = PunctuationPattern().Replace(result, "");
        result = WhitespacePattern().Replace(result, " ");
        return result.Trim();
    }

    /// <summary>
    /// Folds combining diacritical marks (é → e) and a small set of common
    /// Cyrillic/Latin lookalike glyphs to their Latin equivalents.
    /// </summary>
    public static string FoldDiacritics(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var mapped = MapLookalikes(value);
        var decomposed = mapped.Normalize(NormalizationForm.FormKD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string MapLookalikes(string value)
    {
        if (!ContainsNonAscii(value))
            return value;

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(Lookalikes.TryGetValue(ch, out var latin) ? latin : ch);
        return sb.ToString();
    }

    private static bool ContainsNonAscii(string value)
    {
        foreach (var ch in value)
        {
            if (ch > 127)
                return true;
        }

        return false;
    }

    // Common Cyrillic homoglyphs plus the band-stylization Я/я → r.
    private static readonly Dictionary<char, char> Lookalikes = new()
    {
        ['А'] = 'A', ['В'] = 'B', ['Е'] = 'E', ['К'] = 'K', ['М'] = 'M',
        ['Н'] = 'H', ['О'] = 'O', ['Р'] = 'P', ['С'] = 'C', ['Т'] = 'T',
        ['Х'] = 'X', ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p',
        ['с'] = 'c', ['у'] = 'y', ['х'] = 'x', ['Я'] = 'R', ['я'] = 'r',
    };

    [GeneratedRegex(@"\(.*?\)", RegexOptions.Compiled)]
    private static partial Regex ParenthesesPattern();

    [GeneratedRegex(@"\[.*?\]", RegexOptions.Compiled)]
    private static partial Regex BracketsPattern();

    [GeneratedRegex(@"\b(feat\.?|ft\.?)\s.*", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
    private static partial Regex FeaturingPattern();

    [GeneratedRegex(@"[^\w\s]", RegexOptions.Compiled)]
    private static partial Regex PunctuationPattern();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    private static partial Regex WhitespacePattern();
}

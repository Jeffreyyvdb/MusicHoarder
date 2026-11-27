using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.CleanEdits;

/// <summary>How a catalog title is compared with a song's: a reference only counts when the two come out equal.</summary>
internal static partial class ReferenceTitle
{
    /// <summary>
    /// Lowercase words without accents, in any script, with feature credits and edition labels in
    /// brackets dropped. Empty for a title with no letters or digits, which matches nothing.
    /// </summary>
    public static string Normalize(string title) => Words(EditionLabel().Replace(Fold(title), " "));

    /// <summary>
    /// Whether a catalog's artist credit names the song's lead artist, or the other way round: a title
    /// and a length alone also match other artists' songs called "Intro" or "Home".
    /// </summary>
    public static bool SameArtist(string songArtist, string? catalogArtist)
    {
        var lead = Words(Fold(LeadArtist(songArtist)));
        var credit = Words(Fold(catalogArtist ?? ""));
        return lead.Length > 0 && credit.Length > 0
            && ($" {credit} ".Contains($" {lead} ", StringComparison.Ordinal) || $" {lead} ".Contains($" {credit} ", StringComparison.Ordinal));
    }

    private static string Fold(string text)
    {
        var folded = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormKD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                folded.Append(char.ToLowerInvariant(c));
        }
        return folded.ToString();
    }

    private static string Words(string text) => NonWord().Replace(text, " ").Trim();

    /// <summary>The title with every bracketed part removed, for a catalog search.</summary>
    public static string WithoutBrackets(string title) => Brackets().Replace(title, "");

    /// <summary>The first credited artist, which is how catalogs file a collaboration.</summary>
    public static string LeadArtist(string artist) => artist.Split(',')[0];

    [GeneratedRegex(@"[\(\[].*?(feat|with|ft\.|clean|explicit|edit|version).*?[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex EditionLabel();

    [GeneratedRegex(@"[^\p{L}\p{N}]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWord();

    [GeneratedRegex(@"[\(\[].*?[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();
}

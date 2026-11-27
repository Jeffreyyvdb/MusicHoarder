using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.CleanEdits;

/// <summary>How a catalog title is compared with a song's: a reference only counts when the two come out equal.</summary>
internal static partial class ReferenceTitle
{
    /// <summary>
    /// Lowercase words in any script, with the accents of Latin, Greek and Cyrillic letters and the
    /// feature credits and edition labels in brackets dropped. Empty for a title with no letters or
    /// digits, which matches nothing.
    /// </summary>
    public static string Normalize(string title) => Words(EditionLabel().Replace(Fold(title), " "));

    /// <summary>
    /// Whether a catalog's artist credit names the song's lead artist as whole words: a title and a
    /// length alone also match other artists' songs called "Intro" or "Home". A credit that only names
    /// part of the lead ("Jackson" for "Michael Jackson") is another artist.
    /// </summary>
    public static bool SameArtist(string songArtist, string? catalogArtist)
    {
        var lead = Words(Fold(LeadArtist(songArtist)));
        var credit = Words(Fold(catalogArtist ?? ""));
        return lead.Length > 0 && $" {credit} ".Contains($" {lead} ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Lowercased by Unicode scalar, so letters outside the Basic Multilingual Plane survive. Only an
    /// accent on a Latin, Greek or Cyrillic letter is dropped: in scripts such as Devanagari a mark is
    /// part of the word ("दिन" and "दीन" are two words).
    /// </summary>
    private static string Fold(string text)
    {
        var folded = new StringBuilder();
        var baseIsAccentable = false;
        foreach (var rune in text.Normalize(NormalizationForm.FormKD).EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) == UnicodeCategory.NonSpacingMark)
            {
                if (!baseIsAccentable)
                    folded.Append(rune.ToString());
                continue;
            }
            baseIsAccentable = rune.Value < LastCyrillic;
            folded.Append(Rune.ToLowerInvariant(rune).ToString());
        }
        return folded.ToString();
    }

    private const int LastCyrillic = 0x0530;

    /// <summary>
    /// Runs of letters and digits, with the marks that follow them, one space apart. A mark never
    /// starts a word: an emoji's variation selector is not one.
    /// </summary>
    private static string Words(string text)
    {
        var words = new StringBuilder();
        var inWord = false;
        foreach (var rune in text.EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            var mark = category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
            var wordy = Rune.IsLetterOrDigit(rune) || (mark && inWord);
            if (wordy)
            {
                if (!inWord && words.Length > 0)
                    words.Append(' ');
                words.Append(rune.ToString());
            }
            inWord = wordy;
        }
        return words.ToString();
    }

    /// <summary>The title with every bracketed part removed, for a catalog search.</summary>
    public static string WithoutBrackets(string title) => Brackets().Replace(title, "");

    /// <summary>The first credited artist, which is how catalogs file a collaboration.</summary>
    public static string LeadArtist(string artist) => artist.Split(',')[0];

    // One bracket group holding a credit or an edition label, unless it also names another recording:
    // "(Live Version)" and "(Remix)" stay, so a live cut never matches the studio song.
    [GeneratedRegex(
        @"[\(\[](?![^\)\]]*\b(?:live|remix|mix|acoustic|demo|instrumental|karaoke|sped|slowed|remaster(?:ed)?)\b)[^\)\]]*?\b(?:feat|with|ft|clean|explicit|edit|edited|version)\b[^\)\]]*[\)\]]",
        RegexOptions.CultureInvariant)]
    private static partial Regex EditionLabel();

    [GeneratedRegex(@"[\(\[].*?[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();
}

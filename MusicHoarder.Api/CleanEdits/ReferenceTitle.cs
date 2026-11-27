using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.CleanEdits;

/// <summary>How a catalog title is compared with a song's: a reference only counts when the two come out equal.</summary>
internal static partial class ReferenceTitle
{
    /// <summary>Lowercase ASCII words, with feature credits and edition labels in brackets dropped.</summary>
    public static string Normalize(string title)
    {
        var ascii = new StringBuilder();
        foreach (var c in title.Normalize(NormalizationForm.FormKD))
        {
            if (c < 128)
                ascii.Append(char.ToLowerInvariant(c));
        }
        var unlabelled = EditionLabel().Replace(ascii.ToString(), " ");
        return NonWord().Replace(unlabelled, " ").Trim();
    }

    /// <summary>The title with every bracketed part removed, for a catalog search.</summary>
    public static string WithoutBrackets(string title) => Brackets().Replace(title, "");

    /// <summary>The first credited artist, which is how catalogs file a collaboration.</summary>
    public static string LeadArtist(string artist) => artist.Split(',')[0];

    [GeneratedRegex(@"[\(\[].*?(feat|with|ft\.|clean|explicit|edit|version).*?[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex EditionLabel();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWord();

    [GeneratedRegex(@"[\(\[].*?[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex Brackets();
}

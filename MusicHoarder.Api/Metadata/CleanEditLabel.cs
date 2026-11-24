using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Metadata;

/// <summary>
/// What a clean edit's label is. Labels publish a song twice, the original and a censored edit, and the
/// edit announces itself in the title ("(Clean)", "- Edited Version", "(Album Version Edited)"). The
/// library always wants the original, so every place that picks between editions shares this one rule.
/// Radio edits are deliberately not labels here: they are a different cut, often the one asked for.
/// </summary>
public static partial class CleanEditLabel
{
    private static readonly string[] MarkerWords = ["clean", "censored", "edited"];

    // A bracketed group, or a trailing " - ", whose whole content is label vocabulary. Words that are
    // part of a title's identity ("Clean (Taylor's Version)", "Burn Clean", "(feat. Clean Bandit)")
    // never form a whole group, so they survive.
    [GeneratedRegex(
        @"\s*(?:[\(\[]\s*(?:original\s+clean|album\s+version\s+edited|clean\s+album\s+version|(?:clean|edited|censored)(?:\s+(?:album\s+version|version|edit))?)\s*[\)\]]"
        + @"|\s-\s+(?:original\s+clean|album\s+version\s+edited|clean\s+album\s+version|(?:clean|edited|censored)(?:\s+(?:album\s+version|version|edit))?)\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"[\p{L}\p{N}]+")]
    private static partial Regex WordPattern();

    /// <summary><paramref name="title"/> without its clean-edit label, trimmed.</summary>
    public static string Strip(string title) => LabelPattern().Replace(title, "").Trim();

    /// <summary>
    /// Whether a search hit announces itself as a clean edit the request did not ask for: it carries a
    /// marker word (clean, censored, edited) that the request, label aside, does not. Comparing against
    /// the request keeps titles that contain the word for real ("So Fresh, So Clean") and catches bare
    /// upload titles ("Make Her Say CLEAN").
    /// </summary>
    public static bool IsUnrequestedCleanEdit(string candidateTitle, string requestedArtistAndTitle)
    {
        var requested = Words(Strip(requestedArtistAndTitle));
        var candidate = Words(candidateTitle);
        return MarkerWords.Any(m => candidate.Contains(m) && !requested.Contains(m));
    }

    private static HashSet<string> Words(string text) =>
        WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
}

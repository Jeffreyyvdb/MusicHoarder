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

    private const string Vocabulary =
        @"(?:original\s+clean|album\s+version\s+edited|(?:clean|edited|censored)(?:\s+(?:album\s+version|version|edit))?)";

    // A bracketed group, or a trailing " - ", whose whole content is label vocabulary. Words that are
    // part of a title's identity ("Clean (Taylor's Version)", "Burn Clean", "(feat. Clean Bandit)")
    // never form a whole group, so they survive.
    [GeneratedRegex(@"\s*(?:[\(\[]\s*" + Vocabulary + @"\s*[\)\]]|\s-\s+" + Vocabulary + @"\s*$)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LabelPattern();

    // Only the bracketed form, for search hits: an upload titled "Taylor Swift - Clean" is the song.
    [GeneratedRegex(@"[\(\[]\s*" + Vocabulary + @"\s*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BracketedLabelPattern();

    // "Shot & Edited by Cole Bennett" credits the video editor, it does not describe the audio.
    [GeneratedRegex(@"\bedited\s+by\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditorCreditPattern();

    // Hyphenated compounds stay one word, so "Un-censored" never reads as "censored".
    [GeneratedRegex(@"[\p{L}\p{N}]+(?:-[\p{L}\p{N}]+)*")]
    private static partial Regex WordPattern();

    /// <summary><paramref name="title"/> without its clean-edit label, trimmed.</summary>
    public static string Strip(string title) => LabelPattern().Replace(title, "").Trim();

    /// <summary>
    /// Whether a search hit is a clean edit the request did not ask for. A bracketed label always makes it
    /// one, since the original is preferred even over a requested clean edit. Otherwise it is one when it
    /// carries a marker word (clean, censored, edited) that the requested artist and title, label aside,
    /// do not: that keeps titles that contain the word for real ("So Fresh, So Clean") and catches bare
    /// upload titles ("Make Her Say CLEAN").
    /// </summary>
    public static bool IsUnrequestedCleanEdit(string candidateTitle, string requestedArtist, string requestedTitle)
    {
        if (BracketedLabelPattern().IsMatch(candidateTitle))
            return true;

        var requested = Words($"{requestedArtist} {Strip(requestedTitle)}");
        var candidate = Words(EditorCreditPattern().Replace(candidateTitle, " "));
        return MarkerWords.Any(m => candidate.Contains(m) && !requested.Contains(m));
    }

    private static HashSet<string> Words(string text) =>
        WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
}

using System.Text;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Matching;

/// <summary>
/// Version/edition qualifiers detected in a track title or album name. Used to keep
/// a "Live" / "Remix" / "Acoustic" candidate from silently satisfying a request for
/// the studio recording.
/// </summary>
[Flags]
public enum VersionQualifiers
{
    None = 0,
    Live = 1 << 0,
    Remix = 1 << 1,
    Remaster = 1 << 2,
    Deluxe = 1 << 3,
    Acoustic = 1 << 4,
    Instrumental = 1 << 5,
    Demo = 1 << 6,
    Cover = 1 << 7,
    Edit = 1 << 8,
    Extended = 1 << 9,
    Radio = 1 << 10,
    Karaoke = 1 << 11,
}

public static partial class VersionQualifier
{
    /// <summary>
    /// Qualifiers that change the *identity* of the recording. Two identities are only
    /// compatible if they carry the same set of these. Non-strong qualifiers (Remaster,
    /// Deluxe, Edit, Extended, Radio) are edition/packaging differences of the same song
    /// and are treated as compatible with a plain studio recording.
    /// </summary>
    public const VersionQualifiers StrongMask =
        VersionQualifiers.Live
        | VersionQualifiers.Remix
        | VersionQualifiers.Acoustic
        | VersionQualifiers.Instrumental
        | VersionQualifiers.Demo
        | VersionQualifiers.Cover
        | VersionQualifiers.Karaoke;

    public static VersionQualifiers Detect(string? title, string? album = null)
    {
        var result = DetectIn(title);
        // Deluxe/Remaster/Edition markers usually live on the album, not the track title.
        result |= DetectIn(album) & (VersionQualifiers.Remaster | VersionQualifiers.Deluxe | VersionQualifiers.Extended);
        return result;
    }

    private static VersionQualifiers DetectIn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return VersionQualifiers.None;

        var v = value.ToLowerInvariant();
        var result = VersionQualifiers.None;

        // Unambiguous markers: the word essentially never appears in a normal studio
        // title meaning anything else, so detecting it anywhere in the string is safe.
        if (RemixPattern().IsMatch(v)) result |= VersionQualifiers.Remix;
        if (RemasterPattern().IsMatch(v)) result |= VersionQualifiers.Remaster;
        if (DeluxePattern().IsMatch(v)) result |= VersionQualifiers.Deluxe;
        if (AcousticPattern().IsMatch(v)) result |= VersionQualifiers.Acoustic;
        if (InstrumentalPattern().IsMatch(v)) result |= VersionQualifiers.Instrumental;
        if (EditPattern().IsMatch(v)) result |= VersionQualifiers.Edit;
        if (ExtendedPattern().IsMatch(v)) result |= VersionQualifiers.Extended;
        if (RadioPattern().IsMatch(v)) result |= VersionQualifiers.Radio;
        if (KaraokePattern().IsMatch(v)) result |= VersionQualifiers.Karaoke;

        // Ambiguous everyday words ("live", "cover", "demo") are only a version marker when
        // they appear as a decoration — inside parentheses/brackets or after a " - " separator
        // — or in an explicit phrase ("Live at …", "Live version"). Otherwise studio titles
        // like "Live and Let Die", "Live Forever", "Cover Me" or "Demolition" would be
        // misread as live/cover/demo recordings and wrongly fail to match the studio catalog.
        var decorations = ExtractDecorations(v);
        if (LivePattern().IsMatch(decorations) || LivePhrasePattern().IsMatch(v))
            result |= VersionQualifiers.Live;
        if (CoverPattern().IsMatch(decorations)) result |= VersionQualifiers.Cover;
        if (DemoPattern().IsMatch(decorations)) result |= VersionQualifiers.Demo;

        return result;
    }

    /// <summary>
    /// Returns the "decoration" portion of a (already-lowercased) title: the text inside any
    /// parentheses/brackets, plus any trailing segment after a " - " / " – " separator. These
    /// are where edition/version markers conventionally live.
    /// </summary>
    private static string ExtractDecorations(string lowered)
    {
        var sb = new StringBuilder();
        foreach (Match m in DecorationGroupPattern().Matches(lowered))
        {
            sb.Append(' ');
            sb.Append(m.Value);
        }

        var dashIdx = lowered.LastIndexOf(" - ", StringComparison.Ordinal);
        if (dashIdx < 0)
            dashIdx = lowered.LastIndexOf(" – ", StringComparison.Ordinal);
        if (dashIdx >= 0)
        {
            sb.Append(' ');
            sb.Append(lowered.AsSpan(dashIdx + 3));
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns true when a candidate's qualifiers are compatible with what the source
    /// track represents — i.e. they carry the same identity-changing (strong) qualifiers.
    /// </summary>
    public static bool Compare(VersionQualifiers expected, VersionQualifiers candidate)
        => (expected & StrongMask) == (candidate & StrongMask);

    [GeneratedRegex(@"\blive\b", RegexOptions.Compiled)]
    private static partial Regex LivePattern();

    // Explicit live phrasing that's unambiguous even outside a decoration: "Live at <venue>",
    // "Live in concert", "Live version/recording/session". "in"/"from" alone are excluded so
    // studio titles like "Live in Color" aren't misread.
    [GeneratedRegex(@"\blive\s+(?:at|version|recording|session|in\s+concert)\b", RegexOptions.Compiled)]
    private static partial Regex LivePhrasePattern();

    // A parenthetical or bracketed group, e.g. "(Live)" / "[Acoustic Cover]".
    [GeneratedRegex(@"\((.*?)\)|\[(.*?)\]", RegexOptions.Compiled)]
    private static partial Regex DecorationGroupPattern();

    [GeneratedRegex(@"\bre-?mix(ed|es)?\b", RegexOptions.Compiled)]
    private static partial Regex RemixPattern();

    [GeneratedRegex(@"\bre-?master(ed)?\b", RegexOptions.Compiled)]
    private static partial Regex RemasterPattern();

    [GeneratedRegex(@"\bdeluxe\b", RegexOptions.Compiled)]
    private static partial Regex DeluxePattern();

    [GeneratedRegex(@"\bac(ou)?stic\b", RegexOptions.Compiled)]
    private static partial Regex AcousticPattern();

    [GeneratedRegex(@"\binstrumental\b", RegexOptions.Compiled)]
    private static partial Regex InstrumentalPattern();

    [GeneratedRegex(@"\bdemo\b", RegexOptions.Compiled)]
    private static partial Regex DemoPattern();

    [GeneratedRegex(@"\bcover\b", RegexOptions.Compiled)]
    private static partial Regex CoverPattern();

    [GeneratedRegex(@"\b(radio|single|club|extended|short)?\s*edit\b", RegexOptions.Compiled)]
    private static partial Regex EditPattern();

    [GeneratedRegex(@"\bextended\b", RegexOptions.Compiled)]
    private static partial Regex ExtendedPattern();

    [GeneratedRegex(@"\bradio\s*(edit|version|mix)\b", RegexOptions.Compiled)]
    private static partial Regex RadioPattern();

    [GeneratedRegex(@"\bkaraoke\b", RegexOptions.Compiled)]
    private static partial Regex KaraokePattern();
}

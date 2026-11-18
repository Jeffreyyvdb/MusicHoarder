using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Enrichment;

/// <summary>
/// Reads the lyrics out of a Genius song page. The words sit in one or more
/// <c>data-lyrics-container="true"</c> blocks, lines separated by <c>&lt;br&gt;</c>, with the page's own
/// header (contributors, "… Lyrics") in an element marked <c>data-exclude-from-selection</c>. Section
/// labels such as <c>[Chorus: A, B]</c> are dropped.
/// </summary>
public static partial class GeniusLyricsParser
{
    private static readonly HashSet<string> VoidTags =
        new(StringComparer.OrdinalIgnoreCase) { "br", "img", "hr", "input", "wbr", "meta", "link" };

    /// <summary>Null when the page has no lyrics container, or nothing is left after cleaning.</summary>
    public static string? Parse(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var text = new StringBuilder();
        var found = false;
        foreach (Match start in ContainerStart().Matches(html))
        {
            found = true;
            AppendContainer(html, start.Index + start.Length, text);
            text.Append('\n');
        }

        return found ? Clean(text.ToString()) : null;
    }

    private static void AppendContainer(string html, int from, StringBuilder text)
    {
        var divDepth = 1;
        string? skipTag = null;
        var skipDepth = 0;

        foreach (Match token in Token().Matches(html, from))
        {
            var value = token.Value;
            if (value[0] != '<')
            {
                if (skipTag is null)
                    text.Append(WebUtility.HtmlDecode(value));
                continue;
            }

            if (value.StartsWith("<!--", StringComparison.Ordinal))
                continue;

            var tag = TagParts().Match(value);
            if (!tag.Success)
                continue;

            var closing = tag.Groups["close"].Length > 0;
            var name = tag.Groups["name"].Value.ToLowerInvariant();
            var selfClosing = value.EndsWith("/>", StringComparison.Ordinal) || VoidTags.Contains(name);

            if (skipTag is not null)
            {
                if (name == skipTag && !selfClosing)
                {
                    skipDepth += closing ? -1 : 1;
                    if (skipDepth == 0)
                        skipTag = null;
                }

                continue;
            }

            if (closing)
            {
                if (name == "div" && --divDepth == 0)
                    return;
                continue;
            }

            if (ExcludedAttribute().IsMatch(value) && !selfClosing)
            {
                skipTag = name;
                skipDepth = 1;
                continue;
            }

            if (name == "br")
                text.Append('\n');
            else if (name == "div" && !selfClosing)
                divDepth++;
        }
    }

    private static string? Clean(string raw)
    {
        var lines = raw
            .Replace(' ', ' ')
            .Replace("\r", string.Empty)
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => !SectionLabel().IsMatch(l));

        var joined = BlankRuns().Replace(string.Join('\n', lines), "\n\n").Trim();
        return joined.Length == 0 ? null : joined;
    }

    [GeneratedRegex("""<div\b[^>]*\bdata-lyrics-container\s*=\s*"true"[^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerStart();

    [GeneratedRegex(@"<!--.*?-->|<[^>]+>|[^<]+", RegexOptions.Singleline)]
    private static partial Regex Token();

    [GeneratedRegex(@"^<(?<close>/)?(?<name>[a-zA-Z][a-zA-Z0-9]*)")]
    private static partial Regex TagParts();

    [GeneratedRegex(@"\bdata-exclude-from-selection\s*=\s*""true""", RegexOptions.IgnoreCase)]
    private static partial Regex ExcludedAttribute();

    [GeneratedRegex(@"^\[[^\]]*\]$")]
    private static partial Regex SectionLabel();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankRuns();
}

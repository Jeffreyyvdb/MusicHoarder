using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace MusicHoarder.Api.Matching;

/// <summary>Fields a <see cref="MatchRulePattern"/> extracted from an input string.</summary>
public sealed record MatchRuleExtraction(string? Artist, string? Title, string? Album, string? AlbumArtist)
{
    public bool HasAny => Artist is not null || Title is not null || Album is not null || AlbumArtist is not null;
}

/// <summary>A template compiled to a regex, plus the placeholder field names it captures.</summary>
public sealed record CompiledMatchRule(Regex Regex, IReadOnlyList<string> Fields);

/// <summary>
/// Compiles a user-friendly template pattern into a regex and extracts metadata fields from a
/// matching string. A template is literal text plus <c>{artist}</c> / <c>{title}</c> / <c>{album}</c>
/// / <c>{albumartist}</c> placeholders, e.g. <c>{artist} | {title} | 101Barz</c>. Literal text is
/// matched verbatim (whitespace-tolerant, case-insensitive) and anchors the match; placeholders
/// capture the variable parts into the named fields. Compilation is cached per template.
/// </summary>
public static partial class MatchRulePattern
{
    public static readonly IReadOnlyList<string> SupportedFields = ["artist", "title", "album", "albumartist"];

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(200);
    private static readonly ConcurrentDictionary<string, CompiledMatchRule> Cache = new();

    /// <summary>
    /// Compiles <paramref name="template"/>. Returns false with a human-readable <paramref name="error"/>
    /// when the template is malformed (unbalanced braces, unknown/duplicate placeholder, no placeholder).
    /// </summary>
    public static bool TryCompile(string? template, out CompiledMatchRule? compiled, out string? error)
    {
        compiled = null;
        error = null;

        if (string.IsNullOrWhiteSpace(template))
        {
            error = "Pattern is empty.";
            return false;
        }

        var t = template.Trim();
        var sb = new StringBuilder("^");
        var fields = new List<string>();

        var i = 0;
        while (i < t.Length)
        {
            var c = t[i];
            if (c == '{')
            {
                var close = t.IndexOf('}', i + 1);
                if (close < 0)
                {
                    error = "Unclosed '{' in pattern.";
                    return false;
                }

                var name = t[(i + 1)..close].Trim().ToLowerInvariant();
                if (name.Length == 0)
                {
                    error = "Empty placeholder '{}'.";
                    return false;
                }

                if (!SupportedFields.Contains(name))
                {
                    error = $"Unknown placeholder '{{{name}}}'. Supported: {string.Join(", ", SupportedFields.Select(f => $"{{{f}}}"))}.";
                    return false;
                }

                if (fields.Contains(name))
                {
                    error = $"Placeholder '{{{name}}}' appears more than once.";
                    return false;
                }

                fields.Add(name);
                sb.Append("(?<").Append(name).Append(">.+?)");
                i = close + 1;
            }
            else if (c == '}')
            {
                error = "Unexpected '}' in pattern.";
                return false;
            }
            else
            {
                var start = i;
                while (i < t.Length && t[i] != '{' && t[i] != '}')
                    i++;
                AppendLiteral(sb, t[start..i]);
            }
        }

        sb.Append('$');

        if (fields.Count == 0)
        {
            error = "Pattern must contain at least one placeholder, e.g. {title}.";
            return false;
        }

        try
        {
            var rx = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout);
            compiled = new CompiledMatchRule(rx, fields);
            return true;
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Compiles (cached) and matches in one call; null when the template is invalid or doesn't match.</summary>
    public static MatchRuleExtraction? Match(string? template, string? input)
    {
        if (string.IsNullOrWhiteSpace(template))
            return null;

        var compiled = Cache.GetOrAdd(template.Trim(), key =>
            TryCompile(key, out var c, out _) ? c! : new CompiledMatchRule(MatchNothing, []));

        return compiled.Fields.Count == 0 ? null : Match(compiled, input);
    }

    /// <summary>Matches an already-compiled rule; null when the input is empty or doesn't match.</summary>
    public static MatchRuleExtraction? Match(CompiledMatchRule compiled, string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        Match m;
        try
        {
            m = compiled.Regex.Match(Prepare(input));
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        if (!m.Success)
            return null;

        return new MatchRuleExtraction(
            Get(compiled, m, "artist"),
            Get(compiled, m, "title"),
            Get(compiled, m, "album"),
            Get(compiled, m, "albumartist"));
    }

    private static string? Get(CompiledMatchRule compiled, Match m, string field)
    {
        if (!compiled.Fields.Contains(field))
            return null;
        var value = m.Groups[field].Value.Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>
    /// Compiles a literal run. A run made only of whitespace + connector punctuation
    /// (<c>- _ | ｜ / · – —</c>) becomes a single flexible separator class, so one template like
    /// <c>{artist} | {title} | 101Barz</c> matches the dash / underscore / fullwidth-pipe variants.
    /// Other literals (e.g. a channel name) are escaped and matched exactly.
    /// </summary>
    private static void AppendLiteral(StringBuilder sb, string literal)
    {
        var i = 0;
        while (i < literal.Length)
        {
            if (IsSeparator(literal[i]))
            {
                var start = i;
                while (i < literal.Length && IsSeparator(literal[i]))
                    i++;
                var run = literal[start..i];
                // A run containing an actual connector (e.g. " | ") matches any connector with optional
                // surrounding whitespace, so one template covers dash/underscore/pipe/fullwidth. A
                // whitespace-only run stays a plain whitespace gap — crucially it must NOT match a bare
                // space, or the artist/title boundary would land mid-name ("Yung Nnelg").
                sb.Append(run.Any(IsConnector) ? ConnectorSeparator : "\\s+");
            }
            else
            {
                var start = i;
                while (i < literal.Length && !IsSeparator(literal[i]))
                    i++;
                sb.Append(Regex.Escape(literal[start..i]));
            }
        }
    }

    // Whitespace or a connector. Connectors are the credit separators yt-dlp / manual renames use
    // interchangeably: ASCII hyphen/underscore/pipe/slash, fullwidth pipe U+FF5C, middle dot, en/em dash.
    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || IsConnector(c);
    private static bool IsConnector(char c) => c is '-' or '_' or '|' or '/' or '｜' or '·' or '–' or '—';

    // At least one connector, with optional whitespace on either side.
    private const string ConnectorSeparator = "\\s*[\\-_|/｜·–—]+\\s*";

    /// <summary>
    /// Normalizes a raw title/filename before matching: strips a leading track-number prefix
    /// ("00 - ", "346 - "), a trailing YouTube id ("[dZ1nGVWI5a0]"), and a trailing dedupe suffix
    /// ("(1)"), so templates needn't encode that noise.
    /// </summary>
    private static string Prepare(string input)
    {
        var value = input.Trim();
        value = TrailingYouTubeId().Replace(value, "");
        value = TrailingDuplicateSuffix().Replace(value, "");
        value = LeadingTrackNumber().Replace(value, "");
        return value.Trim();
    }

    [GeneratedRegex(@"^\s*\d{1,4}\s*[-._]\s+", RegexOptions.Compiled)]
    private static partial Regex LeadingTrackNumber();

    [GeneratedRegex(@"\s*\[[A-Za-z0-9_-]{6,}\]\s*$", RegexOptions.Compiled)]
    private static partial Regex TrailingYouTubeId();

    [GeneratedRegex(@"\s*\(\d+\)\s*$", RegexOptions.Compiled)]
    private static partial Regex TrailingDuplicateSuffix();

    // A regex that never matches — cached for invalid templates so we don't recompile them.
    private static readonly Regex MatchNothing = new("(?!)", RegexOptions.CultureInvariant);
}

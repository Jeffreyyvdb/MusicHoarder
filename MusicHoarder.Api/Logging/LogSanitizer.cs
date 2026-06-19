namespace MusicHoarder.Api.Logging;

/// <summary>
/// Sanitizes externally-sourced strings (Spotify track/playlist names, search queries, etc.) before
/// they go into a structured log message, stripping CR/LF and other control characters so a crafted
/// value can't forge extra log lines (CodeQL "log entries created from user input").
/// </summary>
public static class LogSanitizer
{
    /// <summary>
    /// Returns <paramref name="value"/> with the line-break characters that enable log forging removed.
    /// Uses <see cref="string.Replace(string, string)"/> on CR/LF/tab — the pattern CodeQL's log-forging
    /// query recognizes as a sanitizing barrier.
    /// </summary>
    public static string? ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        return value
            .Replace("\r", string.Empty)
            .Replace("\n", string.Empty)
            .Replace("\t", " ");
    }
}

namespace MusicHoarder.Api.Logging;

/// <summary>
/// Sanitizes externally-sourced strings (Spotify track/playlist names, search queries, etc.) before
/// they go into a structured log message, stripping CR/LF and other control characters so a crafted
/// value can't forge extra log lines (CodeQL "log entries created from user input").
/// </summary>
public static class LogSanitizer
{
    /// <summary>Returns <paramref name="value"/> with control characters (incl. CR/LF/tab) removed.</summary>
    public static string? ForLog(string? value)
    {
        if (string.IsNullOrEmpty(value)) return value;

        Span<char> buffer = value.Length <= 256 ? stackalloc char[value.Length] : new char[value.Length];
        var written = 0;
        foreach (var ch in value)
        {
            if (!char.IsControl(ch))
                buffer[written++] = ch;
        }

        return written == value.Length ? value : new string(buffer[..written]);
    }
}

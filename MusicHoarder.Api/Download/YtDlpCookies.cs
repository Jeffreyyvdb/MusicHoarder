namespace MusicHoarder.Api.Download;

/// <summary>
/// Staging for the yt-dlp cookies file. yt-dlp rewrites its <c>--cookies</c> file on exit to persist
/// refreshed session cookies; when the deployment mounts that file read-only (a bind mount with
/// <c>ReadOnly = true</c>), the write fails with <c>OSError: [Errno 30] Read-only file system</c> and
/// crashes yt-dlp — <em>after</em> it has already produced its output. To sidestep that, we hand yt-dlp
/// a writable per-invocation copy in the temp dir and delete it afterwards. Per-invocation (not shared)
/// because concurrent downloads each rewrite their own copy.
/// </summary>
internal static class YtDlpCookies
{
    /// <summary>
    /// Returns a writable temp copy of <paramref name="configuredPath"/> to pass to <c>--cookies</c>, or
    /// null when no cookies file is configured/present (caller then omits <c>--cookies</c>). Falls back to
    /// the configured path itself if the copy can't be made (no worse than before).
    /// </summary>
    public static string? PrepareWritableCopy(string? configuredPath, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(configuredPath) || !File.Exists(configuredPath))
            return null;
        try
        {
            var temp = Path.Combine(Path.GetTempPath(), $"yt-cookies-{Guid.NewGuid():N}.txt");
            File.Copy(configuredPath, temp, overwrite: true);
            return temp;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not stage a writable copy of the yt-dlp cookies file; using it directly");
            return configuredPath;
        }
    }

    /// <summary>Deletes the staged temp copy (no-op when we fell back to the configured file itself).</summary>
    public static void Cleanup(string? preparedPath, string? configuredPath)
    {
        if (preparedPath is null || string.Equals(preparedPath, configuredPath, StringComparison.Ordinal))
            return;
        try { File.Delete(preparedPath); }
        catch { /* best effort — temp files age out anyway */ }
    }
}

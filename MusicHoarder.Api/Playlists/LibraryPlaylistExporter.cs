using System.Text;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Playlists;

/// <summary>Outcome of one export run, for logs and tests.</summary>
public sealed record LibraryPlaylistExportResult(int Written, int Unchanged, int Removed);

public interface ILibraryPlaylistExporter
{
    /// <summary>
    /// Writes every library-owner playlist marked <see cref="Playlist.ExportToLibrary"/> as an
    /// <c>.m3u8</c> of its built tracks, in playlist order, into the destination library's playlists
    /// folder. A file whose content would not change is left alone, so a media server does not
    /// re-import it; a renamed playlist's old file is removed.
    /// </summary>
    Task<LibraryPlaylistExportResult> RunAsync(CancellationToken ct = default);

    /// <summary>Removes a playlist's file (it was deleted, or its export was switched off).</summary>
    Task RemoveFileAsync(string? filePath, CancellationToken ct = default);
}

/// <summary>
/// Exports MusicHoarder's own playlists. Only the library owner's: the destination library is theirs,
/// and a member's playlist names songs from someone else's library.
/// </summary>
public sealed class LibraryPlaylistExporter(
    IServiceScopeFactory scopeFactory,
    IOwnerLookupService ownerLookup,
    IM3uPlaylistWriter writer,
    IOptions<MusicEnricherOptions> options,
    ILogger<LibraryPlaylistExporter> logger) : ILibraryPlaylistExporter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public async Task<LibraryPlaylistExportResult> RunAsync(CancellationToken ct = default)
    {
        if (!options.Value.EnablePlaylistExport)
            return new LibraryPlaylistExportResult(0, 0, 0);
        var playlistsDir = PlaylistFolder.Resolve(options.Value);
        if (playlistsDir is null)
            return new LibraryPlaylistExportResult(0, 0, 0);

        await PlaylistFolder.Lock.WaitAsync(ct);
        try
        {
            return await RunCoreAsync(playlistsDir, ct);
        }
        finally
        {
            PlaylistFolder.Lock.Release();
        }
    }

    public async Task RemoveFileAsync(string? filePath, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return;
        var playlistsDir = PlaylistFolder.Resolve(options.Value);
        await PlaylistFolder.Lock.WaitAsync(ct);
        try
        {
            PlaylistFolder.TryDelete(filePath, playlistsDir, logger);
        }
        finally
        {
            PlaylistFolder.Lock.Release();
        }
    }

    private async Task<LibraryPlaylistExportResult> RunCoreAsync(string playlistsDir, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ownerId = ownerLookup.OwnerUserId;

        // Oldest first, so when two playlists share a name the older one keeps the plain file name.
        var playlists = await db.Playlists
            .IgnoreQueryFilters()
            .Include(p => p.Entries)
            .Where(p => p.OwnerUserId == ownerId && p.ExportToLibrary)
            .OrderBy(p => p.Id)
            .ToListAsync(ct);
        if (playlists.Count == 0)
            return new LibraryPlaylistExportResult(0, 0, 0);

        var sourceIds = playlists.Where(p => p.WishlistSourceId != null).Select(p => p.WishlistSourceId!.Value).ToList();
        var sources = await db.WishlistSources
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId && sourceIds.Contains(s.Id))
            .ToDictionaryAsync(s => s.Id, ct);

        var refs = await PlaylistTracks.LoadRefsAsync(db, playlists, sources, ct);
        var candidates = refs.Values.SelectMany(r => r).Where(r => r.SongId != null).Select(r => r.SongId!.Value).ToList();
        var built = await PlaylistTracks.BuiltForOwnerAsync(db, ownerId, candidates, ct);
        var songIdMap = built.ToDictionary(kv => kv.Key, kv => kv.Value.Id);
        var builtById = built.Values.DistinctBy(s => s.Id).ToDictionary(s => s.Id);

        // Playlist sync's files are its own; never take one of their names.
        var reserved = await db.ExportedPlaylists
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.OwnerUserId == ownerId && e.FilePath != "")
            .Select(e => e.FilePath)
            .ToListAsync(ct);
        var used = new HashSet<string>(reserved.Select(Path.GetFileName).OfType<string>(), StringComparer.OrdinalIgnoreCase);

        int written = 0, unchanged = 0, removed = 0;
        var now = DateTime.UtcNow;
        foreach (var playlist in playlists)
        {
            ct.ThrowIfCancellationRequested();

            var name = playlist.WishlistSourceId is { } sid && sources.TryGetValue(sid, out var source)
                ? source.Name
                : playlist.Name;
            var fileName = UniqueFileName(PlaylistFolder.FileStem(name, "Playlist"), playlist.ExportFilePath, used);
            var filePath = Path.Combine(playlistsDir, fileName);

            var tracks = PlaylistTracks.Resolve(refs[playlist.Id], songIdMap);
            var entries = tracks.SongIds
                .Select(id => builtById[id])
                .Select(s => new M3uEntry(s.DestinationPath, s.Artist, s.Title, s.DurationSeconds))
                .ToList();

            var content = M3uPlaylistWriter.BuildContent(playlistsDir, entries);
            if (File.Exists(filePath) && await File.ReadAllTextAsync(filePath, Utf8NoBom, ct) == content)
            {
                unchanged++;
            }
            else
            {
                await writer.WriteAsync(filePath, playlistsDir, entries, ct);
                written++;
                logger.LogInformation("Exported playlist {PlaylistId}: {Count} tracks → {Path}", playlist.Id, entries.Count, filePath);
            }

            // A rename leaves the old file behind. Compared without case: on a case-insensitive share
            // "mix.m3u8" and "Mix.m3u8" are the file just written.
            if (!string.IsNullOrEmpty(playlist.ExportFilePath)
                && !string.Equals(playlist.ExportFilePath, filePath, StringComparison.OrdinalIgnoreCase)
                && PlaylistFolder.TryDelete(playlist.ExportFilePath, playlistsDir, logger))
            {
                removed++;
            }

            if (!string.Equals(playlist.ExportFilePath, filePath, StringComparison.Ordinal))
                playlist.ExportFilePath = filePath;
            playlist.ExportedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
        return new LibraryPlaylistExportResult(written, unchanged, removed);
    }

    /// <summary>
    /// "Name.m3u8", or "Name (2).m3u8" and up when another playlist has it. A playlist whose current
    /// file is still named after it keeps that file, so an export never swaps two playlists' files.
    /// </summary>
    internal static string UniqueFileName(string stem, string? currentPath, HashSet<string> used)
    {
        var current = string.IsNullOrEmpty(currentPath) ? null : Path.GetFileName(currentPath);
        if (current is not null && IsNamedAfter(current, stem) && used.Add(current))
            return current;

        for (var n = 1; ; n++)
        {
            var candidate = n == 1 ? $"{stem}.m3u8" : $"{stem} ({n}).m3u8";
            if (used.Add(candidate))
                return candidate;
        }
    }

    /// <summary>"Stem.m3u8" or "Stem (n).m3u8", exactly (a change of case is a rename).</summary>
    private static bool IsNamedAfter(string fileName, string stem)
    {
        if (!fileName.EndsWith(".m3u8", StringComparison.Ordinal)) return false;
        var bare = fileName[..^".m3u8".Length];
        if (bare == stem) return true;
        if (!bare.StartsWith(stem + " (", StringComparison.Ordinal) || !bare.EndsWith(')')) return false;
        return int.TryParse(bare[(stem.Length + 2)..^1], out var n) && n >= 2;
    }
}

/// <summary>
/// Asks for an export soon. Edits call <see cref="Request"/>; the worker coalesces a burst of them
/// (adding a whole album is one request per call, but tapping through several songs is a burst).
/// </summary>
public sealed class LibraryPlaylistExportQueue
{
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Request() => _signal.Writer.TryWrite(true);

    /// <summary>Waits for a request, or until <paramref name="timeout"/> passes (null waits for a request only).</summary>
    public async Task WaitAsync(TimeSpan? timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout is { } t) cts.CancelAfter(t);
        try
        {
            await _signal.Reader.ReadAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The periodic sweep is due.
        }
    }
}

/// <summary>
/// Runs <see cref="ILibraryPlaylistExporter"/> shortly after an edit, and every
/// <c>MusicEnricher:LibraryPlaylistExportIntervalMinutes</c> for tracks built in the meantime.
/// </summary>
public sealed class LibraryPlaylistExportBackgroundService(
    ILibraryPlaylistExporter exporter,
    LibraryPlaylistExportQueue queue,
    IOptions<MusicEnricherOptions> options,
    ILogger<LibraryPlaylistExportBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.EnablePlaylistExport)
        {
            logger.LogInformation("Library playlist export is disabled (MusicEnricher:EnablePlaylistExport = false)");
            return;
        }

        var intervalMinutes = Math.Max(0, options.Value.LibraryPlaylistExportIntervalMinutes);
        TimeSpan? period = intervalMinutes == 0 ? null : TimeSpan.FromMinutes(intervalMinutes);

        try
        {
            // Stagger after boot, like the other sweeps; an edit in the meantime still counts.
            await queue.WaitAsync(TimeSpan.FromSeconds(45), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Debounce, stoppingToken);
                try
                {
                    await exporter.RunAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Library playlist export failed");
                }
                await queue.WaitAsync(period, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }
}

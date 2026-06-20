using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

/// <summary>Outcome of a single export run, surfaced to the manual trigger / logs.</summary>
public sealed record PlaylistExportResult(
    bool Ran,
    int PlaylistsWritten,
    int TotalTracks,
    int MatchedTracks,
    IReadOnlyList<ExportedPlaylistSummary> Playlists);

public sealed record ExportedPlaylistSummary(
    string Kind,
    string Name,
    string FilePath,
    int SpotifyTrackTotal,
    int MatchedTrackCount);

public interface IPlaylistExportService
{
    /// <summary>
    /// Mirrors the owner's Spotify Liked Songs + every playlist to <c>.m3u8</c> files in the
    /// destination library. Returns <c>Ran=false</c> if another run is already in progress.
    /// </summary>
    Task<PlaylistExportResult> RunExportAsync(CancellationToken ct = default);
}

public sealed class PlaylistExportService(
    ISpotifyApiService spotifyApi,
    ISpotifyLibraryComparisonService comparison,
    IServiceScopeFactory scopeFactory,
    IOwnerLookupService ownerLookup,
    IM3uPlaylistWriter writer,
    IOptions<MusicEnricherOptions> options,
    ILogger<PlaylistExportService> logger) : IPlaylistExportService
{
    // Serialize export runs: the periodic background tick and the manual regenerate button must never
    // write the same files concurrently.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<PlaylistExportResult> RunExportAsync(CancellationToken ct = default)
    {
        if (!await Gate.WaitAsync(0, ct))
        {
            logger.LogInformation("Playlist export already running; skipping this trigger");
            return new PlaylistExportResult(false, 0, 0, 0, []);
        }

        try
        {
            return await RunExportCoreAsync(ct);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<PlaylistExportResult> RunExportCoreAsync(CancellationToken ct)
    {
        var opts = options.Value;
        var destinationRoot = opts.DestinationDirectory;
        if (string.IsNullOrWhiteSpace(destinationRoot))
        {
            logger.LogWarning("Playlist export skipped: no destination directory configured");
            return new PlaylistExportResult(true, 0, 0, 0, []);
        }

        var folderName = string.IsNullOrWhiteSpace(opts.PlaylistsFolderName) ? "Playlists" : opts.PlaylistsFolderName;
        var playlistsDir = Path.Combine(destinationRoot, folderName);

        var collections = await GatherCollectionsAsync(ct);

        var summaries = new List<ExportedPlaylistSummary>();
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        var totalTracks = 0;
        var matchedTracks = 0;

        foreach (var collection in collections)
        {
            ct.ThrowIfCancellationRequested();

            var entries = await ResolveEntriesAsync(collection.Tracks, ct);
            var fileName = BuildUniqueFileName(collection, usedFileNames);
            var filePath = Path.Combine(playlistsDir, fileName);

            await writer.WriteAsync(filePath, playlistsDir, entries, ct);
            await UpsertRowAsync(collection, filePath, collection.Tracks.Count, entries.Count, ct);

            seenKeys.Add(CollectionKey(collection.Kind, collection.SpotifyPlaylistId));
            totalTracks += collection.Tracks.Count;
            matchedTracks += entries.Count;
            summaries.Add(new ExportedPlaylistSummary(
                collection.Kind.ToString(), collection.Name, filePath, collection.Tracks.Count, entries.Count));

            logger.LogInformation(
                "Exported playlist {Name}: {Matched}/{Total} tracks → {Path}",
                collection.Name, entries.Count, collection.Tracks.Count, filePath);
        }

        await CleanupOrphansAsync(playlistsDir, seenKeys, ct);

        logger.LogInformation(
            "Playlist export finished: {Playlists} playlists, {Matched}/{Total} tracks written",
            summaries.Count, matchedTracks, totalTracks);

        return new PlaylistExportResult(true, summaries.Count, totalTracks, matchedTracks, summaries);
    }

    private sealed record ExportCollection(
        ExportedPlaylistKind Kind,
        string? SpotifyPlaylistId,
        string Name,
        IReadOnlyList<SpotifyTrackItem> Tracks);

    private async Task<List<ExportCollection>> GatherCollectionsAsync(CancellationToken ct)
    {
        var collections = new List<ExportCollection>();

        // Liked Songs first — Spotify returns /me/tracks newest-first, which is exactly the requested
        // "liked-date descending" order, so no re-sort is needed.
        var liked = await PageAllAsync((offset, ct2) => spotifyApi.GetLikedSongsAsync(offset, 50, ct2),
            r => (r.Items, r.Total), ct);
        collections.Add(new ExportCollection(ExportedPlaylistKind.LikedSongs, null, "Liked Songs", liked));

        var playlists = await spotifyApi.GetPlaylistsAsync(ct);
        foreach (var playlist in playlists.Items)
        {
            ct.ThrowIfCancellationRequested();
            var tracks = await PageAllAsync(
                (offset, ct2) => spotifyApi.GetPlaylistTracksAsync(playlist.SpotifyId, offset, 50, ct2),
                r => (r.Items, r.Total), ct);
            collections.Add(new ExportCollection(ExportedPlaylistKind.Playlist, playlist.SpotifyId, playlist.Name, tracks));
        }

        return collections;
    }

    private static async Task<List<SpotifyTrackItem>> PageAllAsync<TResponse>(
        Func<int, CancellationToken, Task<TResponse>> fetchPage,
        Func<TResponse, (IReadOnlyList<SpotifyTrackItem> Items, int Total)> select,
        CancellationToken ct)
    {
        var all = new List<SpotifyTrackItem>();
        var offset = 0;
        while (true)
        {
            var page = await fetchPage(offset, ct);
            var (items, total) = select(page);
            if (items.Count == 0)
                break;
            all.AddRange(items);
            offset += items.Count;
            if (offset >= total)
                break;
        }
        return all;
    }

    /// <summary>
    /// Resolves the Spotify tracks (in order) to local built-track playlist entries. Recomputes matches
    /// fresh via the comparison matcher (the cache can be stale as songs get built), keeps only
    /// confident <see cref="ComparisonMatchStatus.InLibrary"/> matches that are actually built.
    /// </summary>
    private async Task<List<M3uEntry>> ResolveEntriesAsync(IReadOnlyList<SpotifyTrackItem> tracks, CancellationToken ct)
    {
        if (tracks.Count == 0)
            return [];

        var matches = await comparison.UpsertMatchesForTracksAsync(
            tracks, SpotifyLibraryComparisonService.SourcePlaylistExport, ct);

        var matchedSongIds = matches.Values
            .Where(m => string.Equals(m.MatchStatus, nameof(ComparisonMatchStatus.InLibrary), StringComparison.OrdinalIgnoreCase)
                && m.MatchedSongId is > 0)
            .Select(m => m.MatchedSongId!.Value)
            .Distinct()
            .ToList();

        var builtById = await LoadBuiltSongsAsync(matchedSongIds, ct);

        var entries = new List<M3uEntry>();
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.SpotifyId))
                continue;
            if (!matches.TryGetValue(track.SpotifyId, out var info))
                continue;
            if (info.MatchedSongId is not > 0
                || !string.Equals(info.MatchStatus, nameof(ComparisonMatchStatus.InLibrary), StringComparison.OrdinalIgnoreCase))
                continue;
            if (!builtById.TryGetValue(info.MatchedSongId.Value, out var song))
                continue;

            entries.Add(new M3uEntry(
                song.DestinationPath,
                string.IsNullOrWhiteSpace(song.Artist) ? track.Artist : song.Artist,
                string.IsNullOrWhiteSpace(song.Title) ? track.Title : song.Title,
                song.DurationSeconds ?? (track.DurationMs > 0 ? track.DurationMs / 1000 : null)));
        }

        return entries;
    }

    private sealed record BuiltSong(int Id, string DestinationPath, string? Artist, string? Title, int? DurationSeconds);

    private async Task<Dictionary<int, BuiltSong>> LoadBuiltSongsAsync(IReadOnlyList<int> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
            return [];

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ownerId = ownerLookup.OwnerUserId;

        var rows = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.OwnerUserId == ownerId
                && ids.Contains(s.Id)
                && s.DeletedAtUtc == null
                && s.LibraryBuildStatus == LibraryBuildStatus.Done
                && s.DestinationPath != null)
            .Select(s => new BuiltSong(s.Id, s.DestinationPath!, s.Artist, s.Title, s.DurationSeconds))
            .ToListAsync(ct);

        return rows.ToDictionary(r => r.Id);
    }

    private async Task UpsertRowAsync(ExportCollection collection, string filePath, int total, int matched, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ownerId = ownerLookup.OwnerUserId;
        var now = DateTime.UtcNow;

        var row = await db.ExportedPlaylists
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OwnerUserId == ownerId
                && e.Kind == collection.Kind
                && e.SpotifyPlaylistId == collection.SpotifyPlaylistId, ct);

        if (row is null)
        {
            row = new ExportedPlaylist
            {
                OwnerUserId = ownerId,
                Kind = collection.Kind,
                SpotifyPlaylistId = collection.SpotifyPlaylistId,
            };
            db.ExportedPlaylists.Add(row);
        }
        else if (!string.Equals(row.FilePath, filePath, StringComparison.Ordinal))
        {
            // The collection was renamed on Spotify (same id, new sanitized filename): drop the old file.
            TryDeletePlaylistFile(row.FilePath, Path.GetDirectoryName(filePath));
        }

        row.Name = collection.Name;
        row.FilePath = filePath;
        row.SpotifyTrackTotal = total;
        row.MatchedTrackCount = matched;
        row.LastGeneratedAtUtc = now;
        row.UpdatedAtUtc = now;

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Removes <c>.m3u8</c> files + rows for collections no longer present on Spotify.</summary>
    private async Task CleanupOrphansAsync(string playlistsDir, HashSet<string> seenKeys, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ownerId = ownerLookup.OwnerUserId;

        var rows = await db.ExportedPlaylists
            .IgnoreQueryFilters()
            .Where(e => e.OwnerUserId == ownerId)
            .ToListAsync(ct);

        var orphans = rows.Where(r => !seenKeys.Contains(CollectionKey(r.Kind, r.SpotifyPlaylistId))).ToList();
        if (orphans.Count == 0)
            return;

        foreach (var orphan in orphans)
        {
            TryDeletePlaylistFile(orphan.FilePath, playlistsDir);
            db.ExportedPlaylists.Remove(orphan);
            logger.LogInformation("Removed orphaned playlist export {Name} ({Path})", orphan.Name, orphan.FilePath);
        }

        await db.SaveChangesAsync(ct);
    }

    // Only ever delete files that live inside the playlists directory — never follow a stale absolute
    // path out of the managed folder.
    private void TryDeletePlaylistFile(string? filePath, string? playlistsDir)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(playlistsDir))
            return;
        try
        {
            var full = Path.GetFullPath(filePath);
            var dir = Path.GetFullPath(playlistsDir);
            if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                return;
            if (File.Exists(full))
                File.Delete(full);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to delete playlist file {Path}", filePath);
        }
    }

    private static string CollectionKey(ExportedPlaylistKind kind, string? spotifyPlaylistId)
        => $"{kind}\0{spotifyPlaylistId ?? string.Empty}";

    private static string BuildUniqueFileName(ExportCollection collection, HashSet<string> used)
    {
        var baseName = DestinationPathResolver.Sanitize(collection.Name).Trim();
        if (baseName.Length == 0)
            baseName = collection.Kind == ExportedPlaylistKind.LikedSongs ? "Liked Songs" : "Playlist";
        if (baseName.Length > 100)
            baseName = baseName[..100].Trim();

        var candidate = baseName;
        // Disambiguate collisions deterministically with a short slice of the Spotify playlist id.
        if (used.Contains(candidate + ".m3u8") && !string.IsNullOrEmpty(collection.SpotifyPlaylistId))
        {
            var slice = collection.SpotifyPlaylistId.Length <= 6
                ? collection.SpotifyPlaylistId
                : collection.SpotifyPlaylistId[..6];
            candidate = $"{baseName} ({slice})";
        }

        var fileName = candidate + ".m3u8";
        var suffix = 2;
        while (!used.Add(fileName))
        {
            fileName = $"{candidate} ({suffix}).m3u8";
            suffix++;
        }
        return fileName;
    }
}

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
    /// Mirrors the owner's <b>subscribed</b> Spotify collections (Liked Songs and/or specific
    /// playlists) to <c>.m3u8</c> files in the destination library. Export is opt-in: only collections
    /// the owner has subscribed (an <see cref="ExportedPlaylist"/> row exists) are written — nothing is
    /// synced by default. Returns <c>Ran=false</c> if another run is already in progress.
    /// </summary>
    Task<PlaylistExportResult> RunExportAsync(CancellationToken ct = default);

    /// <summary>
    /// Subscribes a Spotify collection so it is mirrored on the next export run. Idempotent: re-subscribing
    /// just refreshes the stored name. Does not write the file itself — call <see cref="RunExportAsync"/>
    /// afterwards (the endpoint kicks that off the request path).
    /// </summary>
    Task<ExportedPlaylist> SubscribeAsync(
        ExportedPlaylistKind kind, string? spotifyPlaylistId, string name, CancellationToken ct = default);

    /// <summary>
    /// Unsubscribes a collection: deletes its <c>.m3u8</c> file and the subscription row. Returns
    /// <c>false</c> if no such subscription exists for the owner.
    /// </summary>
    Task<bool> UnsubscribeAsync(int id, CancellationToken ct = default);
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
    // Serialize export runs: the periodic background tick, the manual regenerate button and a
    // subscribe-triggered run must never write the same files concurrently.
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
        var playlistsDir = ResolvePlaylistsDir();
        if (playlistsDir is null)
        {
            logger.LogWarning("Playlist export skipped: no destination directory configured");
            return new PlaylistExportResult(true, 0, 0, 0, []);
        }

        // Opt-in: only collections the owner has subscribed are exported. No subscriptions → no-op.
        var subscriptions = await LoadSubscriptionsAsync(ct);
        if (subscriptions.Count == 0)
        {
            logger.LogInformation("Playlist export: no subscribed collections — nothing to export");
            return new PlaylistExportResult(true, 0, 0, 0, []);
        }

        // Live playlist snapshot drives rename + deletion detection, but only matters if a playlist (not
        // just Liked Songs) is subscribed — skip the call otherwise.
        Dictionary<string, SpotifyPlaylistItem>? liveById = null;
        if (subscriptions.Any(s => s.Kind == ExportedPlaylistKind.Playlist))
        {
            var live = await spotifyApi.GetPlaylistsAsync(ct);
            liveById = live.Items
                .GroupBy(p => p.SpotifyId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        }

        var summaries = new List<ExportedPlaylistSummary>();
        var usedFileNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var totalTracks = 0;
        var matchedTracks = 0;

        foreach (var sub in subscriptions)
        {
            ct.ThrowIfCancellationRequested();

            string name;
            if (sub.Kind == ExportedPlaylistKind.Playlist)
            {
                if (liveById is null || !liveById.TryGetValue(sub.SpotifyPlaylistId ?? string.Empty, out var live))
                {
                    // Subscribed playlist was deleted or unfollowed on Spotify → drop file + subscription.
                    await UnsubscribeAsync(sub.Id, ct);
                    logger.LogInformation("Removed subscription for playlist gone from Spotify: {Name}", sub.Name);
                    continue;
                }
                name = live.Name;
            }
            else
            {
                name = "Liked Songs";
            }

            var tracks = await FetchTracksAsync(sub.Kind, sub.SpotifyPlaylistId, ct);
            var collection = new ExportCollection(sub.Kind, sub.SpotifyPlaylistId, name, tracks);
            var (total, matched, summary) = await ExportCollectionAsync(collection, playlistsDir, usedFileNames, ct);
            totalTracks += total;
            matchedTracks += matched;
            summaries.Add(summary);
        }

        logger.LogInformation(
            "Playlist export finished: {Playlists} playlists, {Matched}/{Total} tracks written",
            summaries.Count, matchedTracks, totalTracks);

        return new PlaylistExportResult(true, summaries.Count, totalTracks, matchedTracks, summaries);
    }

    public async Task<ExportedPlaylist> SubscribeAsync(
        ExportedPlaylistKind kind, string? spotifyPlaylistId, string name, CancellationToken ct = default)
    {
        // Liked Songs is a singleton keyed by a null playlist id; a playlist needs its Spotify id.
        var normalizedId = kind == ExportedPlaylistKind.LikedSongs ? null : spotifyPlaylistId;
        if (kind == ExportedPlaylistKind.Playlist && string.IsNullOrWhiteSpace(normalizedId))
            throw new ArgumentException("A playlist subscription requires a Spotify playlist id.", nameof(spotifyPlaylistId));

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ownerId = ownerLookup.OwnerUserId;
        var now = DateTime.UtcNow;

        var row = await db.ExportedPlaylists
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.OwnerUserId == ownerId
                && e.Kind == kind
                && e.SpotifyPlaylistId == normalizedId, ct);

        var resolvedName = string.IsNullOrWhiteSpace(name)
            ? (kind == ExportedPlaylistKind.LikedSongs ? "Liked Songs" : "Playlist")
            : name;

        if (row is null)
        {
            row = new ExportedPlaylist
            {
                OwnerUserId = ownerId,
                Kind = kind,
                SpotifyPlaylistId = normalizedId,
                Name = resolvedName,
                // FilePath / counts stay empty until the first export run writes the .m3u8.
                FilePath = string.Empty,
                UpdatedAtUtc = now,
            };
            db.ExportedPlaylists.Add(row);
        }
        else
        {
            row.Name = resolvedName;
            row.UpdatedAtUtc = now;
        }

        await db.SaveChangesAsync(ct);
        return row;
    }

    public async Task<bool> UnsubscribeAsync(int id, CancellationToken ct = default)
    {
        var playlistsDir = ResolvePlaylistsDir();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ownerId = ownerLookup.OwnerUserId;

        var row = await db.ExportedPlaylists
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(e => e.Id == id && e.OwnerUserId == ownerId, ct);
        if (row is null)
            return false;

        if (playlistsDir is not null)
            TryDeletePlaylistFile(row.FilePath, playlistsDir);
        db.ExportedPlaylists.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private sealed record Subscription(int Id, ExportedPlaylistKind Kind, string? SpotifyPlaylistId, string Name);

    private async Task<List<Subscription>> LoadSubscriptionsAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ownerId = ownerLookup.OwnerUserId;

        return await db.ExportedPlaylists
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(e => e.OwnerUserId == ownerId)
            // Liked Songs (Kind 0) first, then playlists by name — keeps file dedup deterministic.
            .OrderBy(e => e.Kind)
            .ThenBy(e => e.Name)
            .Select(e => new Subscription(e.Id, e.Kind, e.SpotifyPlaylistId, e.Name))
            .ToListAsync(ct);
    }

    private async Task<IReadOnlyList<SpotifyTrackItem>> FetchTracksAsync(
        ExportedPlaylistKind kind, string? spotifyPlaylistId, CancellationToken ct)
    {
        // Liked Songs: Spotify returns /me/tracks newest-first, which is the requested "liked-date
        // descending" order, so no re-sort is needed.
        if (kind == ExportedPlaylistKind.LikedSongs)
            return await PageAllAsync(
                (offset, ct2) => spotifyApi.GetLikedSongsAsync(offset, 50, ct2), r => (r.Items, r.Total), ct);

        return await PageAllAsync(
            (offset, ct2) => spotifyApi.GetPlaylistTracksAsync(spotifyPlaylistId!, offset, 50, ct2),
            r => (r.Items, r.Total), ct);
    }

    private sealed record ExportCollection(
        ExportedPlaylistKind Kind,
        string? SpotifyPlaylistId,
        string Name,
        IReadOnlyList<SpotifyTrackItem> Tracks);

    private async Task<(int Total, int Matched, ExportedPlaylistSummary Summary)> ExportCollectionAsync(
        ExportCollection collection, string playlistsDir, HashSet<string> usedFileNames, CancellationToken ct)
    {
        var entries = await ResolveEntriesAsync(collection.Tracks, ct);
        var fileName = BuildUniqueFileName(collection, usedFileNames);
        var filePath = Path.Combine(playlistsDir, fileName);

        await writer.WriteAsync(filePath, playlistsDir, entries, ct);
        await UpsertRowAsync(collection, filePath, collection.Tracks.Count, entries.Count, ct);

        logger.LogInformation(
            "Exported playlist {Name}: {Matched}/{Total} tracks → {Path}",
            collection.Name, entries.Count, collection.Tracks.Count, filePath);

        return (collection.Tracks.Count, entries.Count, new ExportedPlaylistSummary(
            collection.Kind.ToString(), collection.Name, filePath, collection.Tracks.Count, entries.Count));
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
        else if (!string.IsNullOrEmpty(row.FilePath) && !string.Equals(row.FilePath, filePath, StringComparison.Ordinal))
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

    private string? ResolvePlaylistsDir()
    {
        var opts = options.Value;
        if (string.IsNullOrWhiteSpace(opts.DestinationDirectory))
            return null;
        var folderName = string.IsNullOrWhiteSpace(opts.PlaylistsFolderName) ? "Playlists" : opts.PlaylistsFolderName;
        return Path.Combine(opts.DestinationDirectory, folderName);
    }

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

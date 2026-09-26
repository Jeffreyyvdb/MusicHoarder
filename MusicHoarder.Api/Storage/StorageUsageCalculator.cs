using System.Diagnostics;
using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Download;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Storage;

/// <summary>
/// Measures what MusicHoarder's files occupy on disk by walking every managed root and classifying
/// each file by where it is (which root), what it is (extension) and whether a live song row still
/// points at it. Audio the library knows is credited to its provenance (via
/// <see cref="SongOriginResolver"/>) using the file's real on-disk length, so the origin buckets sum
/// to the library bucket exactly; audio nothing points at is reported as untracked, per root — which
/// is where leftover copies from bugs and soft-deleted rows surface.
/// <para>
/// This is a measurement, not DB arithmetic, on purpose: covers, videos, the thumbnail cache and
/// orphans have no size columns, and <c>FileSizeBytes</c> is the <em>source</em> file's size — it
/// says nothing about the destination copy, and stays on the row after the staged source is released.
/// A walk of a large SMB share takes minutes, so this runs only from
/// <see cref="StorageUsageBackgroundService"/> and is served from <see cref="StorageUsageSnapshotStore"/>.
/// </para>
/// <para>
/// Runs in a hosted-service scope, where the ambient tenant filter resolves to "no rows" (the current
/// user is <see cref="Guid.Empty"/>), so every query here bypasses it with <c>IgnoreQueryFilters()</c>
/// and drops the demo tenant explicitly — disk usage is instance-wide.
/// </para>
/// </summary>
public sealed class StorageUsageCalculator(
    MusicHoarderDbContext db,
    IFileSystem fileSystem,
    IOptions<MusicEnricherOptions> options,
    IOptionsMonitor<SyncOptions> syncOptions,
    IOptions<SlskdOptions> slskdOptions,
    ICoverThumbnailService thumbnails,
    IDirectoryAvailability availability,
    IStorageVolumeProbe volumeProbe,
    StagedSourceReleaseService stagedRelease,
    IOwnerLookupService ownerLookup,
    ILogger<StorageUsageCalculator> logger)
{
    internal const string SkipOffline = "offline";
    internal const string SkipError = "error";

    private static readonly HashSet<string> PlaylistExtensions = [".m3u", ".m3u8"];

    private static readonly EnumerationOptions Recursive = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = true,
    };

    /// <summary>A live song row's claim on a path, plus what the breakdown wants to know about it.</summary>
    private sealed record RowRef(int SongId, bool IsDuplicate, string OriginKey);

    private sealed class Root(string key, string path)
    {
        public string Key { get; } = key;
        public string Path { get; } = path;
        public string Prefix { get; } = IndexService.NormalizeRootPrefix(path);
        public bool Exists { get; set; }
        public bool Walked { get; set; }
        public string? Skipped { get; set; }

        /// <summary>A walked root this one sits inside; its files are counted by that outer walk.</summary>
        public Root? Outer { get; set; }

        /// <summary>Another root configured at the very same path; this one is a duplicate entry.</summary>
        public Root? SameAs { get; set; }

        public long Bytes;
        public int Files;
        public long UntrackedBytes;
        public int UntrackedFiles;
        public long DurationMs;

        public StorageRoot ToDto() => new(
            Key,
            Path,
            SameAs?.Exists ?? Exists,
            Walked || (Outer?.Walked ?? false) || (SameAs?.Walked ?? false),
            Skipped,
            Bytes,
            Files,
            UntrackedBytes,
            UntrackedFiles,
            DurationMs);
    }

    private sealed class Totals
    {
        public readonly Dictionary<string, (long Bytes, int Files)> Categories = new(StringComparer.Ordinal);
        public readonly Dictionary<string, (long Bytes, int Files)> Origins = new(StringComparer.Ordinal);
        public readonly HashSet<int> DuplicateSongs = [];
        public long DuplicateBytes;

        public static void Add(Dictionary<string, (long Bytes, int Files)> into, string key, long bytes)
        {
            var current = into.GetValueOrDefault(key);
            into[key] = (current.Bytes + bytes, current.Files + 1);
        }
    }

    public async Task<StorageUsageSnapshot> ComputeAsync(CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var opts = options.Value;

        var roots = ResolveRoots(opts);
        var index = await LoadRowIndexAsync(opts, ct);

        var totals = new Totals();
        // Longest prefix first, so a file inside a nested root is credited to that root, not its parent.
        var byLongestPrefix = roots
            .Where(r => r.SameAs is null)
            .OrderByDescending(r => r.Prefix.Length)
            .ToList();
        foreach (var root in roots.Where(r => r.Outer is null && r.SameAs is null && r.Skipped is null && r.Exists))
            Walk(root, byLongestPrefix, index, totals, ct);

        var volumes = ProbeVolumes(roots);
        var lyrics = await LoadLyricsAsync(ct);
        var reclaimable = await LoadReclaimableAsync(ct);

        var categories = CategoryKeys.All.Select(k => Bucket(totals.Categories, k)).ToList();
        var origins = OriginKeys.All.Select(k => Bucket(totals.Origins, k)).ToList();

        return new StorageUsageSnapshot(
            ComputedAtUtc: DateTime.UtcNow,
            DurationMs: stopwatch.ElapsedMilliseconds,
            ManagedBytes: categories.Sum(c => c.Bytes),
            CapacityBytes: volumes.Sum(v => v.TotalBytes),
            FreeBytes: volumes.Sum(v => v.FreeBytes),
            Volumes: volumes,
            Roots: roots.Select(r => r.ToDto()).ToList(),
            Categories: categories,
            Origins: origins,
            Duplicates: new StorageDuplicates(totals.DuplicateBytes, totals.DuplicateSongs.Count),
            Lyrics: lyrics,
            Reclaimable: reclaimable);
    }

    private static StorageBucket Bucket(Dictionary<string, (long Bytes, int Files)> from, string key)
    {
        var value = from.GetValueOrDefault(key);
        return new StorageBucket(key, value.Bytes, value.Files);
    }

    // ── roots ───────────────────────────────────────────────────────────────

    private List<Root> ResolveRoots(MusicEnricherOptions opts)
    {
        var reach = availability.Current;
        // Before the monitor's first probe the snapshot is the all-false placeholder; trust the disk then.
        var probed = reach.CheckedAtUtc != DateTime.MinValue;

        var candidates = new (string Key, string? Path, string? Skipped)[]
        {
            (RootKeys.Source, opts.SourceDirectory, probed && !reach.SourceAvailable ? SkipOffline : null),
            (RootKeys.Destination, opts.DestinationDirectory, probed && !reach.DestinationAvailable ? SkipOffline : null),
            (RootKeys.Download, opts.DownloadDirectory, null),
            (RootKeys.Videos, MusicVideoDownloader.ResolveVideoDirectory(opts), null),
            (RootKeys.Synced, syncOptions.CurrentValue.SyncedSourceDirectory, null),
            (RootKeys.CoverCache, thumbnails.CacheDirectory, null),
            (RootKeys.Slskd, slskdOptions.Value.DownloadsDirectory, null),
        };

        var roots = new List<Root>();
        foreach (var (key, path, skipped) in candidates)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            var root = new Root(key, path) { Skipped = skipped };
            var same = roots.FirstOrDefault(r => string.Equals(r.Prefix, root.Prefix, StringComparison.Ordinal));
            if (same is not null)
            {
                root.SameAs = same;
                root.Skipped = $"same as {same.Key}";
            }
            else if (root.Skipped is null)
            {
                root.Exists = DirectoryExists(path);
            }
            roots.Add(root);
        }

        // Nesting is the normal case (videos inside downloads, Playlists/ inside destination): walk
        // each outermost root once and let the longest-prefix match credit files to the inner root.
        var walkable = roots
            .Where(r => r.SameAs is null && r.Exists && r.Skipped is null)
            .OrderBy(r => r.Prefix.Length)
            .ToList();
        foreach (var root in walkable)
        {
            root.Outer = walkable.FirstOrDefault(o =>
                !ReferenceEquals(o, root)
                && o.Outer is null
                && root.Prefix.StartsWith(o.Prefix, StringComparison.Ordinal));
        }

        return roots;
    }

    private bool DirectoryExists(string path)
    {
        try
        {
            return fileSystem.Directory.Exists(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    // ── walk ────────────────────────────────────────────────────────────────

    private void Walk(Root root, List<Root> byLongestPrefix, Dictionary<string, RowRef> index, Totals totals, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var files = 0;
        try
        {
            foreach (var file in fileSystem.Directory.EnumerateFiles(root.Path, "*", Recursive))
            {
                ct.ThrowIfCancellationRequested();

                long length;
                try
                {
                    length = fileSystem.FileInfo.New(file).Length;
                }
                catch (Exception)
                {
                    // Vanished mid-walk or unreadable — not worth failing the whole root over.
                    continue;
                }

                var path = file.Replace('\\', '/');
                var owner = byLongestPrefix.FirstOrDefault(r => path.StartsWith(r.Prefix, StringComparison.Ordinal)) ?? root;
                Classify(owner, path, length, index, totals);
                files++;
            }

            root.Walked = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            root.Skipped = SkipError;
            logger.LogWarning(ex, "Storage walk of {Root} ({Path}) failed after {Files} files", root.Key, root.Path, files);
        }
        finally
        {
            root.DurationMs = stopwatch.ElapsedMilliseconds;
        }

        logger.LogInformation("Storage walk of {Root} ({Path}): {Files} files in {Ms} ms", root.Key, root.Path, files, root.DurationMs);
    }

    private static void Classify(Root root, string path, long length, Dictionary<string, RowRef> index, Totals totals)
    {
        root.Bytes += length;
        root.Files++;

        string category;
        if (IndexService.HasHiddenSegment(path, root.Prefix))
        {
            category = CategoryKeys.Temp;
        }
        else if (root.Key == RootKeys.CoverCache)
        {
            category = CategoryKeys.ThumbnailCache;
        }
        else if (root.Key == RootKeys.Videos)
        {
            category = CategoryKeys.Videos;
        }
        else if (root.Key == RootKeys.Slskd)
        {
            category = CategoryKeys.SoulseekStaging;
        }
        else
        {
            var extension = Path.GetExtension(path).ToLowerInvariant();
            if (IndexService.SupportedExtensions.Contains(extension))
            {
                if (index.TryGetValue(path, out var row))
                {
                    category = root.Key switch
                    {
                        RootKeys.Source => CategoryKeys.Source,
                        RootKeys.Destination => CategoryKeys.Library,
                        RootKeys.Download => CategoryKeys.Staging,
                        RootKeys.Synced => CategoryKeys.Synced,
                        _ => CategoryKeys.Other,
                    };
                    if (category == CategoryKeys.Library)
                        Totals.Add(totals.Origins, row.OriginKey, length);
                    if (row.IsDuplicate)
                    {
                        totals.DuplicateSongs.Add(row.SongId);
                        totals.DuplicateBytes += length;
                    }
                }
                else
                {
                    category = CategoryKeys.Untracked;
                    root.UntrackedBytes += length;
                    root.UntrackedFiles++;
                }
            }
            else if (CoverArtResolver.ImageExtensions.Contains(extension))
            {
                category = CategoryKeys.Covers;
            }
            else if (PlaylistExtensions.Contains(extension))
            {
                category = CategoryKeys.Playlists;
            }
            else
            {
                category = CategoryKeys.Other;
            }
        }

        Totals.Add(totals.Categories, category, length);
    }

    // ── database ────────────────────────────────────────────────────────────

    /// <summary>
    /// Every path a live row still claims: the source (unless its staged copy was released), the
    /// destination, and the previous destination a re-tag is about to overwrite. Soft-deleted rows are
    /// left out on purpose — a file they leave behind is exactly what "untracked" should show.
    /// </summary>
    private async Task<Dictionary<string, RowRef>> LoadRowIndexAsync(MusicEnricherOptions opts, CancellationToken ct)
    {
        var saveDates = await SpotifySaveDates.LoadAsync(db, ct, allTenants: true);
        var syncedDirectory = syncOptions.CurrentValue.SyncedSourceDirectory;

        var rows = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .ExcludingDemoTenant()
            .Where(s => !s.IsSynthetic && s.DeletedAtUtc == null)
            .Select(s => new
            {
                s.Id,
                s.SourcePath,
                s.DestinationPath,
                s.PreviousDestinationPath,
                s.SourceReleasedAtUtc,
                s.IsDuplicate,
            })
            .ToListAsync(ct);

        var index = new Dictionary<string, RowRef>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            var origin = SongOriginResolver.Resolve(row.SourcePath, saveDates.LinkFor(row.Id), opts.DownloadDirectory, syncedDirectory);
            var reference = new RowRef(row.Id, row.IsDuplicate, OriginKeyFor(origin));
            if (row.SourceReleasedAtUtc is null)
                index.TryAdd(Normalize(row.SourcePath), reference);
            if (row.DestinationPath is not null)
                index.TryAdd(Normalize(row.DestinationPath), reference);
            if (row.PreviousDestinationPath is not null)
                index.TryAdd(Normalize(row.PreviousDestinationPath), reference);
        }

        return index;
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    private static string OriginKeyFor(SongOrigin origin) => origin.Source switch
    {
        SongOriginSource.SpotifyLiked => OriginKeys.SpotifyLiked,
        SongOriginSource.SpotifyPlaylist => OriginKeys.SpotifyPlaylist,
        SongOriginSource.DeezerPlaylist => OriginKeys.DeezerPlaylist,
        SongOriginSource.YouTubePlaylist => OriginKeys.YouTubePlaylist,
        SongOriginSource.DirectUrl => OriginKeys.DirectUrl,
        SongOriginSource.AlbumCompletion => OriginKeys.AlbumCompletion,
        _ => origin.Kind switch
        {
            SongOriginKind.Synced => OriginKeys.Synced,
            SongOriginKind.Downloaded => OriginKeys.OtherDownload,
            _ => OriginKeys.Local,
        },
    };

    /// <summary>
    /// Lyrics never become files: they live in these columns and are embedded in the audio tag at
    /// build. Character count is the honest proxy the popup can show — it is labelled approximate.
    /// </summary>
    private async Task<StorageLyrics> LoadLyricsAsync(CancellationToken ct)
    {
        try
        {
            var withLyrics = db.Songs
                .IgnoreQueryFilters()
                .AsNoTracking()
                .ExcludingDemoTenant()
                .Where(s => !s.IsSynthetic && s.DeletedAtUtc == null)
                .Where(s => s.PlainLyrics != null || s.SyncedLyrics != null
                    || s.TranscribedSyncedLyrics != null || s.TranscribedPlainLyrics != null
                    || s.RomanizedSyncedLyrics != null || s.RomanizedPlainLyrics != null
                    || s.TranslatedSyncedLyrics != null || s.TranslatedPlainLyrics != null);

            var tracks = await withLyrics.CountAsync(ct);
            if (tracks == 0)
                return new StorageLyrics(0, 0);

            var characters = await withLyrics.SumAsync(s => (long)(
                (s.PlainLyrics ?? "").Length + (s.SyncedLyrics ?? "").Length
                + (s.TranscribedSyncedLyrics ?? "").Length + (s.TranscribedPlainLyrics ?? "").Length
                + (s.RomanizedSyncedLyrics ?? "").Length + (s.RomanizedPlainLyrics ?? "").Length
                + (s.TranslatedSyncedLyrics ?? "").Length + (s.TranslatedPlainLyrics ?? "").Length), ct);

            return new StorageLyrics(characters, tracks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lyrics size estimate failed; reporting none");
            return new StorageLyrics(0, 0);
        }
    }

    private async Task<StorageReclaimable> LoadReclaimableAsync(CancellationToken ct)
    {
        try
        {
            // The owner id, not the ambient user: this runs in a hosted-service scope.
            var preview = await stagedRelease.PreviewAsync(ownerLookup.OwnerUserId, ct);
            return new StorageReclaimable(preview.EligibleBytes, preview.Eligible, preview.UnavailableReason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Staged-source reclaim preview failed; reporting none");
            return new StorageReclaimable(0, 0, SkipError);
        }
    }

    // ── volumes ─────────────────────────────────────────────────────────────

    private List<StorageVolume> ProbeVolumes(List<Root> roots)
    {
        var volumes = new List<StorageVolume>();
        foreach (var root in roots.Where(r => r.SameAs is null && r.Exists && r.Skipped is null))
        {
            var capacity = volumeProbe.Probe(root.Path);
            if (capacity is null) continue;
            // Two bind mounts of one host filesystem report identical figures; count that volume once.
            if (volumes.Any(v => v.TotalBytes == capacity.TotalBytes && v.FreeBytes == capacity.FreeBytes))
                continue;
            volumes.Add(new StorageVolume(root.Path, capacity.TotalBytes, capacity.FreeBytes));
        }
        return volumes;
    }
}

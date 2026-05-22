using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Enrichment;
using MusicHoarder.Api.Jobs;
using MusicHoarder.Api.Library;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Endpoints;

public static class DashboardEndpoints
{
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/stats", GetStats).WithName("GetStats");
        app.MapGet("/overview", GetOverview).WithName("GetOverview");
        app.MapGet("/library/directory-tree", GetDirectoryTree).WithName("GetDirectoryTree");
        app.MapGet("/library/directory-tree/files", GetDirectoryFiles).WithName("GetDirectoryFiles");
        return app;
    }

    private static async Task<IResult> GetDirectoryTree(
        MusicHoarderDbContext db,
        IOptions<MusicEnricherOptions> options)
    {
        var rows = await db.Songs
            .Where(s => s.DeletedAtUtc == null)
            .Select(s => new { s.SourcePath, s.EnrichmentStatus, s.LibraryBuildStatus, s.FileSizeBytes })
            .ToListAsync();

        var root = BuildMatchTree(
            rows.Select(r => new MatchTreeRow(r.SourcePath, r.EnrichmentStatus, r.LibraryBuildStatus, r.FileSizeBytes)),
            options.Value.SourceDirectory);

        return Results.Ok(root.ToDto());
    }

    /// <summary>
    /// Lists the songs that live <em>directly</em> inside a single source folder (not in nested
    /// sub-folders), so the directory-tree UI can lazily drill into a folder's actual files.
    /// </summary>
    private static async Task<IResult> GetDirectoryFiles(
        string? path,
        MusicHoarderDbContext db,
        IOptions<MusicEnricherOptions> options)
    {
        var prefix = EnrichmentEndpoints.ResolveFolderPrefix(options.Value.SourceDirectory, path ?? string.Empty);
        var prefixSlash = prefix.Length == 0 ? string.Empty : prefix + "/";

        // Narrow to the folder's subtree in SQL (translatable + InMemory-test friendly, same as the
        // enrich/folder endpoint), then keep only direct children — paths whose remainder has no '/'.
        var candidates = await db.Songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null)
            .Where(s => prefixSlash == "" || s.SourcePath.StartsWith(prefixSlash))
            .Select(s => new
            {
                s.Id,
                s.SourcePath,
                s.FileName,
                s.Extension,
                s.FileSizeBytes,
                s.EnrichmentStatus,
                s.LibraryBuildStatus,
                s.MatchConfidence,
                s.DestinationPath,
            })
            .ToListAsync();

        var files = candidates
            .Where(s => IsDirectChild(NormalizePath(s.SourcePath), prefix))
            .OrderBy(s => s.FileName, StringComparer.OrdinalIgnoreCase)
            .Select(s => new
            {
                s.Id,
                s.FileName,
                s.Extension,
                s.FileSizeBytes,
                EnrichmentStatus = s.EnrichmentStatus.ToString(),
                LibraryBuildStatus = s.LibraryBuildStatus.ToString(),
                s.MatchConfidence,
                s.DestinationPath,
                State = DeriveFileState(s.EnrichmentStatus, s.LibraryBuildStatus),
            })
            .ToList();

        return Results.Ok(new { Path = path ?? string.Empty, Count = files.Count, Files = files });
    }

    internal static bool IsDirectChild(string normalizedSourcePath, string prefix)
    {
        if (prefix.Length == 0)
            return !normalizedSourcePath.Contains('/');
        if (!normalizedSourcePath.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
            return false;
        var remainder = normalizedSourcePath[(prefix.Length + 1)..];
        return remainder.Length > 0 && !remainder.Contains('/');
    }

    // Collapses the two persisted status enums into the single per-file state the UI renders.
    internal static string DeriveFileState(EnrichmentStatus enrichment, LibraryBuildStatus build)
        => build == LibraryBuildStatus.Done
            ? "written"
            : enrichment switch
            {
                EnrichmentStatus.Matched => "matched",
                EnrichmentStatus.NeedsReview => "review",
                EnrichmentStatus.Failed => "failed",
                _ => "queued",
            };

    internal readonly record struct MatchTreeRow(
        string SourcePath,
        EnrichmentStatus EnrichmentStatus,
        LibraryBuildStatus LibraryBuildStatus,
        long FileSizeBytes = 0);

    /// <summary>
    /// Folds per-song enrichment/build status into a directory tree rooted at the source library,
    /// where every node's counts are rolled up from all songs anywhere beneath it.
    /// </summary>
    internal static DirectoryMatchNode BuildMatchTree(IEnumerable<MatchTreeRow> rows, string? sourceDirectory)
    {
        var sourceRoot = NormalizePath(sourceDirectory);
        var root = new DirectoryMatchNode(
            string.IsNullOrEmpty(sourceDirectory) ? "/" : sourceDirectory,
            string.Empty);

        foreach (var row in rows)
        {
            var node = root;
            node.Accumulate(row.EnrichmentStatus, row.LibraryBuildStatus, row.FileSizeBytes);

            var cumulative = "";
            foreach (var segment in RelativeDirectorySegments(NormalizePath(row.SourcePath), sourceRoot))
            {
                cumulative = cumulative.Length == 0 ? segment : $"{cumulative}/{segment}";
                node = node.GetOrAddChild(segment, cumulative);
                node.Accumulate(row.EnrichmentStatus, row.LibraryBuildStatus, row.FileSizeBytes);
            }

            // `node` is now the directory this file physically lives in — count it as a
            // direct file there only (not in its ancestors, which roll up cumulatively).
            node.AddDirectFile();
        }

        return root;
    }

    private static string NormalizePath(string? path)
        => (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');

    private static string[] RelativeDirectorySegments(string fullPath, string sourceRoot)
    {
        var lastSlash = fullPath.LastIndexOf('/');
        var directory = lastSlash >= 0 ? fullPath[..lastSlash] : string.Empty;
        if (sourceRoot.Length > 0 && directory.StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
        {
            directory = directory[sourceRoot.Length..];
        }

        return directory.Split('/', StringSplitOptions.RemoveEmptyEntries);
    }

    internal sealed class DirectoryMatchNode
    {
        private readonly Dictionary<string, DirectoryMatchNode> _children = new(StringComparer.Ordinal);

        public DirectoryMatchNode(string name, string path)
        {
            Name = name;
            Path = path;
        }

        public string Name { get; }
        public string Path { get; }
        public int Total { get; private set; }
        public int Matched { get; private set; }
        public int NeedsReview { get; private set; }
        public int Pending { get; private set; }
        public int Failed { get; private set; }
        public int Done { get; private set; }
        public int DirectFiles { get; private set; }
        public long SizeBytes { get; private set; }
        public IReadOnlyCollection<DirectoryMatchNode> Children => _children.Values;

        public DirectoryMatchNode GetOrAddChild(string name, string path)
        {
            if (!_children.TryGetValue(name, out var child))
            {
                child = new DirectoryMatchNode(name, path);
                _children[name] = child;
            }

            return child;
        }

        public void AddDirectFile() => DirectFiles++;

        public void Accumulate(EnrichmentStatus enrichment, LibraryBuildStatus build, long fileSizeBytes = 0)
        {
            Total++;
            SizeBytes += fileSizeBytes;
            switch (enrichment)
            {
                case EnrichmentStatus.Matched:
                    Matched++;
                    break;
                case EnrichmentStatus.NeedsReview:
                    NeedsReview++;
                    break;
                case EnrichmentStatus.Failed:
                    Failed++;
                    break;
                default:
                    Pending++;
                    break;
            }

            if (build == LibraryBuildStatus.Done)
            {
                Done++;
            }
        }

        public object ToDto() => new
        {
            Name,
            Path,
            Total,
            Matched,
            NeedsReview,
            Pending,
            Failed,
            Done,
            DirectFileCount = DirectFiles,
            SizeBytes,
            NotMatched = Total - Matched,
            MatchedPct = Total > 0 ? Math.Round(100.0 * Matched / Total, 1) : 0,
            // Worst-offending folders first so the largest review backlogs surface at the top.
            Children = _children.Values
                .OrderByDescending(c => c.Total - c.Matched)
                .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => c.ToDto())
                .ToList(),
        };
    }

    private static async Task<IResult> GetStats(MusicHoarderDbContext db)
    {
        var active = db.Songs.Where(s => s.DeletedAtUtc == null);
        var totalCount = await active.CountAsync();
        var deletedCount = await db.Songs.CountAsync(s => s.DeletedAtUtc != null);

        var storage = await active
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalBytes = g.Sum(s => s.FileSizeBytes),
                AvgBytes = (long)g.Average(s => s.FileSizeBytes),
            })
            .SingleOrDefaultAsync();

        var duration = await active
            .Where(s => s.DurationSeconds != null)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalSeconds = g.Sum(s => s.DurationSeconds ?? 0),
                TrackCountWithDuration = g.Count(),
            })
            .SingleOrDefaultAsync();

        var byExtensionRaw = await active
            .GroupBy(s => s.Extension)
            .Select(g => new { Extension = g.Key, Count = g.Count() })
            .ToListAsync();
        var byExtension = byExtensionRaw
            .GroupBy(x => x.Extension?.ToLowerInvariant() ?? "")
            .Select(g => new { Extension = g.Key, Count = g.Sum(x => x.Count) })
            .OrderByDescending(x => x.Count)
            .ToList();

        var enrichment = await active
            .GroupBy(_ => 1)
            .Select(g => new
            {
                WithFingerprint = g.Count(s => s.Fingerprint != null && s.Fingerprint != ""),
                WithMusicBrainzId = g.Count(s => s.MusicBrainzId != null && s.MusicBrainzId != ""),
                WithSpotifyId = g.Count(s => s.SpotifyId != null && s.SpotifyId != ""),
                WithIsrc = g.Count(s => s.Isrc != null && s.Isrc != ""),
                WithArtist = g.Count(s => s.Artist != null && s.Artist != ""),
                WithAlbum = g.Count(s => s.Album != null && s.Album != ""),
                WithTitle = g.Count(s => s.Title != null && s.Title != ""),
            })
            .SingleOrDefaultAsync();

        var indexWindow = await active
            .GroupBy(_ => 1)
            .Select(g => new
            {
                OldestIndexed = g.Min(s => s.IndexedAtUtc),
                NewestIndexed = g.Max(s => s.IndexedAtUtc),
                OldestModified = g.Min(s => s.LastModifiedUtc),
                NewestModified = g.Max(s => s.LastModifiedUtc),
            })
            .SingleOrDefaultAsync();

        var stats = new
        {
            Tracks = new
            {
                Total = totalCount,
                Deleted = deletedCount,
            },
            Storage = storage == null
                ? null
                : new
                {
                    TotalBytes = storage.TotalBytes,
                    TotalGiB = Math.Round(storage.TotalBytes / (1024.0 * 1024.0 * 1024.0), 2),
                    AverageBytesPerTrack = storage.AvgBytes,
                },
            Duration = duration == null
                ? null
                : new
                {
                    TotalSeconds = duration.TotalSeconds,
                    TotalHours = Math.Round(duration.TotalSeconds / 3600.0, 1),
                    TracksWithDuration = duration.TrackCountWithDuration,
                    AverageSecondsPerTrack = duration.TrackCountWithDuration > 0
                        ? Math.Round(duration.TotalSeconds / (double)duration.TrackCountWithDuration, 1)
                        : (double?)null,
                },
            ByExtension = byExtension,
            Enrichment = enrichment == null
                ? null
                : new
                {
                    enrichment.WithFingerprint,
                    enrichment.WithMusicBrainzId,
                    enrichment.WithSpotifyId,
                    enrichment.WithIsrc,
                    enrichment.WithArtist,
                    enrichment.WithAlbum,
                    enrichment.WithTitle,
                    FingerprintPct = totalCount > 0 ? Math.Round(100.0 * enrichment.WithFingerprint / totalCount, 1) : 0,
                    MusicBrainzPct = totalCount > 0 ? Math.Round(100.0 * enrichment.WithMusicBrainzId / totalCount, 1) : 0,
                },
            IndexWindow = indexWindow == null
                ? null
                : new
                {
                    OldestIndexedUtc = indexWindow.OldestIndexed,
                    NewestIndexedUtc = indexWindow.NewestIndexed,
                    OldestFileModifiedUtc = indexWindow.OldestModified,
                    NewestFileModifiedUtc = indexWindow.NewestModified,
                },
        };

        return Results.Ok(stats);
    }

    private static async Task<IResult> GetOverview(
        MusicHoarderDbContext db,
        IOptions<MusicEnricherOptions> options,
        ScanProgressTracker scanTracker,
        FingerprintProgressTracker fingerprintTracker,
        EnrichmentProgressTracker enrichmentTracker)
    {
        var opts = options.Value;
        var active = db.Songs.Where(s => s.DeletedAtUtc == null);

        var counts = await PipelineSnapshot.ComputeCountsAsync(active);

        var indexWindow = await active
            .GroupBy(_ => 1)
            .Select(g => new
            {
                NewestIndexed = g.Max(s => s.IndexedAtUtc),
            })
            .SingleOrDefaultAsync();

        var scanState = scanTracker.GetCurrent();
        var scanRunning = scanState is { IsComplete: false };
        var fingerprintState = fingerprintTracker.GetCurrent();
        var fingerprintRunning = fingerprintState is { IsComplete: false };
        var enrichmentState = enrichmentTracker.GetCurrent();
        var enrichmentRunning = enrichmentState is { IsComplete: false };

        var now = DateTime.UtcNow;
        var activities = await PipelineSnapshot.ComputeRecentActivityAsync(active, 50, now);

        var startedAt = scanState?.StartedAt ?? indexWindow?.NewestIndexed ?? now;

        var overview = new
        {
            SourcePath = opts.SourceDirectory,
            DestinationPath = opts.DestinationDirectory,
            Scan = scanState == null ? null : new
            {
                scanState.ScanId,
                scanState.TotalFiles,
                scanState.Processed,
                scanState.NewFiles,
                scanState.ChangedFiles,
                scanState.SkippedFiles,
                scanState.FailedFiles,
                scanState.IsComplete,
                scanState.StartedAt,
                scanState.CompletedAt,
            },
            Job = new
            {
                Status = scanRunning || fingerprintRunning || enrichmentRunning ? "running" : "completed",
                StartedAt = startedAt,
                TracksDiscovered = counts.Discovered,
                TracksProcessed = counts.Processed,
                TracksFingerprinted = counts.Fingerprinted,
                TracksEnriched = counts.Enriched,
                TracksBuildEligible = counts.BuildEligible,
                TracksCopied = counts.Copied,
                TracksReview = counts.Review,
                TracksFailed = counts.Failed,
            },
            Fingerprint = fingerprintState is { IsComplete: false } ? new
            {
                fingerprintState.RunId,
                fingerprintState.TotalTracks,
                fingerprintState.Processed,
                fingerprintState.Fingerprinted,
                fingerprintState.Failed,
                fingerprintState.IsComplete,
                fingerprintState.StartedAt,
                fingerprintState.CompletedAt,
            } : null,
            Enrichment = enrichmentState is { IsComplete: false } ? new
            {
                enrichmentState.RunId,
                enrichmentState.TotalTracks,
                enrichmentState.Processed,
                enrichmentState.Enriched,
                enrichmentState.Failed,
                enrichmentState.NeedsReview,
                enrichmentState.IsComplete,
                enrichmentState.StartedAt,
                enrichmentState.CompletedAt,
            } : null,
            RecentActivity = activities.Select(a => new
            {
                a.Id,
                a.Type,
                a.Track,
                a.Artist,
                a.Time,
            }),
        };

        return Results.Ok(overview);
    }
}

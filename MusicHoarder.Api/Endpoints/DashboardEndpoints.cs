using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Auth;
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
        app.MapGet("/insights", GetInsights).WithName("GetInsights");
        app.MapGet("/overview", GetOverview).WithName("GetOverview");
        app.MapGet("/library/directory-tree", GetDirectoryTree).WithName("GetDirectoryTree");
        app.MapGet("/library/directory-tree/files", GetDirectoryFiles).WithName("GetDirectoryFiles");
        app.MapPost("/library/directory-preferences", SetDirectoryPreference).WithName("SetDirectoryPreference");
        return app;
    }

    private static async Task<IResult> GetDirectoryTree(
        MusicHoarderDbContext db,
        IOptions<MusicEnricherOptions> options,
        ICurrentUserAccessor currentUser)
    {
        var rows = await db.Songs
            .Where(s => s.DeletedAtUtc == null)
            .Select(s => new { s.SourcePath, s.EnrichmentStatus, s.LibraryBuildStatus, s.FileSizeBytes })
            .ToListAsync();

        // Source-relative paths the current user has tagged "expected low" (leaks/unreleased/etc.).
        var expectedLow = (await db.DirectoryPreferences
            .Where(p => p.ExpectedLow)
            .Select(p => p.Path)
            .ToListAsync())
            .ToHashSet(StringComparer.Ordinal);

        var root = BuildMatchTree(
            rows.Select(r => new MatchTreeRow(r.SourcePath, r.EnrichmentStatus, r.LibraryBuildStatus, r.FileSizeBytes)),
            options.Value.SourceDirectory,
            expectedLow,
            rootLabel: currentUser.User?.IsAdmin == true ? null : "/");

        return Results.Ok(root.ToDto());
    }

    private record DirectoryPreferenceRequest(string Path, bool ExpectedLow);

    /// <summary>
    /// Upserts the current user's "expected low" flag for one source-relative folder path (the
    /// <c>Path</c> emitted by the directory tree). Setting it pulls the folder out of the work queue.
    /// </summary>
    private static async Task<IResult> SetDirectoryPreference(
        DirectoryPreferenceRequest request,
        MusicHoarderDbContext db,
        ICurrentUserAccessor currentUser,
        CancellationToken ct)
    {
        if (currentUser.UserId == Guid.Empty)
            return Results.Unauthorized();

        // Normalize to the same forward-slash, no-trailing-slash form the tree node Path uses.
        var path = (request.Path ?? string.Empty).Replace('\\', '/').TrimEnd('/');

        var existing = await db.DirectoryPreferences.FirstOrDefaultAsync(p => p.Path == path, ct);
        if (existing is null)
        {
            existing = new DirectoryPreference
            {
                OwnerUserId = currentUser.UserId,
                Path = path,
            };
            db.DirectoryPreferences.Add(existing);
        }

        existing.ExpectedLow = request.ExpectedLow;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);

        return Results.Ok(new { path, expectedLow = existing.ExpectedLow });
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
    internal static DirectoryMatchNode BuildMatchTree(
        IEnumerable<MatchTreeRow> rows,
        string? sourceDirectory,
        IReadOnlySet<string>? expectedLowPaths = null,
        string? rootLabel = null)
    {
        var sourceRoot = NormalizePath(sourceDirectory);
        var root = new DirectoryMatchNode(
            rootLabel ?? (string.IsNullOrEmpty(sourceDirectory) ? "/" : sourceDirectory),
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

        if (expectedLowPaths is { Count: > 0 })
            ApplyExpectedLow(root, expectedLowPaths);

        return root;
    }

    private static void ApplyExpectedLow(DirectoryMatchNode node, IReadOnlySet<string> paths)
    {
        node.ExpectedLow = paths.Contains(node.Path);
        foreach (var child in node.Children)
            ApplyExpectedLow(child, paths);
    }

    private static string NormalizePath(string? path)
        => (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');

    private static string[] RelativeDirectorySegments(string fullPath, string sourceRoot)
    {
        var lastSlash = fullPath.LastIndexOf('/');
        var directory = lastSlash >= 0 ? fullPath[..lastSlash] : string.Empty;
        // Strip only on a path boundary: "/root/music-demo" starts with "/root/music" as a string,
        // and stripping that non-boundary prefix would relativize a sibling directory against the
        // configured source root, showing the tree as "-demo/…" and handing a non-admin the
        // owner's source directory by arithmetic.
        if (sourceRoot.Length > 0
            && (directory.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)
                || directory.StartsWith(sourceRoot + "/", StringComparison.OrdinalIgnoreCase)))
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
        /// <summary>Set from the current user's <see cref="DirectoryPreference"/>s after the tree is built.</summary>
        public bool ExpectedLow { get; set; }
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
            ExpectedLow,
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

    /// <summary>
    /// One owner-scoped "story of the hoard" payload powering the Stats overview page: how much of the
    /// source made it into the library, what enrichment added (covers, lyrics), how the Spotify-liked →
    /// wishlist → download → library funnel converted, plus library totals, top artists/albums and
    /// enrichment-quality breakdowns. All queries ride the EF global query filter (current user only),
    /// so no manual demo/tenant exclusion is needed.
    /// <see cref="LibraryInsightsReport"/> shapes the payload from the query results below.
    /// </summary>
    internal static async Task<IResult> GetInsights(MusicHoarderDbContext db)
    {
        var active = db.Songs.Where(s => s.DeletedAtUtc == null);
        var built = active.Where(s => s.LibraryBuildStatus == LibraryBuildStatus.Done && s.DestinationPath != null);

        // Most scalar counts batched into two GroupBy(_=>1) round-trips (same pattern as GetStats),
        // which the EF InMemory provider translates cleanly (no GroupBy→First).
        var a = await active
            .GroupBy(_ => 1)
            .Select(g => new InsightsSongCounts
            {
                Indexed = g.Count(),
                Fingerprinted = g.Count(s => s.HasFingerprint),
                Matched = g.Count(s => s.EnrichmentStatus == EnrichmentStatus.Matched),
                NeedsReview = g.Count(s => s.EnrichmentStatus == EnrichmentStatus.NeedsReview),
                FailedEnrich = g.Count(s => s.EnrichmentStatus == EnrichmentStatus.Failed),
                PendingEnrich = g.Count(s => s.EnrichmentStatus == EnrichmentStatus.Pending),
                LyricsFetched = g.Count(s => s.LyricsStatus == LyricsStatus.Fetched),
                LyricsInstrumental = g.Count(s => s.LyricsStatus == LyricsStatus.Instrumental),
                LyricsNotFound = g.Count(s => s.LyricsStatus == LyricsStatus.NotFound),
                LyricsNotFetched = g.Count(s => s.LyricsStatus == LyricsStatus.NotFetched),
                LyricsFailed = g.Count(s => s.LyricsStatus == LyricsStatus.Failed),
                WithMbid = g.Count(s => s.MusicBrainzId != null && s.MusicBrainzId != ""),
                WithSpotify = g.Count(s => s.SpotifyId != null && s.SpotifyId != ""),
                WithIsrc = g.Count(s => s.Isrc != null && s.Isrc != ""),
                ManualApprovals = g.Count(s => s.IsManuallyApproved),
                Duplicates = g.Count(s => s.IsDuplicate),
                Conf90 = g.Count(s => s.EnrichmentStatus == EnrichmentStatus.Matched && s.MatchConfidence >= 0.9),
                Conf75 = g.Count(s => s.EnrichmentStatus == EnrichmentStatus.Matched && s.MatchConfidence >= 0.75 && s.MatchConfidence < 0.9),
                ConfLow = g.Count(s => s.EnrichmentStatus == EnrichmentStatus.Matched && s.MatchConfidence != null && s.MatchConfidence < 0.75),
                OldestIndexed = g.Min(s => s.IndexedAtUtc),
                NewestIndexed = g.Max(s => s.IndexedAtUtc),
            })
            .SingleOrDefaultAsync();

        var b = await built
            .GroupBy(_ => 1)
            .Select(g => new InsightsBuiltCounts
            {
                InLibrary = g.Count(),
                WithCover = g.Count(s => s.HasCoverArt),
                WithLyrics = g.Count(s => s.LyricsStatus == LyricsStatus.Fetched),
                DurationSeconds = g.Sum(s => s.DurationSeconds ?? 0),
                Bytes = g.Sum(s => s.FileSizeBytes),
            })
            .SingleOrDefaultAsync();

        // Album covers MusicHoarder physically wrote into the destination library (one per album folder).
        // The original source file's cover state isn't recorded, so this audit-log count is the honest
        // "covers we added" figure (vs library coverage %, which is the WithCover/InLibrary ratio below).
        var albumCoversAdded = await db.LibraryWriteEvents
            .Where(e => e.Kind == LibraryWriteEventKind.AlbumCoverWritten && e.AlbumFolder != null)
            .Select(e => e.AlbumFolder!)
            .Distinct()
            .CountAsync();

        // "In the library" = the linked downloaded song is built and live in the destination.
        var liked = await db.WishlistItems
            .Where(w => w.WishlistSource != null && w.WishlistSource.SourceType == WishlistSourceType.LikedSongs)
            .GroupBy(_ => 1)
            .Select(g => new InsightsLikedCounts
            {
                Total = g.Count(),
                InLibrary = g.Count(w => w.DownloadedSong != null && w.DownloadedSong.DeletedAtUtc == null && w.DownloadedSong.LibraryBuildStatus == LibraryBuildStatus.Done && w.DownloadedSong.DestinationPath != null),
                Downloaded = g.Count(w => w.Status == WishlistItemStatus.Downloaded || w.DownloadedSongId != null),
                SkippedOwned = g.Count(w => w.Status == WishlistItemStatus.SkippedOwned),
                Downloading = g.Count(w => w.Status == WishlistItemStatus.Downloading),
                Pending = g.Count(w => w.Status == WishlistItemStatus.Pending),
                NotFound = g.Count(w => w.Status == WishlistItemStatus.NotFound),
                Failed = g.Count(w => w.Status == WishlistItemStatus.Failed),
            })
            .SingleOrDefaultAsync();

        var wishAll = await db.WishlistItems
            .GroupBy(_ => 1)
            .Select(g => new InsightsWishlistCounts
            {
                Total = g.Count(),
                InLibrary = g.Count(w => w.DownloadedSong != null && w.DownloadedSong.DeletedAtUtc == null && w.DownloadedSong.LibraryBuildStatus == LibraryBuildStatus.Done && w.DownloadedSong.DestinationPath != null),
                Downloaded = g.Count(w => w.Status == WishlistItemStatus.Downloaded || w.DownloadedSongId != null),
            })
            .SingleOrDefaultAsync();

        var wishlistSources = await db.WishlistSources.CountAsync();

        // Format breakdown — DB GroupBy+Count; the report normalises extensions in memory.
        var byExtRaw = await active
            .GroupBy(s => s.Extension)
            .Select(g => new InsightsExtensionCount(g.Key, g.Count()))
            .ToListAsync();

        // Per-provider match counts (SongProviderAttempt is owner-scoped via its Song.OwnerUserId filter).
        var providerRaw = await db.SongProviderAttempts
            .GroupBy(p => p.Provider)
            .Select(g => new InsightsProviderCount(
                g.Key,
                g.Count(),
                g.Count(x => x.Status == ProviderAttemptStatus.Matched)))
            .ToListAsync();

        // Top artists / albums + distinct counts: pull three string columns over the built library;
        // the report groups them in memory (case-insensitive, no DB collation).
        var builtRows = await built
            .Select(s => new InsightsBuiltTrack(s.AlbumArtist, s.Artist, s.Album))
            .ToListAsync();

        return Results.Ok(LibraryInsightsReport.Build(
            new LibraryInsightsInputs
            {
                Songs = a,
                Built = b,
                AlbumCoversAdded = albumCoversAdded,
                Liked = liked,
                Wishlist = wishAll,
                WishlistSources = wishlistSources,
                SongsByExtension = byExtRaw,
                AttemptsByProvider = providerRaw,
                BuiltTracks = builtRows,
            },
            DateTime.UtcNow));
    }

    private static async Task<IResult> GetStats(MusicHoarderDbContext db)
    {
        // Single table scan for all scalar aggregates (uses stored HasFingerprint so Fingerprint
        // TOAST values are never detoasted).
        var summary = await db.Songs.AsNoTracking()
            .GroupBy(_ => 1)
            .Select(g => new
            {
                TotalCount = g.Count(s => s.DeletedAtUtc == null),
                DeletedCount = g.Count(s => s.DeletedAtUtc != null),
                TotalBytes = g.Sum(s => s.DeletedAtUtc == null ? s.FileSizeBytes : 0L),
                TotalDurationSeconds = g.Sum(s => s.DeletedAtUtc == null && s.DurationSeconds != null ? s.DurationSeconds.Value : 0L),
                TrackCountWithDuration = g.Count(s => s.DeletedAtUtc == null && s.DurationSeconds != null),
                WithFingerprint = g.Count(s => s.DeletedAtUtc == null && s.HasFingerprint),
                WithMusicBrainzId = g.Count(s => s.DeletedAtUtc == null && s.MusicBrainzId != null && s.MusicBrainzId != ""),
                WithSpotifyId = g.Count(s => s.DeletedAtUtc == null && s.SpotifyId != null && s.SpotifyId != ""),
                WithIsrc = g.Count(s => s.DeletedAtUtc == null && s.Isrc != null && s.Isrc != ""),
                WithArtist = g.Count(s => s.DeletedAtUtc == null && s.Artist != null && s.Artist != ""),
                WithAlbum = g.Count(s => s.DeletedAtUtc == null && s.Album != null && s.Album != ""),
                WithTitle = g.Count(s => s.DeletedAtUtc == null && s.Title != null && s.Title != ""),
                OldestIndexed = g.Min(s => s.DeletedAtUtc == null ? (DateTime?)s.IndexedAtUtc : null),
                NewestIndexed = g.Max(s => s.DeletedAtUtc == null ? (DateTime?)s.IndexedAtUtc : null),
                OldestModified = g.Min(s => s.DeletedAtUtc == null ? (DateTime?)s.LastModifiedUtc : null),
                NewestModified = g.Max(s => s.DeletedAtUtc == null ? (DateTime?)s.LastModifiedUtc : null),
            })
            .SingleOrDefaultAsync();

        var totalCount = summary?.TotalCount ?? 0;
        var deletedCount = summary?.DeletedCount ?? 0;

        var byExtensionRaw = await db.Songs.AsNoTracking()
            .Where(s => s.DeletedAtUtc == null)
            .GroupBy(s => s.Extension)
            .Select(g => new { Extension = g.Key, Count = g.Count() })
            .ToListAsync();
        var byExtension = byExtensionRaw
            .GroupBy(x => x.Extension?.ToLowerInvariant() ?? "")
            .Select(g => new { Extension = g.Key, Count = g.Sum(x => x.Count) })
            .OrderByDescending(x => x.Count)
            .ToList();

        var stats = new
        {
            Tracks = new
            {
                Total = totalCount,
                Deleted = deletedCount,
            },
            Storage = totalCount == 0 || summary is null
                ? null
                : new
                {
                    TotalBytes = summary.TotalBytes,
                    TotalGiB = Math.Round(summary.TotalBytes / (1024.0 * 1024.0 * 1024.0), 2),
                    AverageBytesPerTrack = summary.TotalBytes / totalCount,
                },
            Duration = summary is null || summary.TrackCountWithDuration == 0
                ? null
                : new
                {
                    TotalSeconds = summary.TotalDurationSeconds,
                    TotalHours = Math.Round(summary.TotalDurationSeconds / 3600.0, 1),
                    TracksWithDuration = summary.TrackCountWithDuration,
                    AverageSecondsPerTrack = (double?)Math.Round(summary.TotalDurationSeconds / (double)summary.TrackCountWithDuration, 1),
                },
            ByExtension = byExtension,
            Enrichment = totalCount == 0 || summary is null
                ? null
                : new
                {
                    summary.WithFingerprint,
                    summary.WithMusicBrainzId,
                    summary.WithSpotifyId,
                    summary.WithIsrc,
                    summary.WithArtist,
                    summary.WithAlbum,
                    summary.WithTitle,
                    FingerprintPct = Math.Round(100.0 * summary.WithFingerprint / totalCount, 1),
                    MusicBrainzPct = Math.Round(100.0 * summary.WithMusicBrainzId / totalCount, 1),
                },
            IndexWindow = totalCount == 0 || summary is null
                ? null
                : new
                {
                    OldestIndexedUtc = summary.OldestIndexed!.Value,
                    NewestIndexedUtc = summary.NewestIndexed!.Value,
                    OldestFileModifiedUtc = summary.OldestModified!.Value,
                    NewestFileModifiedUtc = summary.NewestModified!.Value,
                },
        };

        return Results.Ok(stats);
    }

    private static async Task<IResult> GetOverview(
        MusicHoarderDbContext db,
        IOptions<MusicEnricherOptions> options,
        ScanProgressTracker scanTracker,
        FingerprintProgressTracker fingerprintTracker,
        EnrichmentProgressTracker enrichmentTracker,
        ICurrentUserAccessor currentUser)
    {
        var opts = options.Value;
        // Host paths and process-wide pipeline state are for admins only, as in the settings endpoint.
        var isAdmin = currentUser.User?.IsAdmin == true;
        var active = db.Songs.Where(s => s.DeletedAtUtc == null);

        var (counts, newestIndexed) = await PipelineSnapshot.ComputeOverviewCountsAsync(active);

        var scanState = isAdmin ? scanTracker.GetCurrent() : null;
        var scanRunning = scanState is { IsComplete: false };
        var fingerprintState = isAdmin ? fingerprintTracker.GetCurrent() : null;
        var fingerprintRunning = fingerprintState is { IsComplete: false };
        var enrichmentState = isAdmin ? enrichmentTracker.GetCurrent() : null;
        var enrichmentRunning = enrichmentState is { IsComplete: false };

        var now = DateTime.UtcNow;
        var activities = await PipelineSnapshot.ComputeRecentActivityAsync(active, 50);

        var startedAt = scanState?.StartedAt ?? newestIndexed ?? now;

        var overview = new
        {
            SourcePath = isAdmin ? opts.SourceDirectory : string.Empty,
            DestinationPath = isAdmin ? opts.DestinationDirectory : string.Empty,
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
            RecentActivity = activities.Select(a => Jobs.RecentActivity.ToWire(a, now)),
        };

        return Results.Ok(overview);
    }
}

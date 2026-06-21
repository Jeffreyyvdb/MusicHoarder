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
        IOptions<MusicEnricherOptions> options)
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
            expectedLow);

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
        IReadOnlySet<string>? expectedLowPaths = null)
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

    private static double Pct(double part, double whole) => whole > 0 ? Math.Round(100.0 * part / whole, 1) : 0;

    /// <summary>
    /// One owner-scoped "story of the hoard" payload powering the Stats overview page: how much of the
    /// source made it into the library, what enrichment added (covers, lyrics), how the Spotify-liked →
    /// wishlist → download → library funnel converted, plus library totals, top artists/albums and
    /// enrichment-quality breakdowns. All queries ride the EF global query filter (current user only),
    /// so no manual demo/tenant exclusion is needed.
    /// </summary>
    internal static async Task<IResult> GetInsights(MusicHoarderDbContext db)
    {
        var active = db.Songs.Where(s => s.DeletedAtUtc == null);
        var built = active.Where(s => s.LibraryBuildStatus == LibraryBuildStatus.Done && s.DestinationPath != null);

        // Most scalar counts batched into two GroupBy(_=>1) round-trips (same pattern as GetStats),
        // which the EF InMemory provider translates cleanly (no GroupBy→First).
        var a = await active
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Indexed = g.Count(),
                Fingerprinted = g.Count(s => s.Fingerprint != null && s.Fingerprint != ""),
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
            .Select(g => new
            {
                InLibrary = g.Count(),
                WithCover = g.Count(s => s.HasCoverArt),
                WithLyrics = g.Count(s => s.LyricsStatus == LyricsStatus.Fetched),
                DurationSeconds = g.Sum(s => s.DurationSeconds ?? 0),
                Bytes = g.Sum(s => s.FileSizeBytes),
            })
            .SingleOrDefaultAsync();

        var indexed = a?.Indexed ?? 0;
        var inLibrary = b?.InLibrary ?? 0;

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
            .Select(g => new
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
            .Select(g => new
            {
                Total = g.Count(),
                InLibrary = g.Count(w => w.DownloadedSong != null && w.DownloadedSong.DeletedAtUtc == null && w.DownloadedSong.LibraryBuildStatus == LibraryBuildStatus.Done && w.DownloadedSong.DestinationPath != null),
                Downloaded = g.Count(w => w.Status == WishlistItemStatus.Downloaded || w.DownloadedSongId != null),
            })
            .SingleOrDefaultAsync();

        var wishlistSources = await db.WishlistSources.CountAsync();

        // Format breakdown — DB GroupBy+Count, then normalise extensions in memory.
        var byExtRaw = await active
            .GroupBy(s => s.Extension)
            .Select(g => new { Ext = g.Key, Count = g.Count() })
            .ToListAsync();
        var byFormat = byExtRaw
            .GroupBy(x => (x.Ext ?? "").TrimStart('.').ToLowerInvariant())
            .Where(g => g.Key.Length > 0)
            .Select(g => new { format = g.Key, count = g.Sum(x => x.Count) })
            .OrderByDescending(x => x.count)
            .ToList();

        // Per-provider match counts (SongProviderAttempt is owner-scoped via its Song.OwnerUserId filter).
        var providerRaw = await db.SongProviderAttempts
            .GroupBy(p => p.Provider)
            .Select(g => new
            {
                Provider = g.Key,
                Total = g.Count(),
                Matched = g.Count(x => x.Status == ProviderAttemptStatus.Matched),
            })
            .ToListAsync();
        var byProvider = providerRaw
            .Select(p => new { provider = p.Provider.ToString(), total = p.Total, matched = p.Matched })
            .OrderByDescending(p => p.matched)
            .ThenByDescending(p => p.total)
            .ToList();

        // Top artists / albums + distinct counts: pull three string columns over the built library and
        // group in memory so case-insensitive grouping is correct and provider-agnostic (no DB collation).
        var builtRows = await built
            .Select(s => new { s.AlbumArtist, s.Artist, s.Album })
            .ToListAsync();
        static string ArtistOf(string? albumArtist, string? artist) => (albumArtist ?? artist ?? "").Trim();

        var topArtists = builtRows
            .Select(r => ArtistOf(r.AlbumArtist, r.Artist))
            .Where(n => n.Length > 0)
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(g => new { name = g.Key, tracks = g.Count() })
            .OrderByDescending(x => x.tracks)
            .ThenBy(x => x.name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var topAlbums = builtRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Album))
            .GroupBy(
                r => (Album: r.Album!.Trim(), Artist: ArtistOf(r.AlbumArtist, r.Artist)),
                ValueTupleCaseInsensitiveComparer.Instance)
            .Select(g => new { album = g.Key.Album, artist = g.Key.Artist, tracks = g.Count() })
            .OrderByDescending(x => x.tracks)
            .ThenBy(x => x.album, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var distinctArtists = builtRows
            .Select(r => ArtistOf(r.AlbumArtist, r.Artist).ToLowerInvariant())
            .Where(n => n.Length > 0)
            .Distinct()
            .Count();
        var distinctAlbums = builtRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Album))
            .Select(r => $"{ArtistOf(r.AlbumArtist, r.Artist)}{r.Album!.Trim()}".ToLowerInvariant())
            .Distinct()
            .Count();

        var lyricsAdded = a?.LyricsFetched ?? 0;
        var likedTotal = liked?.Total ?? 0;

        var response = new
        {
            generatedAtUtc = DateTime.UtcNow,

            // ── Stat 1: how much of the source path is now in the library ──
            source = new
            {
                indexed,
                inLibrary,
                inLibraryPct = Pct(inLibrary, indexed),
                notYetBuilt = Math.Max(0, indexed - inLibrary),
            },

            // Pipeline drop-off, indexed → in library.
            funnel = new[]
            {
                new { stage = "Indexed", count = indexed },
                new { stage = "Fingerprinted", count = a?.Fingerprinted ?? 0 },
                new { stage = "Matched", count = a?.Matched ?? 0 },
                new { stage = "In library", count = inLibrary },
            }
            .Select(x => new { x.stage, x.count, pct = Pct(x.count, indexed) })
            .ToList(),

            // ── Stat 2: covers that weren't there before, but are now ──
            covers = new
            {
                albumCoversAdded,
                builtWithCover = b?.WithCover ?? 0,
                builtTracks = inLibrary,
                coveragePct = Pct(b?.WithCover ?? 0, inLibrary),
            },

            // ── Stat 3: lyrics that weren't there before, but are now ──
            lyrics = new
            {
                added = lyricsAdded,
                builtWithLyrics = b?.WithLyrics ?? 0,
                builtTracks = inLibrary,
                coveragePct = Pct(b?.WithLyrics ?? 0, inLibrary),
                instrumental = a?.LyricsInstrumental ?? 0,
                notFound = a?.LyricsNotFound ?? 0,
                breakdown = new[]
                {
                    new { status = "Fetched", count = lyricsAdded },
                    new { status = "Instrumental", count = a?.LyricsInstrumental ?? 0 },
                    new { status = "Not found", count = a?.LyricsNotFound ?? 0 },
                    new { status = "Not fetched", count = a?.LyricsNotFetched ?? 0 },
                    new { status = "Failed", count = a?.LyricsFailed ?? 0 },
                },
            },

            // ── Stats 4 & 5: Spotify-liked → wishlist → download → library ──
            wishlist = new
            {
                liked = new
                {
                    total = likedTotal,
                    downloaded = liked?.Downloaded ?? 0,
                    inLibrary = liked?.InLibrary ?? 0,
                    skippedOwned = liked?.SkippedOwned ?? 0,
                },
                all = new
                {
                    total = wishAll?.Total ?? 0,
                    downloaded = wishAll?.Downloaded ?? 0,
                    inLibrary = wishAll?.InLibrary ?? 0,
                },
                sources = wishlistSources,
                funnel = new[]
                {
                    new { stage = "Liked / wishlisted", count = likedTotal },
                    new { stage = "Downloaded", count = liked?.Downloaded ?? 0 },
                    new { stage = "In library", count = liked?.InLibrary ?? 0 },
                }
                .Select(x => new { x.stage, x.count, pct = Pct(x.count, likedTotal) })
                .ToList(),
                statusBreakdown = new[]
                {
                    new { status = "In library", count = liked?.InLibrary ?? 0 },
                    new { status = "Downloaded", count = Math.Max(0, (liked?.Downloaded ?? 0) - (liked?.InLibrary ?? 0)) },
                    new { status = "Already owned", count = liked?.SkippedOwned ?? 0 },
                    new { status = "Downloading", count = liked?.Downloading ?? 0 },
                    new { status = "Pending", count = liked?.Pending ?? 0 },
                    new { status = "Not found", count = liked?.NotFound ?? 0 },
                    new { status = "Failed", count = liked?.Failed ?? 0 },
                },
            },

            // ── Library totals (the "cool stuff") ──
            totals = new
            {
                builtTracks = inLibrary,
                totalHours = Math.Round((b?.DurationSeconds ?? 0) / 3600.0, 1),
                totalGiB = Math.Round((b?.Bytes ?? 0) / (1024.0 * 1024.0 * 1024.0), 2),
                distinctArtists,
                distinctAlbums,
                duplicates = a?.Duplicates ?? 0,
                oldestIndexedUtc = a?.OldestIndexed,
                newestIndexedUtc = a?.NewestIndexed,
                byFormat,
            },

            top = new { artists = topArtists, albums = topAlbums },

            // ── Enrichment quality ──
            quality = new
            {
                enrichment = new[]
                {
                    new { status = "Matched", count = a?.Matched ?? 0 },
                    new { status = "Needs review", count = a?.NeedsReview ?? 0 },
                    new { status = "Failed", count = a?.FailedEnrich ?? 0 },
                    new { status = "Pending", count = a?.PendingEnrich ?? 0 },
                },
                confidence = new[]
                {
                    new { bucket = "90%+", count = a?.Conf90 ?? 0 },
                    new { bucket = "75-90%", count = a?.Conf75 ?? 0 },
                    new { bucket = "<75%", count = a?.ConfLow ?? 0 },
                },
                byProvider,
                manualApprovals = a?.ManualApprovals ?? 0,
                coverage = new
                {
                    fingerprint = new { count = a?.Fingerprinted ?? 0, pct = Pct(a?.Fingerprinted ?? 0, indexed) },
                    musicBrainz = new { count = a?.WithMbid ?? 0, pct = Pct(a?.WithMbid ?? 0, indexed) },
                    spotify = new { count = a?.WithSpotify ?? 0, pct = Pct(a?.WithSpotify ?? 0, indexed) },
                    isrc = new { count = a?.WithIsrc ?? 0, pct = Pct(a?.WithIsrc ?? 0, indexed) },
                },
            },
        };

        return Results.Ok(response);
    }

    /// <summary>Case-insensitive equality for the (Album, Artist) grouping key used by top-albums.</summary>
    private sealed class ValueTupleCaseInsensitiveComparer : IEqualityComparer<(string Album, string Artist)>
    {
        public static readonly ValueTupleCaseInsensitiveComparer Instance = new();

        public bool Equals((string Album, string Artist) x, (string Album, string Artist) y)
            => string.Equals(x.Album, y.Album, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Artist, y.Artist, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Album, string Artist) obj)
            => HashCode.Combine(
                obj.Album.ToLowerInvariant(),
                obj.Artist.ToLowerInvariant());
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

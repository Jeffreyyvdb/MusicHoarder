using System.IO.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Pipeline;

namespace MusicHoarder.Api.Endpoints;

/// <summary>
/// Read-only endpoints that expose on-disk reality (directory trees, live file tags) next to what
/// the pipeline persisted, so an agent or human can diagnose enrichment results. Per-song "what
/// happened" already lives at <c>/songs/{id}/enrichment-detail</c>; this group adds the on-disk
/// views and a global pipeline aggregate.
/// </summary>
public static class DebugEndpoints
{
    private const int DefaultMaxDepth = 6;
    private const int DefaultMaxEntries = 5000;
    private const int RecentErrorLimit = 25;

    private static readonly HashSet<string> AudioExtensions =
    [
        ".mp3", ".flac", ".wav", ".m4a", ".ogg", ".wma", ".aac", ".opus", ".aiff"
    ];

    public static IEndpointRouteBuilder MapDebugEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/debug").WithTags("Debug");

        group.MapGet("/source-tree", GetSourceTree)
            .WithName("DebugSourceTree")
            .WithSummary("Dev: nested directory tree of the configured source directory (depth + entry capped).");

        group.MapGet("/destination-tree", GetDestinationTree)
            .WithName("DebugDestinationTree")
            .WithSummary("Dev: nested directory tree of the configured destination directory (depth + entry capped).");

        group.MapGet("/songs/{id:int}/tags", GetSongTags)
            .WithName("DebugSongTags")
            .WithSummary("Dev: live on-disk tag dump (TagLib) for a song next to its persisted DB metadata.");

        group.MapGet("/pipeline-summary", GetPipelineSummary)
            .WithName("DebugPipelineSummary")
            .WithSummary("Dev: aggregate pipeline state — counts per stage/status, provider outcomes, recent errors.");

        return app;
    }

    private static IResult GetSourceTree(
        IFileSystem fileSystem,
        IOptions<MusicEnricherOptions> options,
        int? maxDepth = null,
        int? maxEntries = null)
        => Results.Ok(BuildTree(fileSystem, options.Value.SourceDirectory, ClampDepth(maxDepth), ClampEntries(maxEntries)));

    private static IResult GetDestinationTree(
        IFileSystem fileSystem,
        IOptions<MusicEnricherOptions> options,
        int? maxDepth = null,
        int? maxEntries = null)
        => Results.Ok(BuildTree(fileSystem, options.Value.DestinationDirectory, ClampDepth(maxDepth), ClampEntries(maxEntries)));

    private static async Task<IResult> GetSongTags(int id, MusicHoarderDbContext db)
    {
        var song = await db.Songs
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id && s.DeletedAtUtc == null);

        if (song is null)
            return Results.NotFound(new { message = $"Song with id {id} not found." });

        var resolvedPath =
            (!string.IsNullOrEmpty(song.SourcePath) && File.Exists(song.SourcePath)) ? song.SourcePath :
            (!string.IsNullOrEmpty(song.DestinationPath) && File.Exists(song.DestinationPath)) ? song.DestinationPath :
            null;

        var db_ = new
        {
            song.Title,
            song.Artist,
            song.AlbumArtist,
            song.Album,
            song.Year,
            song.TrackNumber,
            song.DurationSeconds,
            song.DurationMs,
            song.Bitrate,
            song.Isrc,
            song.MusicBrainzId,
            song.MusicBrainzReleaseId,
            song.SpotifyId,
            song.AcoustIdTrackId,
            HasSyncedLyrics = !string.IsNullOrEmpty(song.SyncedLyrics),
            HasPlainLyrics = !string.IsNullOrEmpty(song.PlainLyrics),
        };

        return Results.Ok(new
        {
            id = song.Id,
            fileName = song.FileName,
            sourcePath = song.SourcePath,
            destinationPath = song.DestinationPath,
            resolvedPath,
            fileFoundOnDisk = resolvedPath is not null,
            db = db_,
            disk = resolvedPath is null ? null : ReadDiskTags(resolvedPath),
        });
    }

    private static async Task<IResult> GetPipelineSummary(
        MusicHoarderDbContext db,
        IDirectoryAvailability availability)
    {
        var summary = await BuildSummaryAsync(db);
        return Results.Ok(new
        {
            summary.TotalSongs,
            summary.DuplicateCount,
            summary.EnrichmentStatus,
            summary.LibraryBuildStatus,
            summary.LyricsStatus,
            summary.ProviderAttempts,
            summary.RecentErrors,
            directoryAvailability = availability.Current,
        });
    }

    private static int ClampDepth(int? value) => Math.Clamp(value ?? DefaultMaxDepth, 1, 50);

    private static int ClampEntries(int? value) => Math.Clamp(value ?? DefaultMaxEntries, 1, 100_000);

    // ── Directory tree ──────────────────────────────────────────────────────

    internal static DirectoryTreeResult BuildTree(IFileSystem fs, string root, int maxDepth, int maxEntries)
    {
        if (string.IsNullOrWhiteSpace(root) || !fs.Directory.Exists(root))
            return new DirectoryTreeResult(root ?? string.Empty, false, maxDepth, maxEntries, 0, false, null);

        var ctx = new WalkContext(fs, root, maxDepth, maxEntries);
        var tree = ctx.BuildNode(root, 0);
        return new DirectoryTreeResult(root, true, maxDepth, maxEntries, ctx.Count, ctx.Truncated, tree);
    }

    private sealed class WalkContext(IFileSystem fs, string root, int maxDepth, int maxEntries)
    {
        public int Count { get; private set; }
        public bool Truncated { get; private set; }

        public TreeNode BuildNode(string dirPath, int depth)
        {
            var name = fs.Path.GetFileName(dirPath.TrimEnd('/', '\\'));
            if (string.IsNullOrEmpty(name))
                name = dirPath; // root of a drive / mount
            var relativePath = depth == 0 ? string.Empty : fs.Path.GetRelativePath(root, dirPath);

            if (depth >= maxDepth)
            {
                // Don't descend further; flag truncation if this directory actually has content.
                if (fs.Directory.EnumerateFileSystemEntries(dirPath).Any())
                    Truncated = true;
                return new TreeNode(name, relativePath, IsDirectory: true, SizeBytes: null, IsAudio: null, Children: null);
            }

            var children = new List<TreeNode>();

            foreach (var sub in fs.Directory.EnumerateDirectories(dirPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (Count >= maxEntries) { Truncated = true; break; }
                Count++;
                children.Add(BuildNode(sub, depth + 1));
            }

            foreach (var file in fs.Directory.EnumerateFiles(dirPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                if (Count >= maxEntries) { Truncated = true; break; }
                Count++;

                long? size = null;
                try { size = fs.FileInfo.New(file).Length; } catch { /* ignore unreadable */ }
                var ext = fs.Path.GetExtension(file).ToLowerInvariant();

                children.Add(new TreeNode(
                    fs.Path.GetFileName(file),
                    fs.Path.GetRelativePath(root, file),
                    IsDirectory: false,
                    SizeBytes: size,
                    IsAudio: AudioExtensions.Contains(ext),
                    Children: null));
            }

            return new TreeNode(name, relativePath, IsDirectory: true, SizeBytes: null, IsAudio: null, children);
        }
    }

    // ── Live tag read ───────────────────────────────────────────────────────

    private static object ReadDiskTags(string path)
    {
        try
        {
            using var tagFile = TagLib.File.Create(path);
            var tag = tagFile.Tag;
            var props = tagFile.Properties;

            return new
            {
                title = tag.Title,
                performers = tag.Performers,
                albumArtists = tag.AlbumArtists,
                album = tag.Album,
                genres = tag.Genres,
                composers = tag.Composers,
                year = tag.Year == 0 ? (uint?)null : tag.Year,
                track = tag.Track == 0 ? (uint?)null : tag.Track,
                trackCount = tag.TrackCount == 0 ? (uint?)null : tag.TrackCount,
                disc = tag.Disc == 0 ? (uint?)null : tag.Disc,
                discCount = tag.DiscCount == 0 ? (uint?)null : tag.DiscCount,
                comment = tag.Comment,
                isrc = tag.ISRC,
                hasLyrics = !string.IsNullOrEmpty(tag.Lyrics),
                pictureCount = tag.Pictures?.Length ?? 0,
                properties = props is null ? null : new
                {
                    description = props.Description,
                    durationSeconds = (int)props.Duration.TotalSeconds,
                    audioBitrate = props.AudioBitrate,
                    audioSampleRate = props.AudioSampleRate,
                    audioChannels = props.AudioChannels,
                },
            };
        }
        catch (Exception ex)
        {
            return new { error = $"Could not read tags: {ex.Message}" };
        }
    }

    // ── Pipeline summary ────────────────────────────────────────────────────

    internal static async Task<PipelineSummary> BuildSummaryAsync(MusicHoarderDbContext db)
    {
        var active = db.Songs.AsNoTracking().Where(s => s.DeletedAtUtc == null);

        var total = await active.CountAsync();
        var duplicates = await active.CountAsync(s => s.IsDuplicate);

        var enrichment = await active
            .GroupBy(s => s.EnrichmentStatus)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();

        var build = await active
            .GroupBy(s => s.LibraryBuildStatus)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();

        var lyrics = await active
            .GroupBy(s => s.LyricsStatus)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync();

        // SongProviderAttempt has no own query filter; the Song-based predicate forces the join so
        // the owner filter on Song applies and deleted songs are excluded.
        var providerOutcomes = await db.SongProviderAttempts
            .AsNoTracking()
            .Where(a => a.Song.DeletedAtUtc == null)
            .GroupBy(a => new { a.Provider, a.Status })
            .Select(g => new { g.Key.Provider, g.Key.Status, Count = g.Count() })
            .ToListAsync();

        var enrichErrors = await active
            .Where(s => s.EnrichmentError != null)
            .OrderByDescending(s => s.EnrichmentLastAttemptedAtUtc)
            .Take(RecentErrorLimit)
            .Select(s => new PipelineError(s.Id, s.FileName, "Enrichment", s.EnrichmentError!, s.EnrichmentLastAttemptedAtUtc))
            .ToListAsync();

        var buildErrors = await active
            .Where(s => s.LibraryBuildError != null)
            .OrderByDescending(s => s.LibraryBuildLastAttemptedAtUtc)
            .Take(RecentErrorLimit)
            .Select(s => new PipelineError(s.Id, s.FileName, "LibraryBuild", s.LibraryBuildError!, s.LibraryBuildLastAttemptedAtUtc))
            .ToListAsync();

        var recentErrors = enrichErrors
            .Concat(buildErrors)
            .OrderByDescending(e => e.AtUtc)
            .Take(RecentErrorLimit)
            .ToList();

        return new PipelineSummary(
            TotalSongs: total,
            DuplicateCount: duplicates,
            EnrichmentStatus: enrichment.ToDictionary(x => x.Key.ToString(), x => x.Count),
            LibraryBuildStatus: build.ToDictionary(x => x.Key.ToString(), x => x.Count),
            LyricsStatus: lyrics.ToDictionary(x => x.Key.ToString(), x => x.Count),
            ProviderAttempts: providerOutcomes
                .OrderBy(x => x.Provider).ThenBy(x => x.Status)
                .Select(x => new ProviderOutcome(x.Provider.ToString(), x.Status.ToString(), x.Count))
                .ToList(),
            RecentErrors: recentErrors);
    }
}

public record TreeNode(
    string Name,
    string RelativePath,
    bool IsDirectory,
    long? SizeBytes,
    bool? IsAudio,
    List<TreeNode>? Children);

public record DirectoryTreeResult(
    string Root,
    bool Exists,
    int MaxDepth,
    int MaxEntries,
    int TotalEntries,
    bool Truncated,
    TreeNode? Tree);

public record ProviderOutcome(string Provider, string Status, int Count);

public record PipelineError(int SongId, string FileName, string Stage, string Error, DateTime? AtUtc);

public record PipelineSummary(
    int TotalSongs,
    int DuplicateCount,
    IReadOnlyDictionary<string, int> EnrichmentStatus,
    IReadOnlyDictionary<string, int> LibraryBuildStatus,
    IReadOnlyDictionary<string, int> LyricsStatus,
    IReadOnlyList<ProviderOutcome> ProviderAttempts,
    IReadOnlyList<PipelineError> RecentErrors);

using System.IO.Abstractions;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Artwork;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

public record LibraryBuildBatchResult(
    int TotalTracks,
    int Done,
    int Failed,
    TimeSpan Duration);

public interface ILibraryBuilderService
{
    Task<LibraryBuildBatchResult> ProcessNextBatchAsync(Guid runId, CancellationToken ct = default);
}

public interface ILibraryTagWriter
{
    // The album-IDENTITY tags (album, album artist, year, release ids, disc count, compilation,
    // release types) come from the reconciled <paramref name="albumIdentity"/> shared by every track
    // of the album; track-level tags still come from <paramref name="song"/>.
    Task WriteTagsAsync(string path, SongMetadata song, AlbumIdentity albumIdentity, CancellationToken ct = default);
}

public class TagLibLibraryTagWriter : ILibraryTagWriter
{
    private const string VariousArtists = "Various Artists";
    private const string VariousArtistsMbid = "89ad4ac3-39f7-470e-963a-56509c546377";

    static TagLibLibraryTagWriter()
    {
        // ID3v2.4 is required for real multi-value frames (v2.3 concatenates and loses them).
        TagLib.Id3v2.Tag.DefaultVersion = 4;
        TagLib.Id3v2.Tag.ForceDefaultVersion = true;
    }

    public Task WriteTagsAsync(string path, SongMetadata song, AlbumIdentity albumIdentity, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        using var tagFile = TagLib.File.Create(path);
        var tag = tagFile.Tag;

        // Album-IDENTITY fields come from the reconciled identity (shared by every track of the album)
        // so a single on-disk album isn't split by inconsistent per-track enrichment. Track-level
        // fields below still come from the song.
        var compilation = albumIdentity.IsCompilation;
        var albumArtists = compilation ? [VariousArtists] : BuildAlbumArtistArray(albumIdentity.AlbumArtist, song.Artist);

        tag.Title = NullIfEmpty(song.Title);
        tag.Album = NullIfEmpty(albumIdentity.Album);
        // Singular ARTIST is the DISPLAY credit (Navidrome uses it as the display name when the
        // plural ARTISTS frames are present) — always a single value, never a multi-valued frame.
        var displayArtist = BuildDisplayArtist(song.Artist);
        tag.Performers = displayArtist is null ? [] : [displayArtist];
        // ALBUMARTIST stays the main artist only (or "Various Artists" for compilations) so albums
        // never fragment by per-track featured artist.
        tag.AlbumArtists = albumArtists;
        tag.Year = albumIdentity.Year is > 0 ? (uint)albumIdentity.Year.Value : 0;
        tag.Track = song.TrackNumber is > 0 ? (uint)song.TrackNumber.Value : 0;
        // TrackCount is per-disc on multi-disc releases, so it stays per-song; DiscCount is album-level.
        tag.TrackCount = song.TotalTracks is > 0 ? (uint)song.TotalTracks.Value : 0;
        tag.Disc = song.DiscNumber is > 0 ? (uint)song.DiscNumber.Value : 0;
        tag.DiscCount = albumIdentity.TotalDiscs is > 0 ? (uint)albumIdentity.TotalDiscs.Value : 0;
        tag.ISRC = NullIfEmpty(song.Isrc) ?? string.Empty;

        // MusicBrainz IDs — the generic Tag writes the Picard-compatible frame per format.
        // Gotcha: MusicBrainzTrackId is the field that holds the RECORDING id (track-level).
        // Only assign when present: the Xiph (FLAC) setters throw on null.
        SetIfPresent(NullIfEmpty(song.MusicBrainzId), v => tag.MusicBrainzTrackId = v);
        SetIfPresent(NullIfEmpty(albumIdentity.MusicBrainzReleaseId), v => tag.MusicBrainzReleaseId = v);
        SetIfPresent(NullIfEmpty(albumIdentity.MusicBrainzReleaseGroupId), v => tag.MusicBrainzReleaseGroupId = v);
        SetIfPresent(
            compilation ? VariousArtistsMbid : NullIfEmpty(albumIdentity.AlbumArtistMusicBrainzId),
            v => tag.MusicBrainzReleaseArtistId = v);
        var artistIds = MusicHoarder.Api.Metadata.MultiValue.Split(song.ArtistMusicBrainzIds);
        SetIfPresent(artistIds.Length > 0 ? artistIds[0] : null, v => tag.MusicBrainzArtistId = v);

        // Embed lyrics: the user's preferred source (AI transcription or LRCLIB), synced over plain.
        tag.Lyrics = NullIfEmpty(song.EffectiveSyncedLyrics) ?? NullIfEmpty(song.EffectivePlainLyrics) ?? string.Empty;

        // Multi-value / freeform fields the generic Tag doesn't expose. create:false so we only
        // touch the file's native tag (the generic sets above already created it) — never an
        // ID3 tag on a FLAC, which is non-spec and breaks some players.
        WriteExtendedTags(tagFile, song, albumIdentity, albumArtists, compilation);

        tagFile.Save();
        return Task.CompletedTask;
    }

    private static void SetIfPresent(string? value, Action<string> set)
    {
        if (!string.IsNullOrWhiteSpace(value)) set(value);
    }

    private static void WriteExtendedTags(
        TagLib.File file, SongMetadata song, AlbumIdentity albumIdentity, string[] albumArtists, bool compilation)
    {
        // Discrete per-artist values. When the enrichment didn't produce a discrete list, the only
        // safe fallback is the ';' join (AcoustID-style) — every other delimiter (',', '&', '/')
        // occurs in legitimate artist names ("Tyler, The Creator"). With no discrete data we write
        // NO ARTISTS frame at all: a combined credit as a single ARTISTS value would cement a fake
        // merged artist in Navidrome and defeat its own separator heuristics on the singular ARTIST.
        var artists = MusicHoarder.Api.Metadata.MultiValue.Split(song.Artists);
        if (artists.Length == 0 && song.Artist?.Contains(';') == true)
        {
            artists = SplitOnSemicolon(song.Artist);
        }

        var releaseTypes = MusicHoarder.Api.Metadata.MultiValue.Split(albumIdentity.ReleaseTypes);

        // Per-artist MusicBrainz ids, multi-valued and positionally aligned with ARTISTS (Picard's
        // frame names — Navidrome reads them). The generic single-id write above stays as the
        // fallback: when the id list can't be aligned with the artist list we leave that first id
        // in place rather than write a misaligned plural (worse than a partial one). Empty when
        // absent/misaligned so the per-format writes below are skipped, never removed.
        var artistIds = MusicHoarder.Api.Metadata.MultiValue.Split(song.ArtistMusicBrainzIds);
        var alignedArtistIds = artistIds.Length > 1 && artistIds.Length != artists.Length
            ? []
            : artistIds;

        if (file.GetTag(TagLib.TagTypes.Id3v2, false) is TagLib.Id3v2.Tag id3)
        {
            SetId3UserText(id3, "ARTISTS", artists);
            SetId3UserText(id3, "ALBUMARTISTS", albumArtists);
            SetId3UserText(id3, "MusicBrainz Album Type", releaseTypes);
            SetId3Text(id3, "TCMP", compilation ? ["1"] : []);
            if (alignedArtistIds.Length > 0) SetId3UserText(id3, "MusicBrainz Artist Id", alignedArtistIds);
        }

        if (file.GetTag(TagLib.TagTypes.Xiph, false) is TagLib.Ogg.XiphComment xiph)
        {
            SetXiph(xiph, "ARTISTS", artists);
            SetXiph(xiph, "ALBUMARTISTS", albumArtists);
            SetXiph(xiph, "RELEASETYPE", releaseTypes);
            SetXiph(xiph, "COMPILATION", compilation ? ["1"] : []);
            if (alignedArtistIds.Length > 0) SetXiph(xiph, "MUSICBRAINZ_ARTISTID", alignedArtistIds);
        }

        if (file.GetTag(TagLib.TagTypes.Apple, false) is TagLib.Mpeg4.AppleTag apple)
        {
            SetDash(apple, "ARTISTS", artists);
            SetDash(apple, "ALBUMARTISTS", albumArtists);
            SetDash(apple, "MusicBrainz Album Type", releaseTypes);
            apple.IsCompilation = compilation;
            if (alignedArtistIds.Length > 0) SetDash(apple, "MusicBrainz Artist Id", alignedArtistIds);
        }
    }

    /// <summary>
    /// Writes (or clears) an iTunes freeform dash atom, mirroring the empty-array handling of the
    /// ID3/Xiph helpers above. TagLibSharp's <c>SetDashBoxes</c> dereferences <c>datastring[0]</c>
    /// whenever the atom already exists, so calling it with an empty array on a re-tag throws
    /// <see cref="IndexOutOfRangeException"/> — and the AppleTag branch, unlike ID3/Xiph, has no
    /// guard of its own (issue #239). So: with no values, remove any existing atom instead of
    /// writing; only call <c>SetDashBoxes</c> with a non-empty array.
    /// </summary>
    private static void SetDash(TagLib.Mpeg4.AppleTag apple, string name, string[] values)
    {
        const string mean = "com.apple.iTunes";
        if (values.Length == 0)
        {
            // GetDashBoxes returns null when the atom is absent. TagLibSharp stores a multi-value dash
            // atom as one DASH box per value, and SetDashBox("") removes only a single box, so clear
            // them one at a time until none remain (bounded so a non-progressing remove can't spin).
            for (var guard = 0; apple.GetDashBoxes(mean, name)?.Length > 0 && guard < 64; guard++)
            {
                apple.SetDashBox(mean, name, string.Empty);
            }
            return;
        }

        apple.SetDashBoxes(mean, name, values);
    }

    private static void SetId3UserText(TagLib.Id3v2.Tag id3, string description, string[] values)
    {
        if (values.Length == 0)
        {
            var existing = TagLib.Id3v2.UserTextInformationFrame.Get(id3, description, false);
            if (existing is not null) id3.RemoveFrame(existing);
            return;
        }

        TagLib.Id3v2.UserTextInformationFrame.Get(id3, description, true).Text = values;
    }

    private static void SetId3Text(TagLib.Id3v2.Tag id3, TagLib.ByteVector frameId, string[] values)
    {
        if (values.Length == 0)
        {
            var existing = TagLib.Id3v2.TextInformationFrame.Get(id3, frameId, false);
            if (existing is not null) id3.RemoveFrame(existing);
            return;
        }

        TagLib.Id3v2.TextInformationFrame.Get(id3, frameId, true).Text = values;
    }

    private static void SetXiph(TagLib.Ogg.XiphComment xiph, string key, string[] values)
    {
        if (values.Length == 0)
        {
            xiph.RemoveField(key);
            return;
        }

        xiph.SetField(key, values);
    }

    /// <summary>
    /// The single-value display credit for the ARTIST tag. A ';'-joined credit (AcoustID's join)
    /// is humanized to the conventional "A, B &amp; C" form; anything else (including a provider's
    /// own join-phrase credit like "21 Savage, Travis Scott &amp; Metro Boomin") passes through.
    /// </summary>
    internal static string? BuildDisplayArtist(string? artist)
    {
        var parts = SplitOnSemicolon(artist);
        return parts.Length switch
        {
            0 => null,
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + " & " + parts[^1],
        };
    }

    private static string[] SplitOnSemicolon(string? artist)
    {
        var normalized = NullIfEmpty(artist);
        if (normalized is null)
        {
            return [];
        }

        if (normalized.Contains(';'))
        {
            return normalized
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .ToArray();
        }

        return [normalized];
    }

    private static string[] BuildAlbumArtistArray(string? albumArtist, string? artist)
    {
        var normalizedAlbumArtist = NullIfEmpty(albumArtist);
        if (normalizedAlbumArtist is not null)
        {
            return [normalizedAlbumArtist];
        }

        var fallbackArtist = NullIfEmpty(artist);
        return fallbackArtist is null ? [] : [fallbackArtist];
    }

    private static string? NullIfEmpty(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

internal record LibraryBuildTrackCandidate(int SongId, string DestinationPath, string AlbumFolderKey);

internal enum LibraryBuildOutcome
{
    Done,
    Failed,
}

// Carries what the post-batch album-cover pass needs from a successful build: the source file to
// resolve art from and whether the track is unreleased (those folders mix unrelated singles, so we
// don't drop a single shared cover into them). The owner/song/album fields let the cover pass stamp a
// LibraryWriteEvent without re-loading the row.
internal sealed record LibraryBuildTrackResult(
    LibraryBuildOutcome Outcome,
    string? SourcePath = null,
    bool IsUnreleased = false,
    Guid OwnerUserId = default,
    int SongId = 0,
    string? Album = null,
    string? AlbumArtist = null,
    string? MusicBrainzReleaseId = null,
    string? MusicBrainzReleaseGroupId = null);

// What the post-batch cover pass needs to write one cover.* per folder and record the write. The
// MBIDs (from the reconciled album identity) key the external cover fetch when source art is absent.
internal sealed record CoverPassEntry(
    string SourcePath, Guid OwnerUserId, int SongId, string? Album, string? AlbumArtist,
    string? MusicBrainzReleaseId, string? MusicBrainzReleaseGroupId);

public class LibraryBuilderService(
    IServiceScopeFactory scopeFactory,
    IDestinationPathResolver destinationPathResolver,
    IFileSystem fileSystem,
    ILibraryDestinationCleaner destinationCleaner,
    ILibraryTagWriter tagWriter,
    IAlbumCoverWriter albumCoverWriter,
    IAlbumIdentityReconciler albumIdentityReconciler,
    IOptions<MusicEnricherOptions> options,
    Observability.PipelineMetrics metrics,
    ILogger<LibraryBuilderService> logger) : ILibraryBuilderService
{
    private const int CopyBufferSize = 1024 * 1024;
    private static long tempFileSequence;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> destinationLocks = new(StringComparer.Ordinal);

    public async Task<LibraryBuildBatchResult> ProcessNextBatchAsync(Guid runId, CancellationToken ct = default)
    {
        var startedAt = DateTime.UtcNow;
        var opts = options.Value;

        List<LibraryBuildTrackCandidate> candidates;
        // Destination album folder -> the single album identity every track in it must be tagged with.
        // Empty when reconciliation is disabled (ProcessTrackAsync then falls back to per-song tags).
        var identityByFolder = new Dictionary<string, AlbumIdentity>(StringComparer.Ordinal);
        using (var scope = scopeFactory.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
            // Background service: bypass the per-user query filter. Skip synthetic (demo) rows —
            // they have no real source file to copy and are pre-seeded as Done. A fresh build also
            // waits (bounded) for the per-song lyrics fetch so the file lands with lyrics embedded.
            var rawCandidates = await LibraryBuildQuery.BuildCandidates(
                    db.Songs.IgnoreQueryFilters().AsNoTracking(),
                    LibraryBuildQuery.LyricsWaitCutoff(opts),
                    opts.MaxLibraryBuildAttempts)
                .OrderBy(s => s.Id)
                .Take(opts.LibraryBuilderBatchSize)
                .ToListAsync(ct);

            var uniqueDestinationPaths = new HashSet<string>(StringComparer.Ordinal);
            candidates = [];
            foreach (var candidateSong in rawCandidates)
            {
                var destinationPath = destinationPathResolver.ResolvePath(candidateSong);
                if (uniqueDestinationPaths.Add(destinationPath))
                {
                    var folderKey = Path.GetDirectoryName(destinationPath) ?? string.Empty;
                    candidates.Add(new LibraryBuildTrackCandidate(candidateSong.Id, destinationPath, folderKey));
                }
                else
                {
                    logger.LogWarning(
                        "Deferring song {SongId}: destination path collision in current batch for {DestinationPath}",
                        candidateSong.Id,
                        destinationPath);
                }
            }

            if (candidates.Count > 0 && opts.EnableAlbumIdentityReconciliation)
            {
                await BuildAlbumIdentityMapAsync(db, candidates, identityByFolder, ct);
            }
        }

        if (candidates.Count == 0)
        {
            return new LibraryBuildBatchResult(0, 0, 0, DateTime.UtcNow - startedAt);
        }

        logger.LogInformation("Starting library build run {RunId} with {Count} tracks", runId, candidates.Count);

        var done = 0;
        var failed = 0;
        var semaphore = new SemaphoreSlim(opts.LibraryBuilderWorkerConcurrency, opts.LibraryBuilderWorkerConcurrency);

        // Destination album folder -> the cover-pass payload (representative source file + owner/album
        // for the write event). Populated by successful, non-unreleased builds; drained once after the
        // track loop so each folder gets a single cover.* write with no intra-album races.
        var coverDirectories = new ConcurrentDictionary<string, CoverPassEntry>(StringComparer.Ordinal);

        await Parallel.ForEachAsync(
            candidates,
            new ParallelOptions
            {
                // Match the worker concurrency (the gating semaphore) rather than 2×: the surplus
                // tasks only pinned more thread-pool threads on synchronous TagLib tag writes,
                // starving request handling under load.
                MaxDegreeOfParallelism = opts.LibraryBuilderWorkerConcurrency,
                CancellationToken = ct
            },
            async (candidate, token) =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    LibraryBuildTrackResult result;
                    identityByFolder.TryGetValue(candidate.AlbumFolderKey, out var albumIdentity);
                    using (await AcquireDestinationLockAsync(candidate.DestinationPath, token))
                    {
                        result = await ProcessTrackAsync(candidate.SongId, candidate.DestinationPath, albumIdentity, runId, token);
                    }

                    switch (result.Outcome)
                    {
                        case LibraryBuildOutcome.Done:
                            Interlocked.Increment(ref done);
                            if (!result.IsUnreleased && !string.IsNullOrEmpty(result.SourcePath))
                            {
                                var directory = fileSystem.Path.GetDirectoryName(candidate.DestinationPath);
                                if (!string.IsNullOrEmpty(directory))
                                {
                                    coverDirectories.TryAdd(directory, new CoverPassEntry(
                                        result.SourcePath, result.OwnerUserId, result.SongId, result.Album, result.AlbumArtist,
                                        result.MusicBrainzReleaseId, result.MusicBrainzReleaseGroupId));
                                }
                            }
                            break;
                        case LibraryBuildOutcome.Failed:
                            Interlocked.Increment(ref failed);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            });

        await WriteAlbumCoversAsync(coverDirectories, runId, opts.LibraryBuilderWorkerConcurrency, ct);

        var duration = DateTime.UtcNow - startedAt;
        logger.LogInformation(
            "Library build run {RunId} complete: Total={Total}, Done={Done}, Failed={Failed}, Duration={Duration:F1}s",
            runId,
            candidates.Count,
            done,
            failed,
            duration.TotalSeconds);

        metrics.RecordStageDuration("build", duration.TotalSeconds);
        metrics.RecordTerminal("built", done);
        metrics.RecordTerminal("build_failed", failed);

        return new LibraryBuildBatchResult(candidates.Count, done, failed, duration);
    }

    // Elects one album identity per destination folder represented in this batch. Crucially it elects
    // from the FULL folder membership (every matched, buildable song that resolves to the folder), not
    // just the batch slice — so an album whose tracks straddle batches still gets one stable identity.
    // Unreleased tracks are excluded: they share a per-artist "Unreleased" folder with unrelated
    // singles, so they keep their own (per-song) tags.
    private async Task BuildAlbumIdentityMapAsync(
        MusicHoarderDbContext db,
        IReadOnlyList<LibraryBuildTrackCandidate> candidates,
        Dictionary<string, AlbumIdentity> identityByFolder,
        CancellationToken ct)
    {
        var batchFolders = candidates.Select(c => c.AlbumFolderKey).ToHashSet(StringComparer.Ordinal);

        var allMembers = await db.Songs
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null && !s.IsSynthetic)
            // Destination folder keys carry no owner segment, so without this a demo album with the
            // same artist/album as an owner album would vote in the owner's identity election.
            .ExcludingDemoTenant()
            .Where(s => !s.IsDuplicate && !s.IsUnreleased)
            .Where(s => s.EnrichmentStatus == EnrichmentStatus.Matched)
            .ToListAsync(ct);

        var grouped = allMembers
            .Select(s => (Folder: Path.GetDirectoryName(destinationPathResolver.ResolvePath(s)) ?? string.Empty, Song: s))
            .Where(x => batchFolders.Contains(x.Folder))
            .GroupBy(x => x.Folder, StringComparer.Ordinal);

        foreach (var group in grouped)
        {
            identityByFolder[group.Key] = albumIdentityReconciler.Reconcile(group.Select(x => x.Song).ToList());
        }
    }

    private async Task<LibraryBuildTrackResult> ProcessTrackAsync(
        int songId, string destinationPath, AlbumIdentity? albumIdentity, Guid runId, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var song = await db.Songs.IgnoreQueryFilters().FirstOrDefaultAsync(s => s.Id == songId, ct);
        if (song is null || song.IsDeleted || song.EnrichmentStatus != EnrichmentStatus.Matched)
        {
            logger.LogDebug("Skipping song {SongId}: not buildable (missing/deleted/not-matched)", songId);
            return new LibraryBuildTrackResult(LibraryBuildOutcome.Failed);
        }

        // No reconciled identity for this folder (reconciliation disabled, or an unreleased/loner
        // track): fall back to the song's own tags — exactly today's behavior.
        var identity = albumIdentity ?? AlbumIdentity.FromSong(song);

        var legacyPath = ResolveLegacyDestinationPath(song);
        var currentManagedPath = ResolveCurrentManagedPath(song, destinationPath, legacyPath);

        if (currentManagedPath is not null && !PathsEqual(currentManagedPath, destinationPath))
        {
            song.PreviousDestinationPath = currentManagedPath;
        }

        var destinationDirectory = fileSystem.Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(destinationDirectory))
        {
            logger.LogWarning(
                "Library build failed for {Track} (SongId={SongId}): destination directory resolution returned empty. DestinationPath={DestinationPath}",
                song.TrackLabel, songId, destinationPath);
            song.MarkBuildFailed("Could not resolve destination directory");
            await db.SaveChangesAsync(ct);
            return new LibraryBuildTrackResult(LibraryBuildOutcome.Failed);
        }

        var tempPath = BuildTempPath(destinationPath, songId);
        song.LibraryBuildLastAttemptedAtUtc = DateTime.UtcNow;

        try
        {
            logger.LogInformation("Building {Track} (SongId={SongId}) -> {DestinationPath}",
                song.TrackLabel, songId, destinationPath);

            fileSystem.Directory.CreateDirectory(destinationDirectory);

            // Skip the copy when a same-size file already sits at the destination — but only on a
            // fresh build. A forced rebuild (signalled by PreviousDestinationPath, set by
            // ResetLibraryBuild) must always re-copy + re-tag so changed metadata reaches the file;
            // the size heuristic compares against the source and could otherwise drop the re-tag.
            if (string.IsNullOrWhiteSpace(song.PreviousDestinationPath) && fileSystem.File.Exists(destinationPath))
            {
                var existingSize = fileSystem.FileInfo.New(destinationPath).Length;
                if (existingSize == song.FileSizeBytes)
                {
                    // No tags were rewritten, so emit no LibraryWriteEvent and leave the written-tags
                    // snapshot intact — the prior write's record stays accurate.
                    song.MarkBuildDone(destinationPath);
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation(
                        "Skipping copy for {Track} (SongId={SongId}): destination already exists with same size ({Bytes} bytes)",
                        song.TrackLabel, songId, existingSize);
                    return new LibraryBuildTrackResult(
                        LibraryBuildOutcome.Done, song.SourcePath, song.IsUnreleased,
                        song.OwnerUserId, song.Id, identity.Album, EffectiveAlbumArtist(song, identity),
                        identity.MusicBrainzReleaseId, identity.MusicBrainzReleaseGroupId);
                }
            }

            if (fileSystem.File.Exists(tempPath))
            {
                fileSystem.File.Delete(tempPath);
            }

            await StreamCopyAsync(song.SourcePath, tempPath, ct);
            song.MarkCopied();
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Copied {Track} (SongId={SongId}) to temp file {TempPath}",
                song.TrackLabel, songId, tempPath);

            await tagWriter.WriteTagsAsync(tempPath, song, identity, ct);
            song.MarkTagged();
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Tagged temp file for {Track} (SongId={SongId})",
                song.TrackLabel, songId);

            if (fileSystem.File.Exists(destinationPath))
            {
                fileSystem.File.Delete(destinationPath);
            }

            fileSystem.File.Move(tempPath, destinationPath);
            if (!string.IsNullOrWhiteSpace(song.PreviousDestinationPath)
                && !PathsEqual(song.PreviousDestinationPath, destinationPath))
            {
                destinationCleaner.DeleteManagedPathAndPrune(song.PreviousDestinationPath, options.Value.DestinationDirectory);
            }

            // Record what actually landed on disk, diffed against the previous write (or the
            // source-original baseline on first build), as the destination-side change history the
            // History feed reads. Same transaction as MarkBuildDone below, so the build can't be marked
            // Done without its events. A no-op re-tag (empty diff) adds nothing.
            RecordTrackWrite(db, song, identity, destinationPath, runId);

            song.MarkBuildDone(destinationPath);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Library build complete for {Track} (SongId={SongId}): {DestinationPath}",
                song.TrackLabel, songId, destinationPath);

            return new LibraryBuildTrackResult(
                LibraryBuildOutcome.Done, song.SourcePath, song.IsUnreleased,
                song.OwnerUserId, song.Id, identity.Album, EffectiveAlbumArtist(song, identity),
                identity.MusicBrainzReleaseId, identity.MusicBrainzReleaseGroupId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            TryDeleteFileBestEffort(tempPath, songId);

            logger.LogWarning(ex,
                "Library build failed for {Track} (SongId={SongId}). Source={SourcePath}, Temp={TempPath}, Destination={DestinationPath}",
                song.TrackLabel, songId, song.SourcePath, tempPath, destinationPath);
            song.MarkBuildFailed(ex.Message);
            await db.SaveChangesAsync(ct);
            if (song.LibraryBuildAttempts >= options.Value.MaxLibraryBuildAttempts)
            {
                // Quarantined: the build query stops selecting this row until a manual re-build/re-enrich
                // resets the counter, so one un-writable file can't loop the builder forever (issue #239).
                logger.LogError(
                    "Quarantining {Track} (SongId={SongId}) after {Attempts} failed build attempts; "
                    + "excluded from the build queue until reset. Last error: {Error}",
                    song.TrackLabel, songId, song.LibraryBuildAttempts, ex.Message);
            }
            return new LibraryBuildTrackResult(LibraryBuildOutcome.Failed);
        }
    }

    // Writes a cover.<ext> into each freshly-built album folder that doesn't already have a
    // cover/folder/front.* image, lifting art from a representative source track (folder image first,
    // else embedded — Navidrome's order), and falling back to external providers (Cover Art Archive →
    // Deezer → iTunes) when the source has none. One task per directory, so no intra-album races.
    // Best-effort: a cover failure never fails the build. When a cover is actually written it records
    // an AlbumCoverWritten event so the History feed can surface "Cover art added for <album>".
    private async Task WriteAlbumCoversAsync(
        ConcurrentDictionary<string, CoverPassEntry> directories,
        Guid runId,
        int maxDegreeOfParallelism,
        CancellationToken ct)
    {
        if (directories.IsEmpty)
        {
            return;
        }

        await Parallel.ForEachAsync(
            directories,
            new ParallelOptions { MaxDegreeOfParallelism = maxDegreeOfParallelism, CancellationToken = ct },
            async (entry, token) =>
            {
                var payload = entry.Value;
                var externalQuery = new ExternalCoverArtQuery(
                    payload.MusicBrainzReleaseId, payload.MusicBrainzReleaseGroupId,
                    payload.AlbumArtist, payload.Album);
                var result = await albumCoverWriter.WriteIfMissingAsync(
                    entry.Key, payload.SourcePath, externalQuery, token);
                if (!result.Written)
                {
                    return;
                }

                using var scope = scopeFactory.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
                db.LibraryWriteEvents.Add(new LibraryWriteEvent
                {
                    OwnerUserId = payload.OwnerUserId,
                    RunId = runId,
                    SongId = payload.SongId == 0 ? null : payload.SongId,
                    Kind = LibraryWriteEventKind.AlbumCoverWritten,
                    WrittenAtUtc = DateTime.UtcNow,
                    AlbumFolder = entry.Key,
                    AlbumArtist = payload.AlbumArtist,
                    Album = payload.Album,
                    FieldName = "Cover",
                    NewValue = result.Source == "source" ? "written" : $"fetched:{result.Source}",
                });
                await db.SaveChangesAsync(token);

                // Reflect the just-written cover into HasCoverArt so the grid/hero request it; an
                // art-less source leaves the flag false otherwise (it's only set from the source side).
                await DestinationCoverFlagger.FlagFolderAsync(
                    db, entry.Key, fileSystem.Path.DirectorySeparatorChar, token);
            });
    }

    // Records one LibraryWriteEvent per tag field that differs from the previous write (or, on a first
    // build, from the source-original baseline), then refreshes the written-tags snapshot. Adds the
    // events to the SAME DbContext as the build completion so they commit atomically with MarkBuildDone.
    private void RecordTrackWrite(
        MusicHoarderDbContext db, SongMetadata song, AlbumIdentity identity, string destinationPath, Guid runId)
    {
        var current = WrittenTagSet.From(song, identity);
        var previous = ResolvePreviousTagSet(song, current);
        var diffs = WrittenTagSet.Diff(previous, current);

        var now = DateTime.UtcNow;
        if (diffs.Count > 0)
        {
            var albumFolder = fileSystem.Path.GetDirectoryName(destinationPath);
            foreach (var (field, oldValue, newValue, isAlbumIdentity) in diffs)
            {
                db.LibraryWriteEvents.Add(new LibraryWriteEvent
                {
                    OwnerUserId = song.OwnerUserId,
                    RunId = runId,
                    SongId = song.Id,
                    Kind = LibraryWriteEventKind.TrackTagsWritten,
                    WrittenAtUtc = now,
                    DestinationPath = destinationPath,
                    AlbumFolder = albumFolder,
                    AlbumArtist = current.AlbumArtist,
                    Album = current.Album,
                    FieldName = field,
                    OldValue = oldValue,
                    NewValue = newValue,
                    IsAlbumIdentityField = isAlbumIdentity,
                });
            }
        }

        song.LastWrittenTagsJson = JsonSerializer.Serialize(current);
        song.LastWrittenAtUtc = now;
    }

    private static WrittenTagSet ResolvePreviousTagSet(SongMetadata song, WrittenTagSet current)
    {
        if (!string.IsNullOrWhiteSpace(song.LastWrittenTagsJson))
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<WrittenTagSet>(song.LastWrittenTagsJson);
                if (snapshot is not null)
                {
                    return snapshot;
                }
            }
            catch (JsonException)
            {
                // Snapshot schema drift / corruption — fall back to the source-original baseline.
            }
        }

        return WrittenTagSet.FromOriginal(song, current);
    }

    private static string? EffectiveAlbumArtist(SongMetadata song, AlbumIdentity identity)
        => WrittenTagSet.From(song, identity).AlbumArtist;

    private async Task StreamCopyAsync(string sourcePath, string tempDestinationPath, CancellationToken ct)
    {
        await using var source = fileSystem.File.Open(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        await using var destination = fileSystem.File.Open(
            tempDestinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None);
        await source.CopyToAsync(destination, CopyBufferSize, ct);
        await destination.FlushAsync(ct);
    }

    private void TryDeleteFileBestEffort(string path, int songId)
    {
        try
        {
            if (!fileSystem.File.Exists(path))
            {
                return;
            }

            fileSystem.File.Delete(path);
        }
        catch (Exception cleanupEx)
        {
            logger.LogWarning(cleanupEx,
                "Cleanup failed while deleting temp file for SongId={SongId} at {Path}",
                songId, path);
        }
    }

    private string BuildTempPath(string destinationPath, int songId)
    {
        var directory = fileSystem.Path.GetDirectoryName(destinationPath);
        var fileNameWithoutExtension = fileSystem.Path.GetFileNameWithoutExtension(destinationPath);
        var extension = fileSystem.Path.GetExtension(destinationPath);
        var uniqueToken = Interlocked.Increment(ref tempFileSequence);

        var tempSuffix = $".tmp.{songId}.{uniqueToken}";
        var tempFileName = string.IsNullOrWhiteSpace(extension)
            ? $"{fileNameWithoutExtension}{tempSuffix}"
            : $"{fileNameWithoutExtension}{tempSuffix}{extension}";

        return string.IsNullOrWhiteSpace(directory)
            ? tempFileName
            : fileSystem.Path.Combine(directory, tempFileName);
    }

    private async Task<IDisposable> AcquireDestinationLockAsync(string destinationPath, CancellationToken ct)
    {
        var canonicalPath = fileSystem.Path.GetFullPath(destinationPath);
        var semaphore = destinationLocks.GetOrAdd(canonicalPath, static _ => new SemaphoreSlim(1, 1));
        await semaphore.WaitAsync(ct);
        return new DestinationLockReleaser(semaphore);
    }

    private sealed class DestinationLockReleaser(SemaphoreSlim semaphore) : IDisposable
    {
        private readonly SemaphoreSlim semaphore = semaphore;
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            semaphore.Release();
        }
    }

    private string? ResolveCurrentManagedPath(SongMetadata song, string desiredPath, string legacyPath)
    {
        if (!string.IsNullOrWhiteSpace(song.DestinationPath))
        {
            return song.DestinationPath;
        }

        if (fileSystem.File.Exists(desiredPath))
        {
            return desiredPath;
        }

        return fileSystem.File.Exists(legacyPath) ? legacyPath : null;
    }

    private string ResolveLegacyDestinationPath(SongMetadata song)
    {
        var artist = NormalizeSegment(song.Artist, "Unknown Artist");
        var title = NormalizeSegment(song.Title, "Unknown Title");
        var extension = NormalizeExtension(song.Extension);

        if (song.IsUnreleased)
        {
            return Path.Combine(
                options.Value.DestinationDirectory,
                artist,
                "Unreleased",
                $"{title}{extension}");
        }

        var album = NormalizeSegment(song.Album, "Unknown Album");
        var albumFolder = song.Year is > 0
            ? $"{song.Year.Value} - {album}"
            : album;
        var trackPrefix = song.TrackNumber is > 0
            ? $"{song.TrackNumber.Value:00} - "
            : string.Empty;

        return Path.Combine(
            options.Value.DestinationDirectory,
            artist,
            albumFolder,
            $"{trackPrefix}{title}{extension}");
    }

    private static bool PathsEqual(string a, string b)
        => string.Equals(a, b, StringComparison.Ordinal);

    private static string NormalizeSegment(string? value, string fallback)
    {
        var sanitized = DestinationPathResolver.Sanitize(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            sanitized = fallback;
        }

        return sanitized.Length <= 60 ? sanitized : sanitized[..60];
    }

    private static string NormalizeExtension(string? extension)
    {
        var sanitized = DestinationPathResolver.Sanitize(extension ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(sanitized))
        {
            return string.Empty;
        }

        return sanitized.StartsWith(".", StringComparison.Ordinal)
            ? sanitized
            : $".{sanitized}";
    }
}

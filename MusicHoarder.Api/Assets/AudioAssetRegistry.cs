using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MusicHoarder.Api.Logging;
using MusicHoarder.Api.Options;
using MusicHoarder.Api.Persistence;
using MusicHoarder.Api.Scanner;

namespace MusicHoarder.Api.Assets;

/// <summary>What reading a file once tells us: its SHA-256 and what the tag reader says about its audio.</summary>
public sealed record AudioFileFacts(
    string Path,
    string Sha256,
    long SizeBytes,
    string Container,
    string? Codec,
    int? Bitrate,
    int? SampleRate,
    int? DurationMs,
    DateTime LastWriteUtc);

/// <summary>
/// Registers audio files as <see cref="AudioAsset"/>s and links songs to them. Two ways in:
/// <list type="bullet">
/// <item>A file that already exists is registered where it is (<see cref="LinkAsync"/>): the hosted build
/// and the adoption job read it and never move or change it.</item>
/// <item>A hosted download is moved from staging into the content store (<see cref="StoreDownloadAsync"/>),
/// unless the same bytes are already stored, in which case the staged copy is dropped.</item>
/// </list>
/// Reading a file (<see cref="ReadAsync"/>) touches only the disk, so callers can do it in parallel and
/// keep the database work on one context.
/// </summary>
public sealed class AudioAssetRegistry(
    IOptions<MusicEnricherOptions> options,
    TimeProvider time,
    ILogger<AudioAssetRegistry> logger)
{
    private const int ReadBufferSize = 1024 * 1024;
    private static readonly SemaphoreSlim Claims = new(1, 1);

    /// <summary>
    /// Held by anything that links songs to assets and saves, or deletes a file an asset could store:
    /// the hosted build, the adoption job, hosted downloads, the staged-source release, upgrade merges
    /// and report removals. Two linkers then never insert the same hash, and a release never deletes a
    /// file adoption is linking. The API runs as one process, so an in-process lock covers every writer.
    /// Not re-entrant: take it once, around the read-check-save of one unit of work.
    /// </summary>
    public static async Task<IDisposable> ClaimAsync(CancellationToken ct)
    {
        await Claims.WaitAsync(ct);
        return new Claim();
    }

    private sealed class Claim : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
                Claims.Release();
        }
    }

    /// <summary>Hashes and probes a file. Null when it does not exist.</summary>
    public async Task<AudioFileFacts?> ReadAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
            return null;

        var lastWrite = File.GetLastWriteTimeUtc(path);
        string sha;
        long size;
        await using (var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, ReadBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            size = stream.Length;
            sha = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct));
        }

        var (codec, bitrate, sampleRate, durationMs) = Probe(path);
        return new AudioFileFacts(
            NormalizePath(path), sha, size, ContainerOf(path), codec, bitrate, sampleRate, durationMs, lastWrite);
    }

    /// <summary>
    /// Whether the file is still the one <see cref="ReadAsync"/> hashed: there, with the same size and
    /// modification time. Linkers check it under <see cref="ClaimAsync"/>, after hashing outside it.
    /// </summary>
    public static bool Unchanged(AudioFileFacts facts)
    {
        var file = new FileInfo(facts.Path);
        return file.Exists && file.Length == facts.SizeBytes && file.LastWriteTimeUtc == facts.LastWriteUtc;
    }

    /// <summary>
    /// The SHA-256 a content-store file is named by, without reading it: a stored file is never
    /// written to, so its name stays its hash. Null for a path outside the store.
    /// </summary>
    public string? ContentStoreSha(string path)
    {
        var store = options.Value.ResolvedContentStoreDirectory;
        if (string.IsNullOrWhiteSpace(store))
            return null;
        var normalized = NormalizePath(path);
        if (!normalized.StartsWith(IndexService.NormalizeRootPrefix(store), StringComparison.Ordinal))
            return null;
        var stem = System.IO.Path.GetFileNameWithoutExtension(normalized);
        return stem.Length == 64 && stem.All(Uri.IsHexDigit) ? stem.ToLowerInvariant() : null;
    }

    /// <summary>The asset already holding these bytes, including one added to this context but not yet saved.</summary>
    public static async Task<AudioAsset?> FindAsync(MusicHoarderDbContext db, string sha256, CancellationToken ct)
        => db.AudioAssets.Local.FirstOrDefault(a => a.Sha256 == sha256)
            ?? await db.AudioAssets.FirstOrDefaultAsync(a => a.Sha256 == sha256, ct);

    /// <summary>
    /// Points <paramref name="song"/> at the asset for <paramref name="facts"/>, registering the file
    /// where it is when no asset has these bytes yet. Does not save; call it under <see cref="ClaimAsync"/>
    /// and save before letting go.
    /// </summary>
    public async Task<AudioAsset> LinkAsync(
        MusicHoarderDbContext db, SongMetadata song, AudioFileFacts facts, CancellationToken ct)
    {
        var asset = await FindAsync(db, facts.Sha256, ct) ?? Add(db, facts, facts.Path);
        Link(song, asset, facts.Path);
        return asset;
    }

    /// <summary>
    /// Points <paramref name="song"/> at an asset whose bytes are those of <paramref name="filePath"/>.
    /// A purged asset is live again, stored at that file. Does not save.
    /// </summary>
    public void Link(SongMetadata song, AudioAsset asset, string filePath)
    {
        if (asset.PurgedAtUtc is not null)
        {
            asset.PurgedAtUtc = null;
            asset.StoragePath = NormalizePath(filePath);
        }
        song.AudioAsset = asset;
        if (asset.Id != 0)
            song.AudioAssetId = asset.Id;
        asset.LastReferencedAtUtc = time.GetUtcNow().UtcDateTime;
        if (asset.Fingerprint is null && !string.IsNullOrEmpty(song.Fingerprint))
            asset.Fingerprint = song.Fingerprint;
    }

    /// <summary>
    /// Takes a finished download out of staging. When an asset with the same bytes exists and its file
    /// is still there, the staged copy is deleted and the existing file's path returned. Otherwise the
    /// file moves to <c>ab/cd/&lt;sha256&gt;.&lt;ext&gt;</c> in the content store and is registered.
    /// Returns where the download now lives; the asset is added to the context but not saved.
    /// </summary>
    public async Task<string> StoreDownloadAsync(MusicHoarderDbContext db, AudioFileFacts staged, CancellationToken ct)
    {
        var existing = await FindAsync(db, staged.Sha256, ct);
        if (existing is { PurgedAtUtc: null } && await StillHoldsAsync(existing, ct))
        {
            DeleteStaged(staged.Path);
            existing.LastReferencedAtUtc = time.GetUtcNow().UtcDateTime;
            return existing.StoragePath;
        }

        var storedPath = ContentPath(staged.Sha256, staged.Container);
        MoveIntoStore(staged.Path, storedPath);

        if (existing is not null)
        {
            // Its file was purged or lost: the same bytes are back, now in the store.
            existing.StoragePath = storedPath;
            existing.PurgedAtUtc = null;
            existing.LastReferencedAtUtc = time.GetUtcNow().UtcDateTime;
            return storedPath;
        }

        Add(db, staged, storedPath);
        return storedPath;
    }

    /// <summary>
    /// Whether the file at <paramref name="path"/> is one an asset stores. Such a file is the copy every
    /// song with those bytes plays, so a song changing files must leave it where it is.
    /// </summary>
    public async Task<bool> IsStoredAsync(MusicHoarderDbContext db, string path, CancellationToken ct)
    {
        if (ContentStoreSha(path) is not null)
            return true;
        var normalized = NormalizePath(path);
        return await db.AudioAssets.AnyAsync(a => a.StoragePath == normalized && a.PurgedAtUtc == null, ct);
    }

    /// <summary>
    /// Before removing a song deletes its file: false when an asset stored there is played by another
    /// live song, in any library. Otherwise every asset stored at the path is marked purged, to be saved
    /// with the caller's other changes, and the caller may delete the file.
    /// </summary>
    public async Task<bool> TryPurgeForSongAsync(MusicHoarderDbContext db, string path, int songId, CancellationToken ct)
    {
        var normalized = NormalizePath(path);
        var assets = await db.AudioAssets
            .Where(a => a.StoragePath == normalized && a.PurgedAtUtc == null)
            .ToListAsync(ct);
        if (assets.Count == 0)
            return true;

        var ids = assets.Select(a => a.Id).ToList();
        var shared = await db.Songs
            .IgnoreQueryFilters()
            .AnyAsync(s => s.AudioAssetId != null && ids.Contains(s.AudioAssetId.Value)
                && s.Id != songId && s.DeletedAtUtc == null, ct);
        if (shared)
            return false;

        var now = time.GetUtcNow().UtcDateTime;
        foreach (var asset in assets)
            asset.PurgedAtUtc = now;
        return true;
    }

    /// <summary>
    /// Whether the asset's file still has its bytes. A content-store file is never written to, so being
    /// there is enough; a file registered in place may have been changed since, so it is hashed again.
    /// </summary>
    private async Task<bool> StillHoldsAsync(AudioAsset asset, CancellationToken ct)
    {
        if (!File.Exists(asset.StoragePath))
            return false;
        if (ContentStoreSha(asset.StoragePath) == asset.Sha256)
            return true;
        try
        {
            return (await ReadAsync(asset.StoragePath, ct))?.Sha256 == asset.Sha256;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Where the content store keeps a file with this hash.</summary>
    public string ContentPath(string sha256, string container)
    {
        var store = options.Value.ResolvedContentStoreDirectory;
        if (string.IsNullOrWhiteSpace(store))
            throw new InvalidOperationException("No content store is configured (MusicEnricher:ContentStoreDirectory or DownloadDirectory).");
        var name = string.IsNullOrEmpty(container) ? sha256 : $"{sha256}.{container}";
        return NormalizePath(System.IO.Path.Combine(store, sha256[..2], sha256[2..4], name));
    }

    private AudioAsset Add(MusicHoarderDbContext db, AudioFileFacts facts, string storagePath)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var asset = new AudioAsset
        {
            Sha256 = facts.Sha256,
            StoragePath = storagePath,
            SizeBytes = facts.SizeBytes,
            Container = facts.Container,
            Codec = Clip(facts.Codec, 128),
            Bitrate = facts.Bitrate,
            SampleRate = facts.SampleRate,
            DurationMs = facts.DurationMs,
            CreatedAtUtc = now,
            LastReferencedAtUtc = now,
        };
        db.AudioAssets.Add(asset);
        return asset;
    }

    /// <summary>
    /// Moves through a hidden temporary name in the target folder, so a scan never indexes a file that
    /// is still being copied across volumes, and the final name appears in one rename.
    /// </summary>
    private void MoveIntoStore(string stagedPath, string storedPath)
    {
        var directory = System.IO.Path.GetDirectoryName(storedPath)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(storedPath))
        {
            // Same name, so same bytes: an earlier store whose row was never saved.
            DeleteStaged(stagedPath);
            return;
        }

        var temporary = System.IO.Path.Combine(directory, $".{Guid.NewGuid():N}.tmp");
        File.Move(stagedPath, temporary);
        try
        {
            File.Move(temporary, storedPath);
        }
        catch (IOException) when (File.Exists(storedPath))
        {
            File.Delete(temporary);
        }
    }

    private void DeleteStaged(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not delete staged download {Path}", LogSanitizer.ForLog(path));
        }
    }

    private (string? Codec, int? Bitrate, int? SampleRate, int? DurationMs) Probe(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var properties = file.Properties;
            if (properties is null)
                return (null, null, null, null);
            var codec = properties.Codecs.OfType<TagLib.ICodec>().FirstOrDefault()?.Description;
            var duration = (int)properties.Duration.TotalMilliseconds;
            return (
                string.IsNullOrWhiteSpace(codec) ? null : codec,
                properties.AudioBitrate > 0 ? properties.AudioBitrate : null,
                properties.AudioSampleRate > 0 ? properties.AudioSampleRate : null,
                duration > 0 ? duration : null);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read audio properties of {Path}", LogSanitizer.ForLog(path));
            return (null, null, null, null);
        }
    }

    private static string ContainerOf(string path)
        => System.IO.Path.GetExtension(path).TrimStart('.').ToLowerInvariant() is { Length: > 0 and <= 16 } ext ? ext : "";

    private static string NormalizePath(string path) => path.Replace('\\', '/');

    private static string? Clip(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Playback;

/// <summary>The durable fields of one account's session, as <see cref="PlaybackSession"/> stores them.</summary>
public sealed record PlaybackSessionRecord(
    Guid OwnerUserId,
    int SongId,
    IReadOnlyList<int> Queue,
    int QueueIndex,
    long PositionMs,
    long? DurationMs,
    bool IsPlaying,
    double PlaybackRate,
    int? RadioSeedId,
    bool Shuffle,
    string? Title,
    string? Artist,
    string? Album,
    string? ActiveDeviceId,
    string? ActiveDeviceName,
    long Version,
    DateTime UpdatedAtUtc);

/// <summary>Where <see cref="PlaybackCoordinator"/> remembers sessions across restarts.</summary>
public interface IPlaybackSessionStore
{
    Task<PlaybackSessionRecord?> LoadAsync(Guid userId, CancellationToken ct);

    /// <summary>Upserts each session by its owner.</summary>
    Task SaveAsync(IReadOnlyCollection<PlaybackSessionRecord> sessions, CancellationToken ct);
}

/// <summary>
/// The <see cref="PlaybackSession"/> table. The coordinator is a singleton that loads on first
/// access and flushes from a background service, so each call opens its own scope and bypasses the
/// per-user query filter with an explicit <c>OwnerUserId</c> — the same posture as the other
/// background writers.
/// </summary>
public sealed class EfPlaybackSessionStore(IServiceScopeFactory scopeFactory) : IPlaybackSessionStore
{
    public async Task<PlaybackSessionRecord?> LoadAsync(Guid userId, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var row = await db.PlaybackSessions
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerUserId == userId, ct);

        return row is null ? null : ToRecord(row);
    }

    public async Task SaveAsync(IReadOnlyCollection<PlaybackSessionRecord> sessions, CancellationToken ct)
    {
        if (sessions.Count == 0) return;

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var owners = sessions.Select(s => s.OwnerUserId).Distinct().ToList();
        var rows = await db.PlaybackSessions
            .IgnoreQueryFilters()
            .Where(s => owners.Contains(s.OwnerUserId))
            .ToDictionaryAsync(s => s.OwnerUserId, ct);

        foreach (var session in sessions)
        {
            if (!rows.TryGetValue(session.OwnerUserId, out var row))
            {
                row = new PlaybackSession { OwnerUserId = session.OwnerUserId };
                db.PlaybackSessions.Add(row);
                rows[session.OwnerUserId] = row;
            }
            Apply(session, row);
        }

        await db.SaveChangesAsync(ct);
    }

    private static void Apply(PlaybackSessionRecord source, PlaybackSession row)
    {
        row.SongId = source.SongId;
        row.QueueJson = JsonSerializer.Serialize(source.Queue);
        row.QueueIndex = source.QueueIndex;
        row.PositionMs = source.PositionMs;
        row.DurationMs = source.DurationMs;
        row.IsPlaying = source.IsPlaying;
        row.PlaybackRate = source.PlaybackRate;
        row.RadioSeedId = source.RadioSeedId;
        row.Shuffle = source.Shuffle;
        row.Title = PlaybackRules.Cap(source.Title);
        row.Artist = PlaybackRules.Cap(source.Artist);
        row.Album = PlaybackRules.Cap(source.Album);
        row.ActiveDeviceId = source.ActiveDeviceId;
        row.ActiveDeviceName = PlaybackRules.Cap(source.ActiveDeviceName, PlaybackRules.MaxDeviceNameLength);
        row.Version = source.Version;
        row.UpdatedAtUtc = source.UpdatedAtUtc;
    }

    private static PlaybackSessionRecord ToRecord(PlaybackSession row)
    {
        // A queue that no longer parses, or an index outside it, degrades to "just this song"
        // rather than losing the remembered session altogether.
        var queue = ParseQueue(row.QueueJson);
        var index = row.QueueIndex;
        if (index < 0 || index >= queue.Length)
        {
            queue = [row.SongId];
            index = 0;
        }

        return new PlaybackSessionRecord(
            row.OwnerUserId,
            row.SongId,
            queue,
            index,
            row.PositionMs,
            row.DurationMs,
            row.IsPlaying,
            PlaybackRules.NormalizeRate(row.PlaybackRate),
            row.RadioSeedId,
            row.Shuffle,
            row.Title,
            row.Artist,
            row.Album,
            row.ActiveDeviceId,
            row.ActiveDeviceName,
            row.Version,
            DateTime.SpecifyKind(row.UpdatedAtUtc, DateTimeKind.Utc));
    }

    private static int[] ParseQueue(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<int[]>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}

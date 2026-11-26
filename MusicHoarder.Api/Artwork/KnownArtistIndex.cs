using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Artwork;

/// <summary>
/// The set of artist names that exist in some library (any tenant — the portrait cache is
/// catalog data with no tenant scope) or in someone's follow list, keyed by
/// <see cref="TitleNormalizer.NormalizeForDedup"/>. Held in memory and reloaded periodically by
/// <see cref="ArtistImagePrefetchBackgroundService"/> so the HTTP request path can decide "is this
/// name worth a provider lookup" without scanning <c>Songs</c> per request.
/// </summary>
public interface IKnownArtistIndex
{
    /// <summary>True when the dedup-normalized name belongs to a known artist.</summary>
    bool IsKnown(string normalizedName);

    /// <summary>A display spelling for a known normalized name (for enqueueing a refresh), if any.</summary>
    string? DisplayNameFor(string normalizedName);

    /// <summary>All known normalized → display-name pairs (the background sweep iterates these).</summary>
    IReadOnlyDictionary<string, string> All { get; }

    /// <summary>(Re)loads the index from the database. Cheap: three DISTINCT reads plus follows.</summary>
    Task RefreshAsync(MusicHoarderDbContext db, CancellationToken ct);

    /// <summary>
    /// Makes one name known now, ahead of the next <see cref="RefreshAsync"/> — for a follow, so its
    /// portrait can be fetched at once instead of after the next sweep.
    /// </summary>
    void Add(string displayName);
}

public sealed class KnownArtistIndex : IKnownArtistIndex
{
    private ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);

    public bool IsKnown(string normalizedName) => _names.ContainsKey(normalizedName);

    public string? DisplayNameFor(string normalizedName) =>
        _names.TryGetValue(normalizedName, out var display) ? display : null;

    public IReadOnlyDictionary<string, string> All => _names;

    public void Add(string displayName) => AddTo(_names, displayName);

    private static void AddTo(ConcurrentDictionary<string, string> names, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return;
        var normalized = TitleNormalizer.NormalizeForDedup(raw);
        if (normalized.Length == 0) return;
        names.TryAdd(normalized, raw.Trim());
    }

    public async Task RefreshAsync(MusicHoarderDbContext db, CancellationToken ct)
    {
        // Read unfiltered (any tenant's artist may be requested), one DISTINCT column at a time so
        // the database folds a library's thousands of rows into its few hundred names, and normalize
        // each exactly the way the cache row keys do.
        var songs = db.Songs.IgnoreQueryFilters().AsNoTracking().Where(s => s.DeletedAtUtc == null);
        var raw = new List<string?>();
        raw.AddRange(await songs.Select(s => s.Artist).Distinct().ToListAsync(ct));
        raw.AddRange(await songs.Select(s => s.AlbumArtist).Distinct().ToListAsync(ct));
        var credits = await songs.Where(s => s.Artists != null && s.Artists != "")
            .Select(s => s.Artists).Distinct().ToListAsync(ct);
        foreach (var credit in credits)
            raw.AddRange(credit!.Split(';'));
        raw.AddRange(await db.FollowedArtists.IgnoreQueryFilters().AsNoTracking()
            .Select(f => f.Name)
            .ToListAsync(ct));

        var next = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in raw)
            AddTo(next, name);

        _names = next;
    }
}

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
}

public sealed class KnownArtistIndex : IKnownArtistIndex
{
    private ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);

    public bool IsKnown(string normalizedName) => _names.ContainsKey(normalizedName);

    public string? DisplayNameFor(string normalizedName) =>
        _names.TryGetValue(normalizedName, out var display) ? display : null;

    public IReadOnlyDictionary<string, string> All => _names;

    public async Task RefreshAsync(MusicHoarderDbContext db, CancellationToken ct)
    {
        // The whole point is a fixed, small set of names — read them unfiltered (any tenant's
        // artist may be requested) and normalize each exactly the way the cache row keys do.
        var artists = await db.Songs.IgnoreQueryFilters().AsNoTracking()
            .Where(s => s.DeletedAtUtc == null)
            .Select(s => new { s.Artist, s.AlbumArtist, s.Artists })
            .ToListAsync(ct);

        var next = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        void Add(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return;
            var normalized = TitleNormalizer.NormalizeForDedup(raw);
            if (normalized.Length == 0) return;
            next.TryAdd(normalized, raw.Trim());
        }

        foreach (var row in artists)
        {
            Add(row.Artist);
            Add(row.AlbumArtist);
            if (!string.IsNullOrEmpty(row.Artists))
            {
                foreach (var part in row.Artists.Split(';'))
                    Add(part);
            }
        }

        var followed = await db.FollowedArtists.IgnoreQueryFilters().AsNoTracking()
            .Select(f => f.Name)
            .ToListAsync(ct);
        foreach (var name in followed)
            Add(name);

        _names = next;
    }
}

using FuzzySharp;
using Microsoft.EntityFrameworkCore;
using MusicHoarder.Api.Auth;
using MusicHoarder.Api.Matching;
using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Spotify;

public partial class SpotifyLibraryComparisonService(
    ISpotifyApiService spotifyApi,
    IServiceScopeFactory scopeFactory,
    IOwnerLookupService ownerLookup,
    ILogger<SpotifyLibraryComparisonService> logger) : ISpotifyLibraryComparisonService
{
    private const double FuzzyThreshold = 85.0;
    public const string SourceLikedSync = "liked_sync";
    public const string SourceApiPage = "api_page";

    public async Task<IReadOnlyDictionary<string, SpotifyLibraryMatchInfo>> UpsertMatchesForTracksAsync(
        IReadOnlyList<SpotifyTrackItem> tracks,
        string source,
        CancellationToken ct = default)
    {
        if (tracks.Count == 0)
            return new Dictionary<string, SpotifyLibraryMatchInfo>(StringComparer.OrdinalIgnoreCase);

        var index = await LoadTrackIndexAsync(ct);
        var now = DateTime.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var ids = tracks
            .Select(t => t.SpotifyId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ownerId = ownerLookup.OwnerUserId;
        var existingRows = await db.SpotifyTrackLibraryMatches
            .IgnoreQueryFilters()
            .Where(m => m.OwnerUserId == ownerId && ids.Contains(m.SpotifyTrackId))
            .ToListAsync(ct);
        var existingById = existingRows.ToDictionary(r => r.SpotifyTrackId, StringComparer.OrdinalIgnoreCase);

        var dict = new Dictionary<string, SpotifyLibraryMatchInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var song in tracks.DistinctBy(t => t.SpotifyId, StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(song.SpotifyId))
                continue;

            var (status, matched, confidence) = FindBestMatch(song, index);
            var row = ToPersistedRow(song, status, matched, confidence, source, now);
            if (existingById.TryGetValue(song.SpotifyId, out var existing))
                CopyMatchOntoExisting(existing, row);
            else
            {
                db.SpotifyTrackLibraryMatches.Add(row);
                existingById[song.SpotifyId] = row;
            }

            dict[song.SpotifyId] = ToApiInfo(status, matched, confidence);
        }

        await db.SaveChangesAsync(ct);
        return dict;
    }

    public async Task<IReadOnlyList<SpotifyTrackItem>> AttachLibraryMatchesAsync(
        IReadOnlyList<SpotifyTrackItem> tracks,
        CancellationToken ct = default)
    {
        if (tracks.Count == 0)
            return tracks;

        var ids = tracks
            .Select(t => t.SpotifyId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var ownerId = ownerLookup.OwnerUserId;
        var cached = await db.SpotifyTrackLibraryMatches
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.OwnerUserId == ownerId && ids.Contains(m.SpotifyTrackId))
            .ToListAsync(ct);

        var byId = cached.ToDictionary(c => c.SpotifyTrackId, StringComparer.OrdinalIgnoreCase);
        var missingTracks = tracks
            .Where(t => !string.IsNullOrWhiteSpace(t.SpotifyId) && !byId.ContainsKey(t.SpotifyId))
            .DistinctBy(t => t.SpotifyId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        IReadOnlyDictionary<string, SpotifyLibraryMatchInfo>? computed = null;
        if (missingTracks.Count > 0)
            computed = await UpsertMatchesForTracksAsync(missingTracks, SourceApiPage, ct);

        // Which of these tracks the owner has already wishlisted — independent of library match
        // (a NotInLibrary track can still be on the wishlist), so the overview can flag both states.
        var wishlisted = await LoadWishlistedIdsAsync(db, ids, ct);

        return tracks.Select(t =>
        {
            if (string.IsNullOrWhiteSpace(t.SpotifyId))
                return t;
            var inWishlist = wishlisted.Contains(t.SpotifyId);
            if (byId.TryGetValue(t.SpotifyId, out var row))
                return t with { LibraryMatch = RowToMatchInfo(row), IsInWishlist = inWishlist };
            if (computed?.TryGetValue(t.SpotifyId, out var info) == true)
                return t with { LibraryMatch = info, IsInWishlist = inWishlist };
            return t with { IsInWishlist = inWishlist };
        }).ToList();
    }

    /// <summary>
    /// Returns the subset of <paramref name="spotifyIds"/> that are on the owner's wishlist (any source/status).
    /// </summary>
    private async Task<HashSet<string>> LoadWishlistedIdsAsync(
        MusicHoarderDbContext db, IReadOnlyList<string> spotifyIds, CancellationToken ct)
    {
        if (spotifyIds.Count == 0)
            return new HashSet<string>(StringComparer.Ordinal);

        var ownerId = ownerLookup.OwnerUserId;
        var ids = await db.WishlistItems
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.OwnerUserId == ownerId && spotifyIds.Contains(w.SpotifyTrackId))
            .Select(w => w.SpotifyTrackId)
            .ToListAsync(ct);
        return new HashSet<string>(ids, StringComparer.Ordinal);
    }

    private static SpotifyLibraryMatchInfo RowToMatchInfo(SpotifyTrackLibraryMatch row) =>
        new(
            ((ComparisonMatchStatus)row.MatchStatus).ToString(),
            row.MatchedSongId,
            row.MatchConfidence,
            row.MatchedTitle,
            row.MatchedArtist,
            row.MatchedEnrichmentStatus);

    public async Task SyncLikedSongsMatchesAsync(CancellationToken ct = default)
    {
        var index = await LoadTrackIndexAsync(ct);
        var now = DateTime.UtcNow;
        int total = 0, inLibrary = 0, possible = 0, notIn = 0;
        const int batchSize = 50;
        var spotifyOffset = 0;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        while (true)
        {
            var page = await spotifyApi.GetLikedSongsAsync(spotifyOffset, batchSize, ct);
            if (page.Items.Count == 0)
                break;

            total = page.Total;

            var pageIds = page.Items
                .Select(s => s.SpotifyId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var ownerId = ownerLookup.OwnerUserId;
            var existingRows = await db.SpotifyTrackLibraryMatches
                .IgnoreQueryFilters()
                .Where(m => m.OwnerUserId == ownerId && pageIds.Contains(m.SpotifyTrackId))
                .ToListAsync(ct);
            var existingById = existingRows.ToDictionary(r => r.SpotifyTrackId, StringComparer.OrdinalIgnoreCase);

            foreach (var song in page.Items)
            {
                if (string.IsNullOrWhiteSpace(song.SpotifyId))
                    continue;

                var (status, matched, confidence) = FindBestMatch(song, index);
                switch (status)
                {
                    case ComparisonMatchStatus.InLibrary: inLibrary++; break;
                    case ComparisonMatchStatus.PossibleMatch: possible++; break;
                    case ComparisonMatchStatus.NotInLibrary: notIn++; break;
                }

                var row = ToPersistedRow(song, status, matched, confidence, SourceLikedSync, now);
                if (existingById.TryGetValue(song.SpotifyId, out var existing))
                    CopyMatchOntoExisting(existing, row);
                else
                {
                    db.SpotifyTrackLibraryMatches.Add(row);
                    existingById[song.SpotifyId] = row;
                }
            }

            await db.SaveChangesAsync(ct);

            spotifyOffset += page.Items.Count;
            if (spotifyOffset >= page.Total)
                break;
        }

        var ownerIdSync = ownerLookup.OwnerUserId;
        var settings = await db.SpotifySettings
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerIdSync, ct);
        if (settings is not null)
        {
            settings.SpotifyLikedMatchTotal = total;
            settings.SpotifyLikedMatchInLibrary = inLibrary;
            settings.SpotifyLikedMatchPossible = possible;
            settings.SpotifyLikedMatchNotInLibrary = notIn;
            settings.SpotifyLikedMatchStatsUpdatedAtUtc = now;
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Spotify liked library match sync finished: total={Total}, inLibrary={InLib}, possible={Possible}, notInLibrary={NotIn}",
            total, inLibrary, possible, notIn);
    }

    public async Task<SpotifyComparisonResponse> CompareAsync(
        int offset = 0,
        int limit = 50,
        ComparisonMatchStatus? matchStatus = null,
        CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 50);
        offset = Math.Max(0, offset);

        if (matchStatus is null)
        {
            var likedSongs = await spotifyApi.GetLikedSongsAsync(offset, limit, ct);
            var withMatch = await AttachLibraryMatchesAsync(likedSongs.Items, ct);
            var items = withMatch.Select(ToComparisonItemFromTrack).ToList();
            return new SpotifyComparisonResponse(likedSongs.Total, likedSongs.Offset, likedSongs.Limit, items);
        }

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var statusInt = (int)matchStatus.Value;
        var ownerIdCmp = ownerLookup.OwnerUserId;
        var baseQuery = db.SpotifyTrackLibraryMatches
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(m => m.OwnerUserId == ownerIdCmp && m.MatchStatus == statusInt);

        var totalFiltered = await baseQuery.CountAsync(ct);
        var rows = await baseQuery
            .OrderByDescending(m => m.SpotifyAddedAtUtc ?? m.UpdatedAtUtc)
            .ThenBy(m => m.SpotifyTrackId)
            .Skip(offset)
            .Take(limit)
            .ToListAsync(ct);

        var wishlisted = await LoadWishlistedIdsAsync(db, rows.Select(r => r.SpotifyTrackId).ToList(), ct);
        var itemsFromDb = rows.Select(r => ToComparisonItemFromRow(r, wishlisted.Contains(r.SpotifyTrackId))).ToList();
        return new SpotifyComparisonResponse(totalFiltered, offset, limit, itemsFromDb);
    }

    public async Task<SpotifyComparisonSummaryResponse> GetSummaryAsync(CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();
        var ownerId = ownerLookup.OwnerUserId;
        var settings = await db.SpotifySettings
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.OwnerUserId == ownerId, ct);

        if (settings?.SpotifyLikedMatchTotal is int t &&
            settings.SpotifyLikedMatchInLibrary is int il &&
            settings.SpotifyLikedMatchPossible is int pm &&
            settings.SpotifyLikedMatchNotInLibrary is int ni)
        {
            return new SpotifyComparisonSummaryResponse(t, il, pm, ni);
        }

        // No background sync yet — avoid scanning Spotify on summary; return zeros until sync runs.
        return new SpotifyComparisonSummaryResponse(0, 0, 0, 0);
    }

    private static SpotifyComparisonItem ToComparisonItemFromTrack(SpotifyTrackItem t)
    {
        var info = t.LibraryMatch;
        var status = ParseStatus(info?.MatchStatus) ?? ComparisonMatchStatus.NotInLibrary;
        ComparisonMatchedTrack? mt = null;
        double? conf = info?.MatchConfidence;
        if (info?.MatchedSongId is int id && id > 0)
        {
            mt = new ComparisonMatchedTrack(
                id,
                info.MatchedTitle,
                info.MatchedArtist,
                info.MatchedEnrichmentStatus ?? "");
        }

        return new SpotifyComparisonItem(
            t.SpotifyId,
            t.Title,
            t.Artist,
            t.Album,
            t.AlbumArt,
            t.DurationMs,
            t.AddedAt,
            status,
            mt,
            conf,
            t.IsInWishlist);
    }

    private static SpotifyComparisonItem ToComparisonItemFromRow(SpotifyTrackLibraryMatch m, bool isInWishlist)
    {
        var status = (ComparisonMatchStatus)m.MatchStatus;
        ComparisonMatchedTrack? mt = null;
        if (m.MatchedSongId is int id && id > 0)
        {
            mt = new ComparisonMatchedTrack(
                id,
                m.MatchedTitle,
                m.MatchedArtist,
                m.MatchedEnrichmentStatus ?? "");
        }

        return new SpotifyComparisonItem(
            m.SpotifyTrackId,
            m.SpotifyTitle ?? "",
            m.SpotifyArtist ?? "",
            m.SpotifyAlbum ?? "",
            null,
            m.SpotifyDurationMs ?? 0,
            m.SpotifyAddedAtUtc ?? m.UpdatedAtUtc,
            status,
            mt,
            m.MatchConfidence,
            isInWishlist);
    }

    private static ComparisonMatchStatus? ParseStatus(string? s)
    {
        if (string.IsNullOrEmpty(s)) return null;
        return Enum.TryParse<ComparisonMatchStatus>(s, ignoreCase: true, out var v) ? v : null;
    }

    private static SpotifyLibraryMatchInfo ToApiInfo(
        ComparisonMatchStatus status,
        ComparisonMatchedTrack? matched,
        double? confidence)
    {
        var mt = matched;
        return new SpotifyLibraryMatchInfo(
            status.ToString(),
            mt?.Id,
            confidence,
            mt?.Title,
            mt?.Artist,
            mt?.EnrichmentStatus);
    }

    private SpotifyTrackLibraryMatch ToPersistedRow(
        SpotifyTrackItem song,
        ComparisonMatchStatus status,
        ComparisonMatchedTrack? matched,
        double? confidence,
        string source,
        DateTime now)
    {
        return new SpotifyTrackLibraryMatch
        {
            OwnerUserId = ownerLookup.OwnerUserId,
            SpotifyTrackId = song.SpotifyId,
            MatchStatus = (int)status,
            MatchedSongId = matched?.Id,
            MatchConfidence = confidence,
            MatchedTitle = matched?.Title,
            MatchedArtist = matched?.Artist,
            MatchedEnrichmentStatus = matched?.EnrichmentStatus,
            UpdatedAtUtc = now,
            Source = source,
            SpotifyTitle = song.Title,
            SpotifyArtist = song.Artist,
            SpotifyAlbum = song.Album,
            SpotifyDurationMs = song.DurationMs,
            SpotifyAddedAtUtc = song.AddedAt == DateTime.MinValue ? null : song.AddedAt,
        };
    }

    private static void CopyMatchOntoExisting(SpotifyTrackLibraryMatch target, SpotifyTrackLibraryMatch from)
    {
        target.MatchStatus = from.MatchStatus;
        target.MatchedSongId = from.MatchedSongId;
        target.MatchConfidence = from.MatchConfidence;
        target.MatchedTitle = from.MatchedTitle;
        target.MatchedArtist = from.MatchedArtist;
        target.MatchedEnrichmentStatus = from.MatchedEnrichmentStatus;
        target.UpdatedAtUtc = from.UpdatedAtUtc;
        target.Source = from.Source;
        target.SpotifyTitle = from.SpotifyTitle;
        target.SpotifyArtist = from.SpotifyArtist;
        target.SpotifyAlbum = from.SpotifyAlbum;
        target.SpotifyDurationMs = from.SpotifyDurationMs;
        target.SpotifyAddedAtUtc = from.SpotifyAddedAtUtc;
    }

    private async Task<TrackIndex> LoadTrackIndexAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MusicHoarderDbContext>();

        var tracks = await db.Songs
            .AsNoTracking()
            .Where(s => s.DeletedAtUtc == null)
            .Select(s => new TrackIndexEntry(
                s.Id,
                s.SpotifyId,
                s.Artist,
                s.Title,
                s.EnrichmentStatus))
            .ToListAsync(ct);

        logger.LogDebug("Loaded {Count} library tracks for comparison", tracks.Count);
        return new TrackIndex(tracks);
    }

    internal static (ComparisonMatchStatus Status, ComparisonMatchedTrack? Track, double? Confidence)
        FindBestMatch(SpotifyTrackItem likedSong, TrackIndex index)
    {
        if (index.BySpotifyId.TryGetValue(likedSong.SpotifyId, out var exactMatch))
        {
            return (ComparisonMatchStatus.InLibrary, ToMatchedTrack(exactMatch), 1.0);
        }

        var normalizedArtist = Normalize(likedSong.Artist);
        var normalizedTitle = Normalize(likedSong.Title);
        var key = $"{normalizedArtist}\0{normalizedTitle}";

        if (index.ByNormalizedArtistTitle.TryGetValue(key, out var normalizedMatch))
        {
            return (ComparisonMatchStatus.InLibrary, ToMatchedTrack(normalizedMatch), 0.95);
        }

        TrackIndexEntry? bestFuzzy = null;
        double bestScore = 0;

        foreach (var entry in index.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.NormalizedArtist) || string.IsNullOrWhiteSpace(entry.NormalizedTitle))
                continue;

            var artistScore = Fuzz.WeightedRatio(normalizedArtist, entry.NormalizedArtist);
            var titleScore = Fuzz.WeightedRatio(normalizedTitle, entry.NormalizedTitle);

            if (artistScore >= FuzzyThreshold && titleScore >= FuzzyThreshold)
            {
                var combinedScore = (artistScore + titleScore) / 200.0;
                if (combinedScore > bestScore)
                {
                    bestScore = combinedScore;
                    bestFuzzy = entry;
                }
            }
        }

        if (bestFuzzy is not null)
        {
            return (ComparisonMatchStatus.PossibleMatch, ToMatchedTrack(bestFuzzy), Math.Round(bestScore, 2));
        }

        return (ComparisonMatchStatus.NotInLibrary, null, null);
    }

    private static ComparisonMatchedTrack ToMatchedTrack(TrackIndexEntry entry) =>
        new(entry.Id, entry.Title, entry.Artist, entry.EnrichmentStatus.ToString());

    // Delegates to the shared normalizer so all providers + library comparison agree on
    // case/punctuation/feat./diacritic handling.
    internal static string Normalize(string? s) => TitleNormalizer.NormalizeForSearch(s);
}

internal sealed class TrackIndexEntry
{
    public int Id { get; }
    public string? SpotifyId { get; }
    public string? Artist { get; }
    public string? Title { get; }
    public EnrichmentStatus EnrichmentStatus { get; }
    public string NormalizedArtist { get; }
    public string NormalizedTitle { get; }

    public TrackIndexEntry(int id, string? spotifyId, string? artist, string? title, EnrichmentStatus enrichmentStatus)
    {
        Id = id;
        SpotifyId = spotifyId;
        Artist = artist;
        Title = title;
        EnrichmentStatus = enrichmentStatus;
        NormalizedArtist = SpotifyLibraryComparisonService.Normalize(artist);
        NormalizedTitle = SpotifyLibraryComparisonService.Normalize(title);
    }
}

internal sealed class TrackIndex
{
    public IReadOnlyList<TrackIndexEntry> Entries { get; }
    public Dictionary<string, TrackIndexEntry> BySpotifyId { get; }
    public Dictionary<string, TrackIndexEntry> ByNormalizedArtistTitle { get; }

    public TrackIndex(IReadOnlyList<TrackIndexEntry> entries)
    {
        Entries = entries;

        BySpotifyId = new Dictionary<string, TrackIndexEntry>(StringComparer.OrdinalIgnoreCase);
        ByNormalizedArtistTitle = new Dictionary<string, TrackIndexEntry>(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (!string.IsNullOrWhiteSpace(entry.SpotifyId))
                BySpotifyId.TryAdd(entry.SpotifyId, entry);

            if (!string.IsNullOrWhiteSpace(entry.NormalizedArtist) && !string.IsNullOrWhiteSpace(entry.NormalizedTitle))
            {
                var key = $"{entry.NormalizedArtist}\0{entry.NormalizedTitle}";
                ByNormalizedArtistTitle.TryAdd(key, entry);
            }
        }
    }
}

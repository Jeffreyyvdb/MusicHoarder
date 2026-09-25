using MusicHoarder.Api.Persistence;

namespace MusicHoarder.Api.Library;

/// <summary>
/// Scalar counts over the caller's active (non-deleted) songs, filled by one <c>GroupBy(_ =&gt; 1)</c>
/// aggregate round-trip. <c>null</c> when the caller has no songs.
/// </summary>
internal sealed class InsightsSongCounts
{
    public int Indexed { get; init; }
    public int Fingerprinted { get; init; }
    public int Matched { get; init; }
    public int NeedsReview { get; init; }
    public int FailedEnrich { get; init; }
    public int PendingEnrich { get; init; }
    public int LyricsFetched { get; init; }
    public int LyricsInstrumental { get; init; }
    public int LyricsNotFound { get; init; }
    public int LyricsNotFetched { get; init; }
    public int LyricsFailed { get; init; }
    public int WithMbid { get; init; }
    public int WithSpotify { get; init; }
    public int WithIsrc { get; init; }
    public int ManualApprovals { get; init; }
    public int Duplicates { get; init; }
    public int Conf90 { get; init; }
    public int Conf75 { get; init; }
    public int ConfLow { get; init; }
    public DateTime OldestIndexed { get; init; }
    public DateTime NewestIndexed { get; init; }
}

/// <summary>
/// Scalar counts over the built library (active, <see cref="LibraryBuildStatus.Done"/>, with a
/// destination path). <c>null</c> when nothing is built.
/// </summary>
internal sealed class InsightsBuiltCounts
{
    public int InLibrary { get; init; }
    public int WithCover { get; init; }
    public int WithLyrics { get; init; }
    public int DurationSeconds { get; init; }
    public long Bytes { get; init; }
}

/// <summary>
/// The Spotify Liked Songs wishlist, by outcome. "In library" means the linked downloaded song is
/// built and live in the destination; "downloaded" also counts any item with a linked song.
/// </summary>
internal sealed class InsightsLikedCounts
{
    public int Total { get; init; }
    public int InLibrary { get; init; }
    public int Downloaded { get; init; }
    public int SkippedOwned { get; init; }
    public int Downloading { get; init; }
    public int Pending { get; init; }
    public int NotFound { get; init; }
    public int Failed { get; init; }
}

/// <summary>The whole wishlist (every source, and items with none), same rules as <see cref="InsightsLikedCounts"/>.</summary>
internal sealed class InsightsWishlistCounts
{
    public int Total { get; init; }
    public int InLibrary { get; init; }
    public int Downloaded { get; init; }
}

/// <summary>Active songs per raw file extension, exactly as stored (case and leading dot vary).</summary>
internal sealed record InsightsExtensionCount(string Ext, int Count);

/// <summary>Provider attempts per enrichment provider, and how many of them matched.</summary>
internal sealed record InsightsProviderCount(EnrichmentProvider Provider, int Total, int Matched);

/// <summary>The three tag columns of one built track that the top-artists/albums views group on.</summary>
internal sealed record InsightsBuiltTrack(string? AlbumArtist, string? Artist, string? Album);

/// <summary>
/// Everything <see cref="LibraryInsightsReport.Build"/> reads: the results of the owner-scoped
/// queries in <c>DashboardEndpoints.GetInsights</c>, and nothing else.
/// </summary>
internal sealed record LibraryInsightsInputs
{
    public InsightsSongCounts? Songs { get; init; }
    public InsightsBuiltCounts? Built { get; init; }

    /// <summary>Distinct album folders MusicHoarder wrote a cover into (from the library-write audit log).</summary>
    public int AlbumCoversAdded { get; init; }

    public InsightsLikedCounts? Liked { get; init; }
    public InsightsWishlistCounts? Wishlist { get; init; }
    public int WishlistSources { get; init; }
    public IReadOnlyList<InsightsExtensionCount> SongsByExtension { get; init; } = [];
    public IReadOnlyList<InsightsProviderCount> AttemptsByProvider { get; init; } = [];
    public IReadOnlyList<InsightsBuiltTrack> BuiltTracks { get; init; } = [];
}

/// <summary>
/// Shapes the <c>GET /api/insights</c> payload — the Stats overview's "story of the hoard" — from the
/// counts the endpoint queried: percentages, the pipeline and wishlist funnels, the format and
/// provider breakdowns, and the case-insensitive top artists/albums and distinct counts over the
/// built library. Pure: no database, no clock, so every rule is testable from plain inputs.
/// <para>
/// The payload is an anonymous object on purpose: its camelCase member names, order and labels
/// <em>are</em> the wire contract the web client's <c>LibraryInsights</c> type reads.
/// </para>
/// </summary>
internal static class LibraryInsightsReport
{
    private static double Pct(double part, double whole) => whole > 0 ? Math.Round(100.0 * part / whole, 1) : 0;

    public static object Build(LibraryInsightsInputs inputs, DateTime generatedAtUtc)
    {
        var a = inputs.Songs;
        var b = inputs.Built;
        var liked = inputs.Liked;
        var wishAll = inputs.Wishlist;
        var albumCoversAdded = inputs.AlbumCoversAdded;
        var wishlistSources = inputs.WishlistSources;

        var indexed = a?.Indexed ?? 0;
        var inLibrary = b?.InLibrary ?? 0;

        // Format breakdown — normalise extensions in memory.
        var byExtRaw = inputs.SongsByExtension;
        var byFormat = byExtRaw
            .GroupBy(x => (x.Ext ?? "").TrimStart('.').ToLowerInvariant())
            .Where(g => g.Key.Length > 0)
            .Select(g => new { format = g.Key, count = g.Sum(x => x.Count) })
            .OrderByDescending(x => x.count)
            .ToList();

        // Per-provider match counts.
        var providerRaw = inputs.AttemptsByProvider;
        var byProvider = providerRaw
            .Select(p => new { provider = p.Provider.ToString(), total = p.Total, matched = p.Matched })
            .OrderByDescending(p => p.matched)
            .ThenByDescending(p => p.total)
            .ToList();

        // Top artists / albums + distinct counts over the built library, grouped in memory so
        // case-insensitive grouping is correct and provider-agnostic (no DB collation).
        var builtRows = inputs.BuiltTracks;
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
            generatedAtUtc,

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

        return response;
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
}

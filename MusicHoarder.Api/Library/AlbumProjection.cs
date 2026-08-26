using MusicHoarder.Api.Contracts;

namespace MusicHoarder.Api.Library;

/// <summary>
/// One track, reduced to the fields album grouping reads. Built from the caller's own rows, and —
/// for a library shared with them — from <see cref="Contracts.SharedSongRowDto"/>, which is what
/// keeps a grantor's withheld columns out of the grouping. See <c>AlbumsEndpoints.ListAlbums</c>.
/// </summary>
internal sealed record AlbumTrackRow(
    int Id,
    string FileName,
    string? DestinationPath,
    string? Album,
    string? AlbumArtist,
    string? Artist,
    /// <summary>Discrete credited artists, ';'-joined. Only the browse filter reads it.</summary>
    string? Artists,
    string? Title,
    int? TrackNumber,
    int? Year,
    int? DurationSeconds,
    long FileSizeBytes,
    string? Genre,
    string? Label,
    string? CatalogNumber,
    string? Upc,
    string? ReleaseDate,
    string? MusicBrainzReleaseId,
    bool HasCoverArt,
    int PlayCount,
    DateTime? LikedAtUtc,
    bool IsAlbumFill,
    bool IsBuilt,
    bool IsUnreleased,
    DateTime? SpotifyAddedAtUtc,
    DateTime? AcquiredAtUtc,
    DateTime? LibraryBuiltAtUtc,
    DateTime IndexedAtUtc);

/// <summary>
/// Groups tracks into album cards. This is the single definition of what an album <i>is</i> in this
/// app; it used to live twice, in full, in <c>frontend/src/lib/api-client.ts</c> and
/// <c>android/…/data/AlbumGrouping.kt</c>, and the two had drifted.
///
/// <para>
/// <b>Do not reach for <see cref="AlbumGroupKey"/> here.</b> That key is the pipeline's
/// <i>logical</i> album: it folds a credit to its lead artist and appends an edition qualifier, so it
/// deliberately merges "Kanye West" with "Kanye West &amp; Jay-Z" and separates "X (Deluxe)" from
/// "X". Both are right for reconciling tags on disk and wrong for the browsing grid, where the count
/// must match what the user sees in their music server. The grouping below is the browsing one:
/// destination folder first, then a fold on the raw lowercased display names.
/// </para>
/// </summary>
internal static class AlbumProjection
{
    internal const string UnknownAlbum = "Unknown Album";
    internal const string UnknownArtist = "Unknown Artist";

    /// <summary>
    /// Names are ordered the way both clients order them — JavaScript's <c>localeCompare</c> and the
    /// JVM's root <c>Collator</c>. An ordinal comparison is NOT equivalent: it files every lowercase
    /// name after every uppercase one, and "Ólafur" past "Z".
    /// </summary>
    private static readonly StringComparer Names = StringComparer.InvariantCulture;

    /// <summary>
    /// Group <paramref name="rows"/> into album cards.
    ///
    /// <para>
    /// <paramref name="rows"/> must arrive in the same order <c>GET /songs</c> returns them. Several
    /// rules read the <i>first</i> row to say something — the card's title and artist, its cover, and
    /// each of the first-non-null catalog fields — so the order is part of the contract, not an
    /// implementation detail.
    /// </para>
    /// </summary>
    /// <param name="merge">
    /// Fold cards that are the same album under different destination folders into one. What the
    /// grid wants; the song-detail panel wants the unmerged, per-folder grouping.
    /// </param>
    internal static List<AlbumSummaryDto> Build(IReadOnlyList<AlbumTrackRow> rows, bool merge)
    {
        var albums = Group(rows);
        return merge ? Merge(albums) : Sorted(albums);
    }

    /// <summary>
    /// Built tracks group by their <b>destination folder</b> — the unit the music server reads, where
    /// the library builder elects one reconciled release identity, so the app splits one album name
    /// across releases exactly the way the player does. Tracks with no destination path fall back to
    /// their name key, so unbuilt rows still group somewhere.
    /// </summary>
    private static List<Entry> Group(IReadOnlyList<AlbumTrackRow> rows)
    {
        // Insertion-ordered on purpose: the final sort is stable, so two cards that tie on artist and
        // title come out in the order their first track appeared.
        var order = new List<Entry>();
        var byKey = new Dictionary<string, Entry>(StringComparer.Ordinal);

        // Most recent added-date over ALL tracks, kept aside as the fallback for an album with none
        // of the owner's own — see AlbumSummaryDto.AddedAtUtc. Not a field on the card: nothing
        // outside this method needs it, and one meaning per field is what keeps the sorts honest.
        var anyTrackAdded = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var title = NonEmpty(row.Album) ?? UnknownAlbum;
            var artist = NonEmpty(row.AlbumArtist) ?? NonEmpty(row.Artist) ?? UnknownArtist;
            var nameKey = NameKey(artist, title);
            var key = DestinationFolderOf(row) ?? nameKey;

            if (!byKey.TryGetValue(key, out var entry))
            {
                entry = new Entry
                {
                    Key = key,
                    NameKey = nameKey,
                    Title = title,
                    Artist = artist,
                    // Seeded from the first track, positive or not, and only ever replaced by an
                    // EARLIER positive year below. A card whose every track says 0 keeps the 0.
                    Year = row.Year,
                };
                byKey[key] = entry;
                order.Add(entry);
            }

            entry.TrackCount += 1;

            var added = AddedAt(row);
            if (added is { } addedAt)
            {
                DateTime? seenAny = anyTrackAdded.TryGetValue(key, out var any) ? any : null;
                if (IsLater(addedAt, seenAny)) anyTrackAdded[key] = addedAt;
                if (IsMyMusic(row) && IsLater(addedAt, entry.AddedAtUtc)) entry.AddedAtUtc = addedAt;
            }

            entry.PlayCount += row.PlayCount;
            entry.DurationSeconds += row.DurationSeconds ?? 0;
            entry.ByteSize += row.FileSizeBytes;
            if (row.Year is > 0 && (entry.Year is null or 0 || row.Year < entry.Year)) entry.Year = row.Year;
            entry.Genre ??= NonEmpty(row.Genre);
            entry.Label ??= NonEmpty(row.Label);
            entry.CatalogNumber ??= NonEmpty(row.CatalogNumber);
            entry.Upc ??= NonEmpty(row.Upc);
            entry.ReleaseDate ??= NonEmpty(row.ReleaseDate);
            entry.MusicBrainzReleaseId ??= NonEmpty(row.MusicBrainzReleaseId);
            if (entry.CoverSongId is null && row.HasCoverArt) entry.CoverSongId = row.Id;
            entry.Tracks.Add(row);
        }

        foreach (var entry in order)
        {
            entry.Tracks = SortTracks(entry.Tracks);
            // Nothing of the owner's own in this album — completion filled one in whole, or its owned
            // tracks were soft-deleted. Date it from what is there so it still sorts.
            if (entry.AddedAtUtc is null && anyTrackAdded.TryGetValue(entry.Key, out var fallback))
                entry.AddedAtUtc = fallback;
        }

        return order;
    }

    /// <summary>
    /// Fold cards that are the same album under a different destination folder into one.
    ///
    /// <para>
    /// Grouping by folder mirrors what the music server shows, but it also means one album whose
    /// tracks disagree about the year or the artist spelling lands as two or three adjacent,
    /// near-identical cards. For <i>browsing</i> that reads as noise. The largest constituent folder
    /// becomes the representative — its key keeps existing <c>?album=</c> links working, and ties
    /// break on the key so the choice is stable across refetches — while <c>FolderKeys</c> carries
    /// all of them so a link to a folder that lost can still be resolved.
    /// </para>
    /// </summary>
    private static List<AlbumSummaryDto> Merge(List<Entry> albums)
    {
        var order = new List<List<Entry>>();
        var byName = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
        foreach (var album in albums)
        {
            // Re-derived from the card's own display names rather than reused from its first track:
            // a merged card takes the lead's names, so this has to fold on what the card shows.
            var nameKey = NameKey(album.Artist, album.Title);
            if (byName.TryGetValue(nameKey, out var group))
            {
                group.Add(album);
            }
            else
            {
                group = [album];
                byName[nameKey] = group;
                order.Add(group);
            }
        }

        var merged = new List<Entry>(order.Count);
        foreach (var group in order)
        {
            if (group.Count == 1)
            {
                merged.Add(group[0]);
                continue;
            }

            var ordered = group
                .OrderByDescending(a => a.TrackCount)
                .ThenBy(a => a.Key, Names)
                .ToList();

            var lead = ordered[0];
            var combined = lead.Clone();
            combined.FolderKeys = ordered.Select(a => a.Key).ToList();
            combined.Tracks = SortTracks(ordered.SelectMany(a => a.Tracks).ToList());

            foreach (var other in ordered.Skip(1))
            {
                combined.TrackCount += other.TrackCount;
                combined.DurationSeconds += other.DurationSeconds;
                combined.ByteSize += other.ByteSize;
                combined.PlayCount += other.PlayCount;
                if (other.Year is > 0 && (combined.Year is null or 0 || other.Year < combined.Year))
                    combined.Year = other.Year;
                combined.Genre ??= other.Genre;
                combined.Label ??= other.Label;
                combined.CatalogNumber ??= other.CatalogNumber;
                combined.Upc ??= other.Upc;
                combined.ReleaseDate ??= other.ReleaseDate;
                combined.MusicBrainzReleaseId ??= other.MusicBrainzReleaseId;
                combined.CoverSongId ??= other.CoverSongId;
                if (IsLater(other.AddedAtUtc, combined.AddedAtUtc)) combined.AddedAtUtc = other.AddedAtUtc;
            }

            merged.Add(combined);
        }

        return Sorted(merged);
    }

    /// <summary>
    /// Artist, then title. Every album order falls back to this, so cards that tie on the sort the
    /// user picked keep a stable alphabetical order rather than the arbitrary one grouping produced.
    /// <c>OrderBy</c> is stable; <c>List.Sort</c> is not, and the difference is observable.
    /// </summary>
    private static List<AlbumSummaryDto> Sorted(List<Entry> albums) => albums
        .OrderBy(a => a.Artist, Names)
        .ThenBy(a => a.Title, Names)
        .Select(a => a.ToDto())
        .ToList();

    /// <summary>
    /// Track number first (unnumbered tracks last), then title — falling back to the file name, which
    /// is the only name an unmatched song has. The fallback is on null alone, deliberately: a row
    /// tagged with an empty title sorts as empty, which is what the clients do.
    /// </summary>
    private static List<AlbumTrackRow> SortTracks(List<AlbumTrackRow> tracks) => tracks
        .OrderBy(t => t.TrackNumber ?? int.MaxValue)
        .ThenBy(t => (t.Title ?? t.FileName).ToLowerInvariant(), Names)
        .ToList();

    /// <summary>
    /// Whether this track is the owner's own — they asked for it, rather than album completion adding
    /// it because they already owned another track from the record. A like promotes a filled track:
    /// hearting one is the deliberate act that says "keep this". Plays deliberately do not.
    /// </summary>
    private static bool IsMyMusic(AlbumTrackRow row) => !row.IsAlbumFill || row.LikedAtUtc is not null;

    /// <summary>
    /// When a song entered the collection.
    ///
    /// <para>
    /// Rows predating <c>AcquiredAtUtc</c> fall back to the OLDEST of the two churn-prone stamps
    /// rather than preferring either: a re-index bumps <c>IndexedAtUtc</c> while a rebuild clears and
    /// re-sets <c>LibraryBuiltAtUtc</c>, so whichever survived un-bumped is the closer guess. An
    /// EARLIER Spotify save date beats all of them — for a wishlist download the acquisition date is
    /// when the downloader got round to it, so a years-old save would otherwise drip in with today's
    /// stamp and land at the top of "recently added". It only ever pulls the date backwards, so a
    /// track ripped years before it was saved on Spotify keeps its acquisition date.
    /// </para>
    /// </summary>
    internal static DateTime? AddedAt(AlbumTrackRow row) =>
        Oldest(row.SpotifyAddedAtUtc, row.AcquiredAtUtc ?? Oldest(row.LibraryBuiltAtUtc, row.IndexedAtUtc));

    /// <summary>
    /// The album folder of a built song. Deliberately a manual split on '/' rather than
    /// <see cref="Path.GetDirectoryName"/>: destination paths are POSIX and stored verbatim, and this
    /// has to agree byte-for-byte with the clients — including their quirk that a path whose only
    /// slash is at index 0 returns the whole path rather than an empty folder.
    /// </summary>
    private static string? DestinationFolderOf(AlbumTrackRow row)
    {
        var path = NonEmpty(row.DestinationPath);
        if (path is null) return null;
        var idx = path.LastIndexOf('/');
        return idx > 0 ? path[..idx] : path;
    }

    private static string NameKey(string artist, string title) =>
        $"{artist.ToLowerInvariant()}::{title.ToLowerInvariant()}";

    private static string? NonEmpty(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length > 0 ? trimmed : null;
    }

    private static bool IsLater(DateTime? candidate, DateTime? current) =>
        candidate is not null && (current is null || candidate > current);

    private static DateTime? Oldest(DateTime? a, DateTime? b) =>
        a is null ? b : b is null ? a : a < b ? a : b;

    /// <summary>Mutable accumulator. The DTO is immutable; building one field-by-field is not.</summary>
    private sealed class Entry
    {
        public required string Key { get; init; }
        public required string NameKey { get; init; }
        public List<string>? FolderKeys { get; set; }
        public required string Title { get; init; }
        public required string Artist { get; init; }
        public int? Year { get; set; }
        public int TrackCount { get; set; }
        public int DurationSeconds { get; set; }
        public long ByteSize { get; set; }
        public string? Genre { get; set; }
        public string? Label { get; set; }
        public string? CatalogNumber { get; set; }
        public string? Upc { get; set; }
        public string? ReleaseDate { get; set; }
        public string? MusicBrainzReleaseId { get; set; }
        public int? CoverSongId { get; set; }
        public DateTime? AddedAtUtc { get; set; }
        public int PlayCount { get; set; }
        public List<AlbumTrackRow> Tracks { get; set; } = [];

        public Entry Clone() => (Entry)MemberwiseClone();

        public AlbumSummaryDto ToDto() => new()
        {
            Key = Key,
            FolderKeys = FolderKeys ?? [Key],
            NameKey = NameKey,
            Title = Title,
            Artist = Artist,
            Year = Year,
            TrackCount = TrackCount,
            DurationSeconds = DurationSeconds,
            ByteSize = ByteSize,
            Genre = Genre,
            Label = Label,
            CatalogNumber = CatalogNumber,
            Upc = Upc,
            ReleaseDate = ReleaseDate,
            MusicBrainzReleaseId = MusicBrainzReleaseId,
            CoverSongId = CoverSongId,
            AddedAtUtc = AddedAtUtc,
            PlayCount = PlayCount,
            TrackIds = Tracks.Select(t => t.Id).ToList(),
        };
    }
}

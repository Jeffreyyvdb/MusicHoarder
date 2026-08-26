using MusicHoarder.Api.Contracts;
using MusicHoarder.Api.Library;

namespace MusicHoarder.Api.Tests.Library;

/// <summary>
/// The album grouping rules, pinned by the cases that used to pin them in each client:
/// <c>frontend/src/lib/album-grouping.test.ts</c> and
/// <c>android/…/data/AlbumGroupingTest.kt</c>. Both had their own copy of the implementation and
/// their own copy of these tests, and the two had drifted; this is the one that decides now.
///
/// <para>
/// Album <i>ordering</i> is deliberately not here. The clients still sort — the server settles what
/// an album is, they settle how the grid is arranged — so <c>sortAlbums</c> stays pinned in their
/// suites. What is here is the canonical artist-then-title order every one of those sorts falls
/// back to.
/// </para>
/// </summary>
public class AlbumProjectionTests
{
    // ── buildAlbumsFromSongs / buildAlbums ────────────────────────────────────

    [Fact]
    public void Splits_one_album_name_across_destination_folders()
    {
        // Same artist + album name, but two different release folders (Navidrome shows two albums).
        var albums = Group(
            Song(id: 1, trackNumber: 1, destinationPath: "/dest/Kanye West/2010 - MBDTF/01 - Dark Fantasy.flac"),
            Song(id: 2, trackNumber: 2, destinationPath: "/dest/Kanye West/2010 - MBDTF/02 - Gorgeous.flac"),
            Song(id: 3, trackNumber: 1, destinationPath: "/dest/Kanye West/2013 - MBDTF/01 - Mama's Boy.flac"));

        Assert.Equal(2, albums.Count);
        Assert.Equal([1, 2], albums.Select(a => a.TrackCount).Order());
        // Keys are the destination folder directories, not the (shared) album name.
        Assert.Equal(2, albums.Select(a => a.Key).Distinct().Count());
    }

    [Fact]
    public void Keeps_a_multi_disc_album_in_one_folder_as_one_card()
    {
        var albums = Group(
            Song(id: 1, trackNumber: 1, destinationPath: "/dest/A/2000 - X/1-01 - a.flac"),
            Song(id: 2, trackNumber: 1, destinationPath: "/dest/A/2000 - X/2-01 - b.flac"));

        Assert.Single(albums);
        Assert.Equal(2, albums[0].TrackCount);
    }

    [Fact]
    public void Falls_back_to_the_name_key_when_songs_are_not_built()
    {
        var albums = Group(Song(id: 1), Song(id: 2));

        Assert.Single(albums);
        Assert.Equal("kanye west::my beautiful dark twisted fantasy", albums[0].Key);
        Assert.Equal(albums[0].Key, albums[0].NameKey);
    }

    [Fact]
    public void A_path_whose_only_slash_is_the_root_keeps_the_whole_path()
    {
        // Matches the clients' `idx > 0` guard: the alternative is an empty folder key, which would
        // collapse every such row into one card.
        var albums = Group(Song(id: 1, destinationPath: "/loose.flac"));

        Assert.Equal("/loose.flac", albums[0].Key);
    }

    // ── mergeAlbumsByName ─────────────────────────────────────────────────────

    [Fact]
    public void Folds_one_album_name_split_across_folders_into_a_single_card()
    {
        var merged = Merge(
            Song(id: 1, trackNumber: 2, destinationPath: "/dest/Kanye West/2010 - MBDTF/02 - Gorgeous.flac"),
            Song(id: 2, trackNumber: 1, destinationPath: "/dest/Kanye West/2010 - MBDTF/01 - Dark Fantasy.flac"),
            Song(id: 3, trackNumber: 1, destinationPath: "/dest/Kanye West/2013 - MBDTF/01 - Mama's Boy.flac"));

        var album = Assert.Single(merged);
        Assert.Equal(3, album.TrackCount);
        // The biggest folder wins the representative key, so existing ?album= links still resolve.
        Assert.Equal("/dest/Kanye West/2010 - MBDTF", album.Key);
        // ...and the folder that lost is still resolvable through folderKeys.
        Assert.Equal(
            ["/dest/Kanye West/2010 - MBDTF", "/dest/Kanye West/2013 - MBDTF"],
            album.FolderKeys);
        // Re-sorted by track number. Songs 2 and 3 tie on number AND on title, so the flatMap order
        // decides — which only holds under a STABLE sort.
        Assert.Equal([2, 3, 1], album.TrackIds);
    }

    [Fact]
    public void Leaves_distinct_albums_alone()
    {
        var merged = Merge(
            Song(id: 1, album: "Graduation", destinationPath: "/dest/Kanye West/2007 - Graduation/01.flac"),
            Song(id: 2, album: "808s", destinationPath: "/dest/Kanye West/2008 - 808s/01.flac"));

        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void A_tie_is_broken_on_the_key_so_the_choice_is_stable_across_refetches()
    {
        var a = Song(id: 1, album: "Ye", destinationPath: "/dest/Kanye West/Ye A/01.flac");
        var b = Song(id: 2, album: "Ye", destinationPath: "/dest/Kanye West/Ye B/01.flac");

        Assert.Equal("/dest/Kanye West/Ye A", Merge(a, b).Single().Key);
        Assert.Equal("/dest/Kanye West/Ye A", Merge(b, a).Single().Key);
    }

    [Fact]
    public void The_album_year_is_the_earliest_its_tracks_agree_on()
    {
        // A deluxe re-issue's tracks carry the reissue year; the album is still the year it came out.
        var merged = Merge(
            Song(id: 1, album: "Ye", year: 2018, destinationPath: "/dest/Kanye West/2018 - Ye/01.flac"),
            Song(id: 2, album: "Ye", year: 2021, destinationPath: "/dest/Kanye West/2021 - Ye/01.flac"));

        Assert.Equal(2018, merged.Single().Year);
    }

    [Fact]
    public void A_year_of_zero_is_kept_rather_than_folded_to_null()
    {
        // The web's behaviour, and the one that decides: only a POSITIVE year can replace the seed,
        // so an album whose every track says 0 reports 0 rather than "no year".
        Assert.Equal(0, Group(Song(id: 1, year: 0)).Single().Year);
        // ...and a real year still wins over it.
        Assert.Equal(1999, Group(Song(id: 1, year: 0), Song(id: 2, year: 1999)).Single().Year);
    }

    // ── added date, and album fill (PR #453) ──────────────────────────────────

    [Fact]
    public void The_added_date_prefers_the_immutable_acquisition_stamp()
    {
        var added = AlbumProjection.AddedAt(Song(
            acquiredAtUtc: Utc(2020, 1, 1),
            indexedAtUtc: Utc(2026, 7, 1),
            libraryBuiltAtUtc: Utc(2026, 7, 2)));

        Assert.Equal(Utc(2020, 1, 1), added);
    }

    [Fact]
    public void The_added_date_falls_back_to_the_oldest_churn_prone_stamp()
    {
        // A re-tag or re-index would have bumped only one of the two; the older is the better guess.
        var added = AlbumProjection.AddedAt(Song(
            indexedAtUtc: Utc(2026, 7, 1), libraryBuiltAtUtc: Utc(2022, 5, 5)));

        Assert.Equal(Utc(2022, 5, 5), added);
    }

    [Fact]
    public void An_earlier_spotify_save_date_beats_the_download_date()
    {
        // A years-old liked song the wishlist downloader only got round to fetching today: without
        // this it sits at the top of "recently added" next to things actually just acquired.
        var added = AlbumProjection.AddedAt(Song(
            acquiredAtUtc: Utc(2026, 7, 26), spotifyAddedAtUtc: Utc(2023, 3, 21)));

        Assert.Equal(Utc(2023, 3, 21), added);
    }

    [Fact]
    public void A_later_spotify_save_date_does_not_push_the_acquisition_date_forward()
    {
        // Ripped in 2019, saved on Spotify in 2024.
        var added = AlbumProjection.AddedAt(Song(
            acquiredAtUtc: Utc(2019, 1, 1), spotifyAddedAtUtc: Utc(2024, 6, 1)));

        Assert.Equal(Utc(2019, 1, 1), added);
    }

    [Fact]
    public void An_album_fill_track_does_not_re_date_an_album_you_already_owned()
    {
        var album = Group(
            Song(id: 1, acquiredAtUtc: Utc(2019, 1, 1)),
            Song(id: 2, acquiredAtUtc: Utc(2026, 8, 1), isAlbumFill: true)).Single();

        Assert.Equal(Utc(2019, 1, 1), album.AddedAtUtc);
    }

    [Fact]
    public void A_liked_album_fill_track_counts_because_liking_it_made_it_yours()
    {
        var album = Group(
            Song(id: 1, acquiredAtUtc: Utc(2019, 1, 1)),
            Song(id: 2, acquiredAtUtc: Utc(2026, 8, 1), isAlbumFill: true, likedAtUtc: Utc(2026, 8, 2)))
            .Single();

        Assert.Equal(Utc(2026, 8, 1), album.AddedAtUtc);
    }

    [Fact]
    public void An_album_that_is_nothing_but_fill_still_carries_a_date()
    {
        // Otherwise a wholly-filled record sorts last forever on null.
        var album = Group(
            Song(id: 1, acquiredAtUtc: Utc(2026, 8, 1), isAlbumFill: true),
            Song(id: 2, acquiredAtUtc: Utc(2026, 8, 3), isAlbumFill: true)).Single();

        Assert.Equal(Utc(2026, 8, 3), album.AddedAtUtc);
    }

    [Fact]
    public void A_merged_card_takes_the_latest_added_date_of_its_folders()
    {
        var merged = Merge(
            Song(id: 1, album: "Ye", acquiredAtUtc: Utc(2019, 1, 1), destinationPath: "/dest/K/A/01.flac"),
            Song(id: 2, album: "Ye", acquiredAtUtc: Utc(2024, 1, 1), destinationPath: "/dest/K/B/01.flac"));

        Assert.Equal(Utc(2024, 1, 1), merged.Single().AddedAtUtc);
    }

    // ── aggregates the clients never tested ───────────────────────────────────

    [Fact]
    public void Numeric_fields_are_summed_across_the_album()
    {
        var album = Group(
            Song(id: 1, durationSeconds: 100, fileSizeBytes: 1_000, playCount: 2),
            Song(id: 2, durationSeconds: 250, fileSizeBytes: 2_500, playCount: 3),
            Song(id: 3, durationSeconds: null, fileSizeBytes: 500, playCount: 0)).Single();

        Assert.Equal(3, album.TrackCount);
        Assert.Equal(350, album.DurationSeconds);
        Assert.Equal(4_000, album.ByteSize);
        Assert.Equal(5, album.PlayCount);
    }

    [Fact]
    public void Catalog_fields_take_the_first_row_that_has_one()
    {
        // Per-song enrichment means one track often knows the label and its siblings do not.
        var album = Group(
            Song(id: 1),
            Song(id: 2, genre: "Hip Hop", label: "Roc-A-Fella", catalogNumber: "B0014695-02",
                upc: "602527549293", releaseDate: "2010-11-22", musicBrainzReleaseId: "mbid-1"),
            Song(id: 3, genre: "Rap", label: "Def Jam", musicBrainzReleaseId: "mbid-2")).Single();

        Assert.Equal("Hip Hop", album.Genre);
        Assert.Equal("Roc-A-Fella", album.Label);
        Assert.Equal("B0014695-02", album.CatalogNumber);
        Assert.Equal("602527549293", album.Upc);
        Assert.Equal("2010-11-22", album.ReleaseDate);
        Assert.Equal("mbid-1", album.MusicBrainzReleaseId);
    }

    [Fact]
    public void Blank_catalog_fields_do_not_win_over_a_later_real_one()
    {
        var album = Group(
            Song(id: 1, label: "   "),
            Song(id: 2, label: "Def Jam")).Single();

        Assert.Equal("Def Jam", album.Label);
    }

    [Fact]
    public void The_cover_is_the_first_track_that_has_one()
    {
        var album = Group(
            Song(id: 1, hasCoverArt: false),
            Song(id: 2, hasCoverArt: true),
            Song(id: 3, hasCoverArt: true)).Single();

        Assert.Equal(2, album.CoverSongId);
    }

    [Fact]
    public void An_album_with_no_artwork_reports_no_cover()
    {
        Assert.Null(Group(Song(id: 1)).Single().CoverSongId);
    }

    [Fact]
    public void A_song_with_no_tags_falls_back_to_unknown_artist_and_album()
    {
        var album = Group(Song(id: 1, artist: null, albumArtist: null, album: null)).Single();

        Assert.Equal("Unknown Album", album.Title);
        Assert.Equal("Unknown Artist", album.Artist);
        Assert.Equal("unknown artist::unknown album", album.Key);
    }

    [Fact]
    public void The_album_artist_is_preferred_over_the_track_artist()
    {
        var album = Group(Song(id: 1, artist: "Jay-Z", albumArtist: "Kanye West")).Single();

        Assert.Equal("Kanye West", album.Artist);
    }

    // ── canonical order ───────────────────────────────────────────────────────

    [Fact]
    public void Albums_come_back_ordered_by_artist_then_title()
    {
        var albums = Merge(
            Song(id: 1, artist: "Zappa", albumArtist: "Zappa", album: "Hot Rats",
                destinationPath: "/dest/Zappa/1969 - Hot Rats/01.flac"),
            Song(id: 2, artist: "Aphex Twin", albumArtist: "Aphex Twin", album: "Drukqs",
                destinationPath: "/dest/Aphex Twin/2001 - Drukqs/01.flac"),
            Song(id: 3, artist: "Aphex Twin", albumArtist: "Aphex Twin", album: "Ambient Works",
                destinationPath: "/dest/Aphex Twin/1992 - Ambient Works/01.flac"));

        Assert.Equal(["Ambient Works", "Drukqs", "Hot Rats"], albums.Select(a => a.Title));
    }

    [Fact]
    public void Names_are_collated_not_compared_by_codepoint()
    {
        // Ordinal ordering would file every lowercase name after every uppercase one, and "Ólafur"
        // past "Z". Both clients use a collator precisely to avoid that.
        var albums = Merge(
            Song(id: 1, artist: "Zola", albumArtist: "Zola", album: "Z", destinationPath: "/d/z/01.flac"),
            Song(id: 2, artist: "Ólafur", albumArtist: "Ólafur", album: "O", destinationPath: "/d/o/01.flac"),
            Song(id: 3, artist: "aphex", albumArtist: "aphex", album: "A", destinationPath: "/d/a/01.flac"));

        Assert.Equal(["aphex", "Ólafur", "Zola"], albums.Select(a => a.Artist));
    }

    [Fact]
    public void Tracks_are_ordered_by_track_number_then_title_with_unnumbered_last()
    {
        var album = Group(
            Song(id: 1, trackNumber: null, title: "b side"),
            Song(id: 2, trackNumber: null, title: "a side"),
            Song(id: 3, trackNumber: 2, title: "second"),
            Song(id: 4, trackNumber: 1, title: "first")).Single();

        Assert.Equal([4, 3, 2, 1], album.TrackIds);
    }

    // ── fixtures ──────────────────────────────────────────────────────────────

    private static List<AlbumSummaryDto> Group(params AlbumTrackRow[] rows) =>
        AlbumProjection.Build(rows, merge: false);

    private static List<AlbumSummaryDto> Merge(params AlbumTrackRow[] rows) =>
        AlbumProjection.Build(rows, merge: true);

    private static DateTime Utc(int y, int m, int d) => new(y, m, d, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Mirrors the clients' fixture: only the fields grouping reads, with the same defaults.
    /// <c>indexedAtUtc</c> is required on a real row (the scanner always stamps it), so it defaults
    /// to a recent one rather than being absent as it is in the JavaScript fixture.
    /// </summary>
    private static AlbumTrackRow Song(
        int id = 0,
        string fileName = "track.flac",
        string? artist = "Kanye West",
        string? albumArtist = "Kanye West",
        string? artists = null,
        string? album = "My Beautiful Dark Twisted Fantasy",
        string? title = null,
        string? destinationPath = null,
        int? trackNumber = null,
        int? year = null,
        int? durationSeconds = null,
        long fileSizeBytes = 0,
        string? genre = null,
        string? label = null,
        string? catalogNumber = null,
        string? upc = null,
        string? releaseDate = null,
        string? musicBrainzReleaseId = null,
        bool hasCoverArt = false,
        int playCount = 0,
        DateTime? likedAtUtc = null,
        bool isAlbumFill = false,
        bool isBuilt = true,
        bool isUnreleased = false,
        DateTime? spotifyAddedAtUtc = null,
        DateTime? acquiredAtUtc = null,
        DateTime? libraryBuiltAtUtc = null,
        DateTime? indexedAtUtc = null) =>
        new(id, fileName, destinationPath, album, albumArtist, artist, artists, title, trackNumber,
            year, durationSeconds, fileSizeBytes, genre, label, catalogNumber, upc, releaseDate,
            musicBrainzReleaseId, hasCoverArt, playCount, likedAtUtc, isAlbumFill, isBuilt,
            isUnreleased, spotifyAddedAtUtc, acquiredAtUtc, libraryBuiltAtUtc,
            indexedAtUtc ?? new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc));
}

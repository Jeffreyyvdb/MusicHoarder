package com.musichoarder.app.data

/** Which of the four library pages is showing. */
enum class LibraryTab { Overview, Albums, Artists, Tracks }

/** `Primary` shows lead/album artists only; `All` shows every credited artist, features included. */
enum class ArtistMode { Primary, All }

/**
 * Everything the four tabs' view state amounts to.
 *
 * It lives in the ViewModel rather than in `rememberSaveable` because the album drilldown swaps the
 * library screen out of the composition entirely, and a remembered value does not survive removal
 * without a `SaveableStateHolder` - so backing out of an album would silently clear the search box
 * and every chip. The web hit exactly this and worked around it with `sessionStorage`.
 */
data class LibraryUiState(
    val tab: LibraryTab = LibraryTab.Overview,
    /**
     * One search box shared by the three list tabs. The web keys it per route because each tab is a
     * separate page; on one screen a box that clears itself when you tap a pill reads as a bug.
     */
    val query: String = "",
    val chips: Set<ChipKey> = emptySet(),
    val sortKey: SortKey = SortKey.Added,
    val sortAscending: Boolean = false,
    val albumSort: AlbumSortKey = AlbumSortKey.Recent,
    val unreleasedOnly: Boolean = false,
    val artistMode: ArtistMode = ArtistMode.Primary,
    /** `null` is "All". `#` is the bucket for names that do not start with a Latin letter. */
    val letter: String? = null,
    /** The artist drilldown, which narrows the Albums tab in place rather than opening a screen. */
    val artistFilter: String? = null,
    val openAlbumKey: String? = null,
    /**
     * Seeds the Overview's random shelves. Held here, not in `remember`: a per-composition seed would
     * reshuffle the page on every rotation.
     */
    val seed: String = "",
)

/** The lists the four tabs render, folded once per state change off the main thread. */
data class LibraryContent(
    val albums: List<Album> = emptyList(),
    val artists: List<ArtistGroup> = emptyList(),
    /** Which A-Z buckets have anyone in them, so the bar can grey out the rest. */
    val presentLetters: Set<String> = emptySet(),
    val tracks: List<Track> = emptyList(),
    val chipCounts: Map<ChipKey, Int> = emptyMap(),
    val unreleasedCount: Int = 0,
    /** Built tracks in scope - what the grids and the Overview count. */
    val trackCount: Int = 0,
    /**
     * What the Tracks list could show before the search and the chips narrow it. Wider than
     * [trackCount], because that list also covers local files still waiting on review - so this is
     * the only honest denominator for its "N of M".
     */
    val trackListCount: Int = 0,
    /** Albums in scope before the search box, so "N of M" measures the search, not the library. */
    val albumCount: Int = 0,
    val artistCount: Int = 0,
    /** Every album, unscoped - the Overview's total. */
    val libraryAlbumCount: Int = 0,
    val overview: OverviewSections = OverviewSections(),
    /**
     * "shared by X" for the library header, or null when it is all this account's own music.
     * Precomputed with the rest of the fold, off the main thread.
     */
    val sharedByLabel: String? = null,
    /** Grantor display names by user id, for the per-item "Shared by …" badge. */
    val grantorNames: Map<String, String> = emptyMap(),
)

/** The badge label for one track, or null when this account owns it. */
fun LibraryContent.sharedByLabelFor(track: Track): String? {
    val id = track.sharedByUserId ?: return null
    return "Shared by ${grantorNames[id] ?: "someone"}"
}

/** Whether a song is hearted, reading the optimistic overlay before the fetched value. */
fun likedNow(likes: Map<Int, String?>, track: Track): Boolean =
    (if (likes.containsKey(track.id)) likes[track.id] else track.likedAtUtc) != null

/** When it was hearted, as epoch millis - the Overview's "Favourite tracks" order. */
fun likedAtMs(likes: Map<Int, String?>, track: Track): Long =
    parseIsoUtcMillis(if (likes.containsKey(track.id)) likes[track.id] else track.likedAtUtc)

/**
 * Turns the fetched library plus the current view state into the lists the tabs render.
 *
 * Pure, and deliberately structured so the expensive parts are skipped in the common case: album and
 * artist grouping only re-run when a *filter* changes, never when the user types, because search
 * narrows the grouped result rather than the input - the same shape the web uses.
 */
fun foldLibrary(
    state: LibraryState,
    ui: LibraryUiState,
    likes: Map<Int, String?>,
    plays: Map<Int, PlayStat>,
): LibraryContent {
    val playCountOf: (Track) -> Int = { plays[it.id]?.playCount ?: it.playCount }
    val lastPlayedOf: (Track) -> Long = { plays[it.id]?.lastPlayedAtMs ?: it.lastPlayedAtMs }
    val isLiked: (Track) -> Boolean = { likedNow(likes, it) }

    val unreleasedCount = state.builtTracks.count { it.isUnreleased }
    // Guard the toggle: a library that has no unreleased tracks must not render as empty.
    val filterUnreleased = ui.unreleasedOnly && unreleasedCount > 0
    val releaseScoped =
        if (filterUnreleased) state.builtTracks.filter { it.isUnreleased } else state.builtTracks

    // ---- Albums -------------------------------------------------------------------------------
    val browseScoped = ui.artistFilter
        ?.let { name -> releaseScoped.filter { matchesArtist(it, name) } }
        ?: releaseScoped
    val scopedAlbums = if (filterUnreleased || ui.artistFilter != null) {
        mergeAlbumsByName(buildAlbums(browseScoped))
    } else {
        state.albums
    }
    val query = ui.query.trim()
    val matchingAlbums = if (query.isEmpty()) scopedAlbums else scopedAlbums.filter {
        it.name.contains(query, ignoreCase = true) || it.artist.contains(query, ignoreCase = true)
    }

    // ---- Artists ------------------------------------------------------------------------------
    // Note the artist grid is scoped by the release filter but NOT by the artist drilldown, matching
    // the web: narrowing the artist list to the artist you just picked would say nothing.
    val artistGroups = when {
        filterUnreleased -> buildArtistGroups(releaseScoped, ui.artistMode == ArtistMode.Primary)
        ui.artistMode == ArtistMode.Primary -> state.artistsPrimary
        else -> state.artistsAll
    }
    val matchingArtists =
        if (query.isEmpty()) artistGroups
        else artistGroups.filter { it.label.contains(query, ignoreCase = true) }
    val presentLetters = matchingArtists.mapTo(LinkedHashSet()) { it.initial }
    val letteredArtists =
        if (ui.letter == null) matchingArtists else matchingArtists.filter { it.initial == ui.letter }

    // ---- Tracks -------------------------------------------------------------------------------
    // Album completion's tracks are dropped HERE rather than from LibraryState.trackListBase,
    // which stays whole: it is also the player's row resolver (`trackById`) and the source of the
    // liked-id set, so narrowing it would leave a filled track playing from the album screen with
    // no metadata and an unusable heart. Narrowing the view instead also lets the optimistic like
    // overlay promote a track the moment it is hearted.
    val myMusic = state.trackListBase.filter { isMyMusic(it, isLiked(it)) }
    val trackScope = ui.artistFilter
        ?.let { name -> myMusic.filter { matchesArtist(it, name) } }
        ?: myMusic
    val searched = searchTracks(trackScope, ui.query)
    val chipped = applyChips(searched, ui.chips, isLiked)
    val sorted = sortTracks(chipped, ui.sortKey, ui.sortAscending) { track ->
        if (likes.containsKey(track.id)) track.likedSortKey(likedAtMs(likes, track)) else track.likedAtMs
    }

    return LibraryContent(
        albums = sortAlbums(matchingAlbums, ui.albumSort),
        artists = letteredArtists,
        presentLetters = presentLetters,
        tracks = sorted,
        chipCounts = chipCounts(searched, ui.chips, isLiked),
        unreleasedCount = unreleasedCount,
        trackCount = releaseScoped.size,
        trackListCount = trackScope.size,
        albumCount = scopedAlbums.size,
        artistCount = artistGroups.size,
        libraryAlbumCount = state.albums.size,
        overview = buildOverviewSections(
            tracks = state.builtTracks,
            albums = state.albums,
            artists = state.artistsPrimary,
            seed = ui.seed,
            likedAtMsOf = { likedAtMs(likes, it) },
            playCountOf = playCountOf,
            lastPlayedAtMsOf = lastPlayedOf,
        ),
        sharedByLabel = state.sharedByLabel(),
        grantorNames = state.grantors.associate { grantor ->
            grantor.userId to (grantor.displayName?.trim()?.takeIf(String::isNotEmpty) ?: "someone")
        },
    )
}

/**
 * Resolves an open album key against the unscoped album list.
 *
 * Keys are destination-folder paths, but the name merge discards the losing folders' keys, so a
 * drilldown opened before a refetch can point at a folder that no longer represents its card.
 * `folderKeys` covers those; the name key is the last resort, preferring the largest card so a link
 * lands on the canonical album rather than a split-off bootleg. Mirrors `LibraryV2.svelte`.
 *
 * Deliberately resolved against **all** albums rather than the filtered grid: otherwise an open
 * album vanishes the moment the user types into the search box.
 */
fun resolveAlbum(albums: List<Album>, key: String?): Album? {
    if (key == null) return null
    albums.firstOrNull { key in it.folderKeys }?.let { return it }
    return albums.filter { it.nameKey == key }.maxByOrNull { it.trackCount }
}

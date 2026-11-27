<script lang="ts">
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import { afterNavigate } from '$app/navigation';
  import { replaceUrl } from '#lib/navigation/replace-url.ts';
  import {
    ArrowUpDown,
    Disc3,
    FileText,
    HardDrive,
    Heart,
    Link2,
    ListFilter,
    ListMusic,
    Music2,
    Play,
    Search,
    Sparkles,
    Users,
    Video,
    X
  } from '@lucide/svelte';
  import { EmptyState } from '#lib/components/ui/empty-state/index.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import ShuffleToggle from '#lib/components/v2/ShuffleToggle.svelte';
  import { shuffleScopes } from '#lib/shuffle-prefs.ts';
  import * as DropdownMenu from '#lib/components/ui/dropdown-menu/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { SearchField } from '#lib/components/ui/search-field/index.ts';
  import AlbumPage from '#lib/components/file-browser/AlbumPage.svelte';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import TrackList from '#lib/components/file-browser/TrackList.svelte';
  import FilterChip from '#lib/components/v2/FilterChip.svelte';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import LibraryAlbumsGridV2 from '#lib/components/v2/LibraryAlbumsGridV2.svelte';
  import LibraryArtistsGridV2 from '#lib/components/v2/LibraryArtistsGridV2.svelte';
  import { DYNAMIC_TYPE_CHANGE, dynamicTypeScale } from '#lib/dynamic-type.ts';
  import ArtistShelvesV2 from '#lib/components/v2/ArtistShelvesV2.svelte';
  import TrackRowMenu, { activateTrack } from '#lib/components/v2/TrackRowMenu.svelte';
  import TrackRowText from '#lib/components/v2/TrackRowText.svelte';
  import ArtistActionsV2 from '#lib/components/v2/ArtistActionsV2.svelte';
  import ArtistDiscographyV2 from '#lib/components/v2/ArtistDiscographyV2.svelte';
  import { ArtistPageState } from '#lib/stores/artist-page.svelte.ts';
  import { popularSongs, releaseCaption } from '#lib/artist-discography.ts';
  import type { ArtistRelease } from '#lib/api-client.ts';
  import { longpress, type LongPressPoint } from '#lib/actions/long-press.ts';
  import { swipeRow } from '#lib/actions/swipe-row.ts';
  import { ADD_TO_QUEUE_ICON, queueSong } from '#lib/queue/queue-actions.ts';
  import {
    CHIP_KEYS,
    CHIP_LABELS,
    countSummary,
    createTrackListView,
    FRIEND_CHIP_KEYS,
    parseChips,
    serializeChips,
    SORT_KEYS,
    SORT_LABELS,
    SORT_MENU_LABELS,
    titleOf,
    warmSearchCache,
    type ChipKey,
    type SortKey
  } from '#lib/track-list-view.svelte.ts';
  import {
    ALBUM_SORT_OPTIONS,
    buildArtistGroups,
    fetchAlbumCanonicalStatuses,
    fetchArtistImages,
    getArtistImageUrl,
    isAlbumSortKey,
    rowCoverFallbackForSong,
    rowCoverUrlForSong,
    sortAlbums,
    type AlbumSortKey,
    type AlbumStatusInfo,
    fetchAlbums,
    hydrateAlbums,
    type AlbumSummary,
    type AlbumSummaryDto,
    type ApiSong,
    type ArtistPortrait,
    type GroupSummary
  } from '#lib/api-client.ts';
  import { isAnyUnreleasedSong, isUnreleasedSong } from '#lib/release-status.ts';
  import { parseBrowseFilter, applyBrowseFilter, browseFilterLabel } from '#lib/browse-filter.ts';
  import { formatDuration, formatFileSize, formatTotalDuration } from '#lib/formatters.ts';
  import { toPlayerSong } from '#lib/api-client.ts';
  import { IsMobile } from '#lib/hooks/is-mobile.svelte.ts';
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { filterBySearch, indexForSearch, searchTerms } from '#lib/search/match.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { songDetail } from '#lib/stores/song-detail.svelte.ts';
  import { tabMemory } from '#lib/stores/tab-memory.svelte.ts';
  import { cn } from '#lib/utils.ts';
  import { isAdmin } from '#lib/auth/capabilities.ts';

  // The song-detail panel is the global SongDetailHost (mounted in the app shell), so Library
  // hosts no detail pane of its own — track selection just drives the shared store.

  type LibraryTab = 'albums' | 'artists' | 'tracks';
  type Props = {
    /** Which sub-view this route hosts. The sub-nav navigates between routes. */
    tab: LibraryTab;
  };
  const { tab }: Props = $props();

  // One boundary for "compact" (below md), the JS mirror of Tailwind's md.
  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  // Defensive sessionStorage wrappers — the (app) group is ssr=false, but guard
  // against private-mode/quota errors so view state never breaks rendering.
  function sessionGet(key: string): string | null {
    try {
      return typeof window === 'undefined' ? null : sessionStorage.getItem(key);
    } catch {
      return null;
    }
  }
  function sessionSet(key: string, value: string): void {
    try {
      if (typeof window !== 'undefined') sessionStorage.setItem(key, value);
    } catch {
      // best-effort; ignore quota / disabled-storage errors
    }
  }

  // ── data layer (shared songs store, also feeds the global detail panel) ─────
  const songCount = $derived(songsStore.songs.length);
  const isLoading = $derived(songsStore.isLoading);
  const loadError = $derived(songsStore.error);

  // Friend sessions reuse this page over the grant-scoped dataset; pipeline vocabulary
  // (enrichment %, provider badges, origin/status chips) is owner-only and hidden for them.
  const isFriend = $derived(!isAdmin(page.data.user));

  $effect(() => {
    songsStore.revalidate();
    songsStore.startLive();
    return () => songsStore.stopLive();
  });

  // ── URL state ───────────────────────────────────────────────────────────────
  const albumKey = $derived(page.url.searchParams.get('album'));
  const trackParam = $derived(page.url.searchParams.get('track'));
  const songParam = $derived(page.url.searchParams.get('song'));
  const browse = $derived(parseBrowseFilter(page.url.searchParams));

  // Filter chips live in `?f=`, not in component state, so a filtered list is linkable (the
  // Overview's Favourites row and the /liked redirect both point at one). Strictly one direction:
  // derive from the URL here, write to it only from the click handler below. Reading and writing
  // the same state inside an $effect is the read-modify-write loop that froze the song-detail
  // panel, and it is just as easy to reintroduce with a URL as with a store.
  const chips = $derived(parseChips(page.url.searchParams.get('f')));

  function setChips(next: ChipKey[]) {
    const url = new URL(page.url.href);
    const value = serializeChips(next);
    if (value) url.searchParams.set('f', value);
    else url.searchParams.delete('f');
    // A replace, not a page: Back must leave Tracks, not step through chip states.
    void replaceUrl(url.pathname + url.search);
  }

  // Search edits stay local. A playback source can reopen the Tracks list with its saved search.
  // Search is persisted per tab in
  // sessionStorage so the typed text survives drilling into an item and navigating back (the
  // artist grid remounts on a real route change, which would otherwise wipe it).
  const searchKey = $derived(`mh-lib-search:${tab}`);
  // `tab` is fixed per route mount; capture the initial stored value once.
  let query = $state(untrack(() => sessionGet(`mh-lib-search:${tab}`)) ?? '');
  afterNavigate(({ to }) => {
    const search = to?.url.searchParams.get('q');
    if (tab === 'tracks' && search != null) query = search;
  });
  $effect(() => {
    sessionSet(searchKey, query);
  });

  // ── scroll restoration for the grid scroller ────────────────────────────────
  // The album/artist grid lives inside an {#if} that is destroyed when an album drilldown opens
  // (and the whole component remounts on the artist route change), so a fresh <ScrollArea> always
  // starts at scrollTop 0. We persist the viewport's scrollTop per route (ignoring the drill-in
  // params) and restore it once the grid has laid out.
  let gridViewport = $state<HTMLElement | null>(null);
  const scrollKey = $derived.by(() => {
    const u = new URL(page.url.href);
    for (const p of ['album', 'song', 'track']) u.searchParams.delete(p);
    return `mh-lib-scroll:${u.pathname}${u.search}`;
  });
  $effect(() => {
    const vp = gridViewport;
    if (!vp) return;
    const key = scrollKey;
    const saved = Number(sessionGet(key) ?? '');
    if (saved > 0) {
      requestAnimationFrame(() => {
        // Only restore once the content is tall enough; otherwise the position
        // would clamp to 0 before the grid finishes laying out.
        if (vp.scrollHeight > vp.clientHeight) vp.scrollTop = saved;
      });
    }
    let saveTimer: ReturnType<typeof setTimeout> | null = null;
    const flush = () => {
      if (saveTimer) {
        clearTimeout(saveTimer);
        saveTimer = null;
      }
      sessionSet(key, String(Math.round(vp.scrollTop)));
    };
    const onScroll = () => {
      if (saveTimer) clearTimeout(saveTimer);
      saveTimer = setTimeout(flush, 150);
    };
    vp.addEventListener('scroll', onScroll, { passive: true });
    return () => {
      vp.removeEventListener('scroll', onScroll);
      if (saveTimer) flush();
    };
  });

  // ── derivations (only clean/built songs make up the library) ────────────────
  const builtSongs = $derived(songsStore.builtSongs);

  // What My tracks covers — see isTrackListSong for the (deliberate) shape of it.
  const trackListBase = $derived(songsStore.trackListSongs);

  // "Unreleased only": leaks/snippets/stems, as classified by the API. Grid-only — the Tracks list
  // reaches the same songs through its `unreleased` chip, which composes with the others. A toggle
  // in the grids' "Sort and filter" menu, because the grids have no chip row to fold it into.
  let unreleasedOnly = $state(untrack(() => sessionGet('mh-lib-unreleased-only')) === '1');
  $effect(() => {
    sessionSet('mh-lib-unreleased-only', unreleasedOnly ? '1' : '0');
  });
  // The filter spans both tiers, but they're counted apart for the menu's footnote — a tracker
  // saying "unreleased" is a far stronger claim than nothing having been found.
  const unreleasedCount = $derived(builtSongs.filter(isAnyUnreleasedSong).length);
  const trackerUnreleasedCount = $derived(builtSongs.filter(isUnreleasedSong).length);
  const likelyUnreleasedCount = $derived(unreleasedCount - trackerUnreleasedCount);
  const canFilterUnreleased = $derived(unreleasedCount > 0);
  // Guard the stored preference: a library that no longer has unreleased tracks (or an API too old
  // to classify them) must not silently render as empty.
  const unreleasedActive = $derived(unreleasedOnly && canFilterUnreleased);
  const releaseScoped = $derived(
    unreleasedActive ? builtSongs.filter(isAnyUnreleasedSong) : builtSongs
  );

  /** Resting explanation of the two tiers (the old hover tooltip, which a finger never reached). */
  const unreleasedDetail = $derived(
    likelyUnreleasedCount === 0
      ? `${trackerUnreleasedCount.toLocaleString()} confirmed by a community tracker`
      : `${trackerUnreleasedCount.toLocaleString()} confirmed by a tracker, ${likelyUnreleasedCount.toLocaleString()} with no catalog match`
  );

  // Grouping is the server's, so a card can span two destination folders that disagree about the
  // year or the artist spelling. `allAlbums` stays unscoped: it is the drilldown/deep-link resolver,
  // so an ?album= link must still resolve while a filter is on.
  const allAlbums = $derived(songsStore.albums);

  /**
   * The grid's cards, narrowed by the "Organize by" filter and the unreleased toggle.
   *
   * Narrowing has to happen before grouping, not after: filtering the finished cards would leave a
   * compilation showing its full track count under `?artist=`, where today it shows only that
   * artist's tracks. So a filter means asking the server again — which is a navigation, not a
   * keystroke. With no filter on this is the cached list and costs nothing.
   */
  const albumFilter = $derived({
    artist: browse?.artist ?? null,
    year: browse?.yearUnknown ? 'unknown' : browse?.year != null ? String(browse.year) : null,
    unreleased: unreleasedActive
  });
  const isAlbumFilterActive = $derived(
    albumFilter.artist !== null || albumFilter.year !== null || albumFilter.unreleased
  );
  // The last server answer, stamped with the filter it answers. A different filter's cards are never
  // shown: moving from one artist to another in the same mount (Back between two artist pages, "Go
  // to artist" from a row) used to keep the previous artist's albums, songs and Play pills on screen
  // under the new name until the fetch landed.
  let albumFilterResult = $state<{ key: string; dtos: AlbumSummaryDto[] }>({ key: '', dtos: [] });
  // Keyed on a string so this re-runs on a real filter change and stays put through every background
  // refresh of the same one.
  const albumFilterKey = $derived(
    isAlbumFilterActive
      ? [albumFilter.artist ?? '', albumFilter.year ?? '', albumFilter.unreleased].join('\u0000')
      : ''
  );
  $effect(() => {
    const key = albumFilterKey;
    if (key === '') return;
    const requested = untrack(() => ({ ...albumFilter }));
    let cancelled = false;
    void fetchAlbums(requested)
      .then((albums) => {
        if (!cancelled) albumFilterResult = { key, dtos: albums };
      })
      .catch(() => {
        if (!cancelled) albumFilterResult = { key, dtos: [] };
      });
    return () => {
      cancelled = true;
    };
  });
  // In flight: the grid shows its skeleton (and the artist view hides its pills and songs) rather
  // than "No albums match" — or another filter's albums — for the round trip.
  const albumFilterLoading = $derived(
    albumFilterKey !== '' && albumFilterResult.key !== albumFilterKey
  );
  const scopedAlbums = $derived(
    !isAlbumFilterActive
      ? allAlbums
      : albumFilterLoading
        ? []
        : hydrateAlbums(albumFilterResult.dtos, songsStore.songsById)
  );

  // Provider-link status per album (linked / localOnly / pending) for the grid corner badges.
  // One batch lookup, scoped to the albums this view actually renders and refreshed when that set
  // changes — skipped on /artists, /tracks and album drilldowns, which never show grid badges.
  let albumStatuses = $state<Map<string, AlbumStatusInfo>>(new Map());
  const statusAlbums = $derived(
    tab !== 'albums' || Boolean(albumKey) ? [] : isAlbumFilterActive ? scopedAlbums : allAlbums
  );
  const albumIdentityKey = $derived(
    statusAlbums.map((a) => `${a.artist}\u0000${a.title}`).join('\u0001')
  );
  $effect(() => {
    // The status endpoint is owner-only; a friend's grid just shows no badges.
    if (albumIdentityKey === '' || isFriend) {
      albumStatuses = new Map();
      return;
    }
    const pairs = untrack(() => statusAlbums.map((a) => ({ artist: a.artist, album: a.title })));
    let cancelled = false;
    void fetchAlbumCanonicalStatuses(pairs)
      .then((map) => {
        if (!cancelled) albumStatuses = map;
      })
      .catch(() => {
        // Badges are best-effort; leave them off on error.
      });
    return () => {
      cancelled = true;
    };
  });

  // Grid order. Defaults to recently-added (what people expect on entry, and now trustworthy — it
  // reads the immutable acquisition stamp rather than the build time, which pipeline churn bumps).
  let albumSort = $state<AlbumSortKey>(
    untrack(() => {
      const stored = sessionGet('mh-lib-album-sort');
      return isAlbumSortKey(stored) ? stored : 'recent';
    })
  );
  $effect(() => {
    sessionSet('mh-lib-album-sort', albumSort);
  });

  // Album and artist search go through the same matcher as the tracks list and the command
  // palette (`#lib/search/match.ts`): every term, in any field, over folded text.
  const albumSearchIndex = $derived(indexForSearch(scopedAlbums, (a) => [a.title, a.artist]));

  const filteredAlbums = $derived.by(() =>
    sortAlbums(filterBySearch(albumSearchIndex, searchTerms(query)), albumSort)
  );

  // Artists view: default to lead/album artists only (the discrete multi-artist tagging would
  // otherwise flood the list with featured/guest performers). "Show featured artists" in the
  // view options menu switches to every credited artist.
  let artistMode = $state<'primary' | 'all'>(
    untrack(() => sessionGet('mh-lib-artist-mode')) === 'all' ? 'all' : 'primary'
  );
  $effect(() => {
    sessionSet('mh-lib-artist-mode', artistMode);
  });

  // The default (every built song, by lead artist) is the store's shared copy.
  const artistGroups = $derived(
    !unreleasedActive && artistMode === 'primary'
      ? songsStore.leadArtistGroups
      : buildArtistGroups(releaseScoped, { primaryOnly: artistMode === 'primary' })
  );
  const artistSearchIndex = $derived(indexForSearch(artistGroups, (g) => [g.label]));
  // Following and suggestions sit above the grid for an admin (follows are admin-only), and step
  // aside while a search is typed: then the grid is the answer.
  const showArtistShelves = $derived(isAdmin(page.data.user) && query.trim() === '');
  let artistShelvesInView = $state(false);
  const filteredArtists = $derived.by(() => filterBySearch(artistSearchIndex, searchTerms(query)));

  // Artist portraits: one bulk request per label set (cached rows come back at once and point
  // straight at the CDN — no per-tile redirect through the proxy). Refetched when the group set
  // changes; the ETag makes a repeat visit a 304 per chunk. Only the artists views need it.
  let artistPortraits = $state<ReadonlyMap<string, ArtistPortrait>>(new Map());
  const artistLabelsKey = $derived(artistGroups.map((g) => g.label).join('\u0001'));
  $effect(() => {
    if (tab !== 'artists' && !albumKey) {
      artistPortraits = new Map();
      return;
    }
    const key = artistLabelsKey;
    if (key === '') {
      artistPortraits = new Map();
      return;
    }
    let cancelled = false;
    void fetchArtistImages(key.split('\u0001'))
      .then((map) => {
        if (!cancelled) artistPortraits = map;
      })
      .catch(() => {
        // Best-effort: tiles fall back to the per-name endpoint.
      });
    return () => {
      cancelled = true;
    };
  });
  /** Grid-sized portrait URL: the CDN URL when cached, else the per-name redirect endpoint. */
  function portraitUrlFor(label: string): string {
    return artistPortraits.get(label)?.url ?? getArtistImageUrl(label);
  }
  /** Hero-sized portrait URL (the provider's largest), same fallback. */
  function portraitLargeUrlFor(label: string): string {
    return artistPortraits.get(label)?.large ?? getArtistImageUrl(label);
  }

  // Tracks tab: scope by browse filter only. Search and the chips are applied inside the view, so
  // that one place owns both the visible list and every per-chip count.
  const tracksScoped = $derived(applyBrowseFilter(trackListBase, browse));

  function fallbackArtist(s: ApiSong): string {
    return (s.albumArtist ?? s.artist ?? '').trim() || 'Unknown Artist';
  }
  /**
   * Plays `list` from `from`, in the remembered shuffle state Tracks and every artist share. Never
   * pauses: every caller is labelled Play, or is a tap.
   */
  function playQueue(list: ApiSong[], from = 0) {
    void playerStore.startQueue(
      list.map((s) => toPlayerSong(s, fallbackArtist(s))),
      from,
      { source: playbackSource }
    );
  }
  /**
   * A Play button: `list` from the top in its own order, or from a random song with Shuffle on.
   * For Tracks that is exactly what the list is showing, filters and sort included.
   */
  function playAll(list: ApiSong[]) {
    void playerStore.startQueue(
      list.map((s) => toPlayerSong(s, fallbackArtist(s))),
      0,
      { shuffle: playerStore.shuffleFor(shuffleScopes.library), source: playbackSource }
    );
  }
  function playList() {
    playAll(listView.sorted);
  }

  const totalTracks = $derived(releaseScoped.length);
  const artistCount = $derived(artistGroups.length);

  // ── track-list state ───────────────────────────────────────────────────────
  // The filters live here rather than inside TrackList so the nav bar can render the search, the
  // filters and the "X of Y" subtitle. Both views are built unconditionally (a runes factory
  // can't be created inside an {#if}); the $deriveds are lazy, so the unused one costs nothing.
  const listView = createTrackListView({
    songs: () => tracksScoped,
    searchQuery: () => query,
    chips: () => chips,
    onChipsChange: setChips
  });
  const isListTab = $derived(tab === 'tracks');

  // ── album drilldown (reuses AlbumPage + TrackPanel) ─────────────────────────
  const openAlbum = $derived.by(() => {
    if (!albumKey) return null;
    // Album keys are destination-folder paths, but a link can point at a folder that lost the
    // merge's representative election (`folderKeys` covers those), and legacy/cross-page links
    // (e.g. the album-quality page) still emit the older `artistLower::albumLower` shape. Fall
    // back to matching by display artist+title, preferring the largest card (the canonical album
    // rather than a split-off bootleg) when one name still maps to several cards.
    const byFolder = (list: AlbumSummary[]) =>
      list.find((a) => a.folderKeys.includes(albumKey)) ?? null;
    const byName = (list: AlbumSummary[]) =>
      list
        .filter((a) => `${a.artist.toLowerCase()}::${a.title.toLowerCase()}` === albumKey)
        .sort((a, b) => b.trackCount - a.trackCount)[0] ?? null;
    return (
      byFolder(filteredAlbums) ??
      byFolder(allAlbums) ??
      byName(filteredAlbums) ??
      byName(allAlbums) ??
      null
    );
  });

  // Deep-link entry via ?song= / ?track= — consumed ONCE on load: open the
  // global detail store, then strip the param. The store's open state is the
  // single source of truth thereafter (no ongoing URL<->store sync, which can
  // cycle), so closing/reopening the panel never touches the URL.
  let consumedDeepLink = false;
  $effect(() => {
    if (isLoading || consumedDeepLink) return;
    const raw = songParam ?? trackParam;
    if (!raw) return;
    consumedDeepLink = true;
    const id = Number.parseInt(raw, 10);
    if (!Number.isFinite(id)) return;
    const owningAlbum = allAlbums.find((a) => a.songs.some((s) => s.id === id));
    songDetail.open(id, owningAlbum?.key);
    const url = new URL(page.url.href);
    url.searchParams.delete('song');
    url.searchParams.delete('track');
    // Preserve the drilldown context for a ?song= link that carried no ?album=.
    if (owningAlbum && tab === 'albums' && !albumKey)
      url.searchParams.set('album', owningAlbum.key);
    void replaceUrl(url.pathname + url.search);
  });

  // Highlighted row follows the open panel (the store is the source of truth).
  const tracksSelectedId = $derived(songDetail.isOpen ? (songDetail.target?.songId ?? null) : null);

  /** The visible list from `song`, what a row's tap plays. */
  function playListFrom(song: ApiSong) {
    const list = listView.sorted;
    playQueue(
      list,
      list.findIndex((s) => s.id === song.id)
    );
  }

  /** A desktop row click: toggle the detail overlay for that song, whose Play then plays `play`. */
  function selectTrack(song: ApiSong, play: () => void = () => playListFrom(song)) {
    if (songDetail.isOpen && songDetail.target?.songId === song.id) songDetail.close();
    else songDetail.open(song.id, openAlbum?.key, { play });
  }

  /** A phone row tap (the tap rule, track-list-view.svelte.ts): play the visible list from that row, or open the loaded song. */
  function activateListTrack(song: ApiSong) {
    activateTrack(song.id, () => playListFrom(song));
  }

  // ── hrefs (keep deep-linkable, reuse the v1 ?album= / ?artist= contract) ─────
  function albumHref(a: AlbumSummary): string {
    return `/library?album=${encodeURIComponent(a.key)}`;
  }
  function artistHref(g: GroupSummary): string {
    return `/library?artist=${encodeURIComponent(g.key)}`;
  }

  // Pipeline-health stat — always library-wide, so it doesn't move with the view filters.
  const enrichedPct = $derived.by(() => {
    if (builtSongs.length === 0) return null;
    return (builtSongs.length / Math.max(1, songCount)) * 100;
  });

  // ── artist view (/library?artist=) ──────────────────────────────────────────
  // An artist is a pushed page of its own (portrait, Play/Shuffle, albums, songs), not a filtered
  // Albums grid with a "filtering by" token. Same URL contract, so every existing link lands here.
  const artistName = $derived(browse?.artist ? browseFilterLabel(browse) : null);
  const isArtistView = $derived(tab === 'albums' && artistName !== null && !albumKey);
  const playbackSource = $derived.by(() => {
    const url = new URL(page.url.href);
    url.searchParams.delete('song');
    url.searchParams.delete('track');
    if (tab === 'tracks') url.searchParams.set('q', query);
    const label =
      isArtistView && artistName
        ? artistName
        : chips.length === 1
          ? CHIP_LABELS[chips[0]]
          : 'My tracks';
    return { label, href: url.pathname + url.search, shuffleScope: shuffleScopes.library };
  });
  // The artist's albums are the server-narrowed cards (a compilation shows only this artist's
  // tracks), and their songs, in album order, are the artist's song list.
  const artistAlbums = $derived(isArtistView ? sortAlbums(scopedAlbums, albumSort) : []);
  const artistSongs = $derived(artistAlbums.flatMap((a) => a.songs));

  // Virtualize an artist's Tracks list just like TrackList: an artist can have thousands of tracks
  // (2,746 for one artist in a real library), and building them all into the DOM (even in idle
  // slices on desktop) left tens of thousands of nodes and thousands of hardware-skin covers in the
  // page, tanking scroll frames and INP. Only the rows within the viewport + overscan exist in the
  // DOM, positioned inside a full-height <ul> so the page keeps its exact height and scrollbar.
  let typeScale = $state(dynamicTypeScale());
  $effect(() => {
    const onChange = () => (typeScale = dynamicTypeScale());
    window.addEventListener(DYNAMIC_TYPE_CHANGE, onChange);
    return () => window.removeEventListener(DYNAMIC_TYPE_CHANGE, onChange);
  });
  const artistRowH = $derived(compact ? Math.max(64, Math.round(64 * typeScale)) : 56);
  const ARTIST_ROW_OVERSCAN = 8;

  let artistContentEl = $state<HTMLElement | null>(null);
  let artistSongsEl = $state<HTMLElement | null>(null);
  let artistScrollTop = $state(0);
  let artistViewportH = $state(typeof ResizeObserver === 'undefined' ? 900 : 0);
  let artistSongsTop = $state(0);

  const artistStart = $derived(
    artistViewportH > 0
      ? Math.max(
          0,
          Math.floor((artistScrollTop - artistSongsTop) / artistRowH) - ARTIST_ROW_OVERSCAN
        )
      : 0
  );
  const artistEnd = $derived(
    artistViewportH > 0
      ? Math.min(
          artistSongs.length,
          Math.max(
            0,
            Math.ceil((artistScrollTop - artistSongsTop + artistViewportH) / artistRowH) +
              ARTIST_ROW_OVERSCAN
          )
        )
      : 0
  );
  const visibleArtistSongs = $derived(artistSongs.slice(artistStart, artistEnd));

  $effect(() => {
    const vp = gridViewport;
    const content = artistContentEl;
    const list = artistSongsEl;
    if (!isArtistView || !vp || !content || !list) return;

    const onScroll = () => {
      artistScrollTop = vp.scrollTop;
    };
    vp.addEventListener('scroll', onScroll, { passive: true });

    // Measure inside ResizeObserver (after layout, before paint) so we never force an early layout
    // pass, and re-measure whenever anything above the Tracks list mounts or changes height
    // (Popular, Discography, "Show all", the album grid, or window resizes).
    const measure = () => {
      const vpRect = vp.getBoundingClientRect();
      artistViewportH = vp.clientHeight;
      artistScrollTop = vp.scrollTop;
      artistSongsTop = list.getBoundingClientRect().top - vpRect.top + vp.scrollTop;
    };
    const ro = new ResizeObserver(measure);
    const observeAll = () => {
      ro.disconnect();
      ro.observe(vp);
      ro.observe(list);
      for (const child of content.children) ro.observe(child);
    };
    const mo = new MutationObserver(observeAll);
    observeAll();
    mo.observe(content, { childList: true });

    return () => {
      vp.removeEventListener('scroll', onScroll);
      ro.disconnect();
      mo.disconnect();
    };
  });
  $effect(() => {
    if (isArtistView && artistName) tabMemory.setTitle(page.url, artistName);
  });
  // A desktop bar shows Back only when a page asks; a pushed artist view does.
  const desktopBack = $derived(
    compact ? undefined : tabMemory.backTarget(page.url, page.data.user)
  );

  function activateArtistSong(song: ApiSong, index: number, list: ApiSong[] = artistSongs) {
    const play = () => playQueue(list, index);
    if (compact) activateTrack(song.id, play);
    else selectTrack(song, play);
  }

  // ── the artist page's Spotify half: Popular, what you liked, the discography, Follow ──
  // "Popular" is your own most-played songs of theirs: the only listener counted here is you.
  function statsFor(song: ApiSong) {
    const stats = songsStore.playStatsOf(song);
    return {
      plays: stats.playCount ?? 0,
      lastPlayed: stats.lastPlayedAtUtc ? Date.parse(stats.lastPlayedAtUtc) || 0 : 0,
      liked: Boolean(song.likedAtUtc)
    };
  }
  let popularExpanded = $state(false);
  let artistHeroTitle = $state<HTMLElement | null>(null);
  const popularAll = $derived(isArtistView ? popularSongs(artistSongs, statsFor, 10) : []);
  const popular = $derived(popularExpanded ? popularAll : popularAll.slice(0, 5));
  const likedArtistSongs = $derived(artistSongs.filter((s) => s.likedAtUtc));
  $effect(() => {
    // A new artist starts collapsed.
    void artistName;
    popularExpanded = false;
  });

  // The catalog side is administration (it queues downloads), so only an admin's page asks for it.
  const showCatalog = $derived(isArtistView && isAdmin(page.data.user));
  const artistPage = new ArtistPageState();
  $effect(() => {
    if (showCatalog && artistName && untrack(() => artistPage.artist) !== artistName) {
      void artistPage.load(artistName);
    }
  });
  /** The newest release that is out — Spotify's "Artist pick" spot. */
  const latestRelease = $derived(
    showCatalog ? (artistPage.data?.releases.find((r) => !r.upcoming) ?? null) : null
  );
  const artistAlbumByTitle = $derived.by(() => {
    const map = new Map<string, AlbumSummary>();
    for (const album of artistAlbums) {
      const key = album.title.toLowerCase();
      if (!map.has(key)) map.set(key, album);
    }
    return map;
  });
  function releaseHref(release: ArtistRelease): string | null {
    const album = artistAlbumByTitle.get((release.libraryAlbum ?? release.title).toLowerCase());
    return album ? albumHref(album) : null;
  }

  type RowMenu = { openAt: (point: LongPressPoint) => void };
  const artistMenus: Record<number, RowMenu | undefined> = {};

  // ── nav bar text ───────────────────────────────────────────────────────────
  const TOOLBAR_TITLE = { albums: 'Albums', artists: 'Artists', tracks: 'My tracks' };
  const SEARCH_LABEL = {
    albums: 'Search albums',
    artists: 'Search artists',
    tracks: 'Search tracks'
  };

  // The chip glyphs. Their words are CHIP_LABELS; the long
  // explanations used to be hover tooltips, which a finger never reaches, so they stay only as
  // desktop titles.
  const CHIP_ICON: Record<ChipKey, typeof Heart> = {
    'spotify-liked': Music2,
    'mh-liked': Heart,
    local: HardDrive,
    added: Link2,
    video: Video,
    lyrics: FileText,
    unreleased: Sparkles
  };
  const CHIP_TITLE = $derived<Record<ChipKey, string>>({
    'spotify-liked': 'Tracks you own that are saved in your Spotify Liked Songs, newest save first',
    'mh-liked': 'Tracks you added to your favourites here — independent of Spotify',
    local:
      'Files a scan found already sitting in your source library, rather than something MusicHoarder downloaded. Includes ones still waiting on review.',
    added: 'Tracks you imported yourself from a URL',
    video: 'Tracks with a music video downloaded',
    lyrics: 'Tracks with synced or plain lyrics',
    unreleased: `Unreleased tracks — ${unreleasedDetail}`
  });

  // The same explanations, short enough for a resting second line in the phone's menu — a finger
  // never reaches a tooltip, so a phone would otherwise get the bare word.
  const visibleChipKeys = $derived(isFriend ? FRIEND_CHIP_KEYS : CHIP_KEYS);
  // A sort key the dataset cannot answer for this account is left out rather than offered as a
  // no-op: the shared rows carry no Spotify save dates.
  const visibleSortKeys = $derived(isFriend ? SORT_KEYS.filter((k) => k !== 'spotify') : SORT_KEYS);

  /**
   * Who shared the rows on screen ("Alex", "Alex and Sam", "Alex and 2 others"), read from the last
   * songs fetch so it names whoever actually shared them. Empty for an account browsing only its
   * own music.
   */
  const sharedBySuffix = $derived.by(() => {
    const names = songsStore.grantors.map((g) => g.displayName?.trim() || 'someone');
    if (names.length === 0) return '';
    if (names.length === 1) return ` · Shared by ${names[0]}`;
    if (names.length === 2) return ` · Shared by ${names[0]} and ${names[1]}`;
    return ` · Shared by ${names[0]} and ${names.length - 1} others`;
  });

  // The subtitle. On a phone it is short — what is listed, and how long it plays — and says
  // "N of M" whenever anything narrows the list, since the search field and the filter tokens
  // scroll away. The desktop bar has room for the old summary (size, sort, % enriched).
  const toolbarMeta = $derived.by(() => {
    if (isListTab) {
      const { sorted, songs, stats, sortKey, sortDir } = listView;
      const head = countSummary(sorted.length, songs.length, 'track');
      // Nothing to total when the list is empty — the empty state explains itself.
      if (sorted.length === 0) return head;
      if (compact) return `${head} · ${formatTotalDuration(stats.totalSec)}${sharedBySuffix}`;
      return `${head} · ${formatFileSize(stats.totalBytes)} · ${formatTotalDuration(stats.totalSec)} · by ${SORT_LABELS[sortKey]} ${sortDir === 'asc' ? '↑' : '↓'}${sharedBySuffix}`;
    }
    if (compact) {
      const year = browse && !browse.artist ? `${browseFilterLabel(browse)} · ` : '';
      return tab === 'albums'
        ? `${year}${countSummary(filteredAlbums.length, scopedAlbums.length, 'album')}${sharedBySuffix}`
        : `${countSummary(filteredArtists.length, artistGroups.length, 'artist')}${sharedBySuffix}`;
    }
    const enriched =
      !isFriend && enrichedPct != null ? ` · ${enrichedPct.toFixed(1)}% enriched` : '';
    return `${totalTracks.toLocaleString()} tracks · ${artistCount.toLocaleString()} artists${enriched}${sharedBySuffix}`;
  });

  const artistMeta = $derived.by(() => {
    // Held open (not dropped) while this artist's albums load, so the title doesn't jump when the
    // counts land.
    if (albumFilterLoading) return '\u00a0';
    const albums = `${artistAlbums.length} album${artistAlbums.length === 1 ? '' : 's'}`;
    // "tracks", as the nav, the Artists list and every other count say.
    const tracks = `${artistSongs.length} track${artistSongs.length === 1 ? '' : 's'}`;
    return `${albums} · ${tracks}${unreleasedActive ? ' · Unreleased only' : ''}`;
  });
  /** Loaded, and none of this artist's songs are here: a followed artist's page is catalog only. */
  const ownsNothing = $derived(artistSongs.length === 0 && !albumFilterLoading && !isLoading);
  /** The hero's line: what you have of them (or that you have nothing yet), and the follow. */
  const heroMeta = $derived.by(() => {
    const following = showCatalog && artistPage.data?.follow ? 'Following' : null;
    const have = ownsNothing ? 'Not in your library yet' : artistMeta;
    return [have, following].filter(Boolean).join(' · ');
  });
</script>

<!-- ── shared nav-bar pieces ─────────────────────────────────────────────── -->

{#snippet searchField()}
  <!-- Tracks only: warm the search cache before typing starts, so the first keystroke only has
       to assemble already-folded rows instead of folding all of them inside it (the Albums and
       Artists grids are a few hundred rows, not thousands, so they cost nothing either way). -->
  <SearchField
    bind:value={query}
    label={SEARCH_LABEL[tab]}
    onfocus={tab === 'tracks' ? () => warmSearchCache(trackListBase) : undefined}
    onpointerdown={tab === 'tracks' ? () => warmSearchCache(trackListBase) : undefined}
  />
{/snippet}

{#snippet menuTrigger(label: string, glyph: 'sort' | 'filter')}
  <DropdownMenu.Trigger>
    {#snippet child({ props })}
      <Button {...props} variant="ghost" size="icon" aria-label={label} title={label}>
        {#if glyph === 'sort'}<ArrowUpDown />{:else}<ListFilter />{/if}
      </Button>
    {/snippet}
  </DropdownMenu.Trigger>
{/snippet}

{#snippet unreleasedItem()}
  {#if canFilterUnreleased}
    <!-- The two confidence tiers used to live in a hover tooltip; they are a resting footnote now. -->
    <DropdownMenu.CheckboxItem
      checked={unreleasedOnly}
      onCheckedChange={(v) => (unreleasedOnly = v)}
      closeOnSelect={false}
    >
      <span class="flex min-w-0 flex-1 flex-col">
        <span>Unreleased only</span>
        <span class="text-muted-foreground pointer-coarse:text-footnote text-xs"
          >{unreleasedDetail}</span
        >
      </span>
      <span class="text-muted-foreground tabular-nums">{unreleasedCount.toLocaleString()}</span>
    </DropdownMenu.CheckboxItem>
  {/if}
{/snippet}

<!-- Tracks. A phone: Sort by (a submenu that names the current order) · the filters · Clear —
     three groups, one submenu level, so nothing lands below the fold. Desktop keeps its chips in a
     band under the bar, so its menu is the flat sort list. -->
{#snippet sortItems()}
  <DropdownMenu.RadioGroup
    value={listView.sortKey}
    onValueChange={(v) => listView.setSortKey(v as SortKey)}
  >
    {#if !compact}
      <DropdownMenu.GroupHeading class="text-muted-foreground px-2 py-1 text-xs font-medium">
        Sort by
      </DropdownMenu.GroupHeading>
    {/if}
    {#each visibleSortKeys as key (key)}
      <DropdownMenu.RadioItem value={key}>{SORT_MENU_LABELS[key]}</DropdownMenu.RadioItem>
    {/each}
  </DropdownMenu.RadioGroup>
  <DropdownMenu.Separator />
  <DropdownMenu.RadioGroup
    value={listView.sortDir}
    onValueChange={(v) => listView.setSortDir(v as 'asc' | 'desc')}
  >
    <DropdownMenu.RadioItem value="asc">Ascending</DropdownMenu.RadioItem>
    <DropdownMenu.RadioItem value="desc">Descending</DropdownMenu.RadioItem>
  </DropdownMenu.RadioGroup>
{/snippet}

{#snippet tracksMenu()}
  <DropdownMenu.Root>
    {@render menuTrigger('Sort and filter', 'sort')}
    <DropdownMenu.Content align="end" class="max-h-[min(75svh,40rem)] w-64 pointer-coarse:w-72">
      {#if compact}
        <DropdownMenu.Sub>
          <DropdownMenu.SubTrigger>
            <span class="flex min-w-0 flex-1 flex-col">
              <span>Sort by</span>
              <span class="text-muted-foreground text-footnote truncate">
                {SORT_MENU_LABELS[listView.sortKey]} · {listView.sortDir === 'asc'
                  ? 'Ascending'
                  : 'Descending'}
              </span>
            </span>
          </DropdownMenu.SubTrigger>
          <DropdownMenu.SubContent class="max-h-[min(70svh,36rem)] overflow-y-auto">
            {@render sortItems()}
          </DropdownMenu.SubContent>
        </DropdownMenu.Sub>
        <DropdownMenu.Separator />
        <!-- Checkbox items with the chip's count — what pressing it would leave, the same rule as
             the desktop chips — and the menu stays open so several can be combined. One line
             each ("Favourites 17"): the explanations are the desktop chips' tooltips, and on a
             phone they made the menu most of the screen tall. A filter that could only empty the
             list is shown unavailable (dimmed), unless it is the one already on. -->
        <DropdownMenu.Group>
          <DropdownMenu.GroupHeading>Filter</DropdownMenu.GroupHeading>
          {#each visibleChipKeys as key (key)}
            {@const active = listView.isChipActive(key)}
            {@const count = listView.countFor(key)}
            <DropdownMenu.CheckboxItem
              checked={active}
              onCheckedChange={() => listView.toggleChip(key)}
              disabled={!active && count === 0}
              closeOnSelect={false}
            >
              <span class="min-w-0 flex-1 truncate">{CHIP_LABELS[key]}</span>
              <span class="text-muted-foreground tabular-nums">{count.toLocaleString()}</span>
            </DropdownMenu.CheckboxItem>
          {/each}
        </DropdownMenu.Group>
      {:else}
        {@render sortItems()}
      {/if}
      {#if listView.hasFilters}
        <DropdownMenu.Separator />
        <DropdownMenu.Item onSelect={() => listView.clearFilters()}>Clear filters</DropdownMenu.Item
        >
      {/if}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/snippet}

<!-- Albums (and an artist's albums): sort by · unreleased only. -->
{#snippet albumsMenu()}
  <DropdownMenu.Root>
    {@render menuTrigger('Sort and filter', 'sort')}
    <DropdownMenu.Content align="end" class="w-64 pointer-coarse:w-72">
      <DropdownMenu.RadioGroup
        value={albumSort}
        onValueChange={(v) => {
          if (isAlbumSortKey(v)) albumSort = v;
        }}
      >
        <DropdownMenu.GroupHeading class="text-muted-foreground px-2 py-1 text-xs font-medium">
          Sort by
        </DropdownMenu.GroupHeading>
        {#each ALBUM_SORT_OPTIONS as option (option.key)}
          <DropdownMenu.RadioItem value={option.key}>{option.label}</DropdownMenu.RadioItem>
        {/each}
      </DropdownMenu.RadioGroup>
      {#if canFilterUnreleased}
        <DropdownMenu.Separator />
        {@render unreleasedItem()}
      {/if}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/snippet}

<!-- Artists: which artists are listed. There is no artist sort, so the glyph is a filter. -->
{#snippet artistsMenu()}
  <DropdownMenu.Root>
    {@render menuTrigger('View options', 'filter')}
    <DropdownMenu.Content align="end" class="w-64 pointer-coarse:w-72">
      <DropdownMenu.CheckboxItem
        checked={artistMode === 'all'}
        onCheckedChange={(v) => (artistMode = v ? 'all' : 'primary')}
        closeOnSelect={false}
      >
        <span class="flex min-w-0 flex-1 flex-col">
          <span>Show featured artists</span>
          <span class="text-muted-foreground pointer-coarse:text-footnote text-xs">
            Everyone credited on a track, not only lead artists
          </span>
        </span>
      </DropdownMenu.CheckboxItem>
      {@render unreleasedItem()}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/snippet}

<!-- The desktop chip band (a phone gets these as checkbox items in the menu instead). -->
{#snippet chipRow()}
  {#each visibleChipKeys as key (key)}
    <FilterChip
      pressed={listView.isChipActive(key)}
      onclick={() => listView.toggleChip(key)}
      icon={CHIP_ICON[key]}
      count={listView.countFor(key)}
      title={CHIP_TITLE[key]}
    >
      {CHIP_LABELS[key]}
    </FilterChip>
  {/each}
  {#if listView.hasFilters}
    <Button
      variant="ghost"
      size="sm"
      class="text-primary h-8 px-2.5"
      onclick={() => listView.clearFilters()}
    >
      Clear
    </Button>
  {/if}
{/snippet}

{#snippet playPills(onplay: () => void, className = '')}
  {#if compact}
    <!-- Two equal capsules, tint glyph and label: Apple Music's Play / Shuffle. -->
    <div class={cn('flex shrink-0 gap-3 px-4', className)}>
      <Button variant="gray" size="pill" class="text-primary flex-1" onclick={onplay}>
        <Play fill="currentColor" /> Play
      </Button>
      <ShuffleToggle scope={shuffleScopes.library} appearance="pill" class="flex-1" />
    </div>
  {:else}
    {@render desktopPlayButtons(onplay)}
  {/if}
{/snippet}

{#snippet desktopPlayButtons(onplay: () => void)}
  <!-- md+: one treatment on Tracks, an album and an artist — Play is the prominent button (the
       view's one primary action), the Shuffle toggle beside it. -->
  <div class="flex shrink-0 gap-2">
    <Button size="sm" class="h-8 gap-1.5 rounded-full px-3" onclick={onplay}>
      <Play class="size-4" fill="currentColor" /> Play
    </Button>
    <ShuffleToggle scope={shuffleScopes.library} appearance="sm" />
  </div>
{/snippet}

<!-- Tracks header: rendered by TrackList INSIDE its scroller, so the bar is sticky and the large
     title, search, filter tokens and Play/Shuffle scroll away with the rows. -->
{#snippet tracksHeader()}
  <PageToolbarV2
    title="My tracks"
    meta={toolbarMeta}
    metaFrom="lg"
    search={searchField}
    filterRow={compact ? undefined : chipRow}
  >
    {#snippet actions()}
      {#if !compact && listView.sorted.length > 0}
        <!-- Desktop: Play/Shuffle stay in the bar, labelled. On a phone they are the list's
             first row instead. They act on the filtered list, so they follow the filters. -->
        {@render desktopPlayButtons(playList)}
      {/if}
      {@render tracksMenu()}
    {/snippet}
  </PageToolbarV2>
  {#if compact && listView.hasFilters}
    <!-- Only while something narrows the list: one row of removable tokens. At rest, nothing. -->
    <div
      data-scroll-x=""
      role="group"
      aria-label="Active filters"
      class="no-scrollbar flex shrink-0 scroll-px-4 items-center gap-2 overflow-x-auto overflow-y-hidden px-4 py-1"
    >
      {#each listView.chips as key (key)}
        <button
          type="button"
          onclick={() => listView.toggleChip(key)}
          aria-label="Remove filter {CHIP_LABELS[key]}"
          class="bg-primary/12 text-primary text-subheadline focus-visible:ring-ring relative flex h-8 shrink-0 items-center gap-1 rounded-full pr-2 pl-3 font-medium whitespace-nowrap transition-transform duration-150 ease-[cubic-bezier(0.23,1,0.32,1)] after:absolute after:inset-x-0 after:-inset-y-1.5 focus-visible:ring-2 focus-visible:outline-hidden active:scale-[0.97]"
        >
          {CHIP_LABELS[key]}
          <X class="size-4" aria-hidden="true" />
        </button>
      {/each}
      <button
        type="button"
        onclick={() => listView.clearFilters()}
        class="text-primary text-subheadline relative flex h-8 shrink-0 items-center px-2 font-medium after:absolute after:inset-x-0 after:-inset-y-1.5 focus-visible:underline focus-visible:outline-hidden"
      >
        Clear
      </button>
    </div>
  {/if}
  {#if compact && listView.sorted.length > 0}
    {@render playPills(playList, 'pb-1')}
  {/if}
{/snippet}

<!-- Blank lists — one pattern on every Listen page (EmptyState): a search miss names the search
     and offers to clear it; a filter dead end offers to clear the filters. Rendered inside the
     scroller, so the search field that would clear it stays reachable. -->
{#snippet searchMiss(filtersOn: boolean, clearFilters: () => void)}
  <EmptyState
    icon={Search}
    title={`No results for “${query.trim()}”`}
    hint={filtersOn
      ? 'Nothing matches both the search and the filters.'
      : 'Check the spelling or try another search.'}
    action={filtersOn
      ? { label: 'Clear filters', onclick: clearFilters }
      : { label: 'Clear search', onclick: () => (query = '') }}
  />
{/snippet}

{#snippet tracksEmpty()}
  {#if query.trim()}
    {@render searchMiss(listView.hasFilters, () => listView.clearFilters())}
  {:else if listView.hasFilters}
    <EmptyState
      icon={ListMusic}
      title="No tracks match these filters"
      hint="Each filter's number is what it would leave, so a zero is the dead end."
      action={{ label: 'Clear filters', onclick: () => listView.clearFilters() }}
    />
  {:else}
    <EmptyState icon={ListMusic} title="No tracks yet" />
  {/if}
{/snippet}

{#snippet albumsEmpty()}
  {#if query.trim()}
    {@render searchMiss(unreleasedActive, () => (unreleasedOnly = false))}
  {:else if unreleasedActive}
    <EmptyState
      icon={Disc3}
      title="No unreleased albums"
      action={{ label: 'Show all albums', onclick: () => (unreleasedOnly = false) }}
    />
  {:else}
    <EmptyState icon={Disc3} title="No albums yet" />
  {/if}
{/snippet}

{#snippet artistsEmpty()}
  {#if query.trim()}
    {@render searchMiss(false, () => {})}
  {:else}
    <EmptyState icon={Users} title="No artists yet" />
  {/if}
{/snippet}

<!-- An artist's song row: the phone row design (48pt art, title over album, ⋯). Used in flow for
     Popular (5–10 rows) and virtually positioned inside the Tracks list (`virtual = true`). -->
{#snippet artistSongRow(
  song: ApiSong,
  index: number,
  list: ApiSong[] = artistSongs,
  ranked = false,
  virtual = false
)}
  {@const isLoaded = playerStore.currentSong?.id === song.id}
  {@const plays = ranked ? (songsStore.playStatsOf(song).playCount ?? 0) : 0}
  <li
    aria-setsize={virtual ? list.length : undefined}
    aria-posinset={virtual ? index + 1 : undefined}
    use:longpress={{ onlongpress: (p) => artistMenus[song.id]?.openAt(p) }}
    use:swipeRow={{
      side: 'leading',
      tone: 'primary',
      icon: ADD_TO_QUEUE_ICON,
      onswipe: () => queueSong(song)
    }}
    oncontextmenu={(e) => {
      e.preventDefault();
      artistMenus[song.id]?.openAt({ x: e.clientX, y: e.clientY });
    }}
    style={virtual ? `top: ${index * artistRowH}px; height: ${artistRowH}px;` : undefined}
    class={cn(
      'group has-[[data-row-main]:active]:bg-accent md:hover:bg-accent flex items-center pr-1 transition-colors duration-100 md:rounded-lg',
      virtual ? 'absolute inset-x-0' : 'relative min-h-16 md:min-h-14',
      index < list.length - 1 &&
        "after:bg-separator after:absolute after:right-0 after:bottom-0 after:left-[76px] after:h-(--hairline) after:content-[''] md:after:left-[68px]",
      ranked && index < list.length - 1 && 'after:left-[104px] md:after:left-[96px]'
    )}
  >
    <button
      type="button"
      data-row-main=""
      onclick={() => activateArtistSong(song, index, list)}
      aria-current={isLoaded ? 'true' : undefined}
      class={cn(
        'focus-visible:ring-ring flex min-w-0 flex-1 items-center gap-3 pl-4 text-left focus-visible:ring-2 focus-visible:outline-hidden focus-visible:ring-inset md:pl-2',
        virtual ? 'h-full' : 'min-h-16 md:min-h-14'
      )}
    >
      {#if ranked}
        <span
          class="text-muted-foreground text-body w-4 shrink-0 text-center tabular-nums md:text-sm"
          aria-hidden="true"
        >
          {index + 1}
        </span>
      {/if}
      <span class="relative shrink-0">
        <Cover
          artist={fallbackArtist(song)}
          title={song.album ?? titleOf(song)}
          coverUrl={rowCoverUrlForSong(song)}
          fallbackUrl={rowCoverFallbackForSong(song)}
          size={48}
          corner={6}
          caption={false}
          dprCap={3}
          class="md:size-10!"
        />
        {#if isLoaded}
          <!-- The loaded song: an equalizer over its art, as in Tracks. -->
          <span
            class="absolute inset-0 grid place-items-center rounded-[6px] bg-black/45 text-white"
            aria-hidden="true"
          >
            <span class={cn('mh-eq', playerStore.isPlaying && 'is-playing')}>
              <i></i><i></i><i></i>
            </span>
          </span>
        {/if}
      </span>
      <!-- The same text column as Tracks (heart, Review and shared marks included); the artist
           is the page, so the second line names the album. -->
      <TrackRowText
        {song}
        loaded={isLoaded}
        opensPlayer={isLoaded && compact}
        secondary={ranked
          ? `${plays > 0 ? `${plays.toLocaleString()} play${plays === 1 ? '' : 's'} · ` : ''}${song.album ?? '—'}`
          : `${song.album ?? '—'}${song.year ? ` · ${song.year}` : ''}`}
      />
      <span class="text-muted-foreground hidden text-xs tabular-nums md:inline">
        {formatDuration(song.durationSeconds)}
      </span>
    </button>
    <TrackRowMenu
      bind:this={() => artistMenus[song.id], (m) => (artistMenus[song.id] = m)}
      {song}
      showArtist={false}
      onplay={() => playQueue(list, index)}
    />
  </li>
{/snippet}

<!-- ── pages ─────────────────────────────────────────────────────────────── -->

{#if loadError && songCount === 0 && !isLoading}
  <!-- Nothing loaded at all: the error and Retry, whatever the URL asked for (an album link must
       not read "Album not found" when the library simply failed to load). -->
  <ScrollArea class="min-h-0 flex-1">
    <PageToolbarV2
      title={tab === 'albums' && albumKey ? 'Album' : (artistName ?? TOOLBAR_TITLE[tab])}
      back={albumKey || isArtistView ? desktopBack : undefined}
    />
    <div class="flex flex-col items-center justify-center gap-3 px-6 py-16 text-center">
      <p class="text-destructive-text text-subheadline md:text-sm">{loadError}</p>
      <Button variant="gray" class="rounded-full" onclick={() => void songsStore.loadSongs()}
        >Retry</Button
      >
    </div>
  </ScrollArea>
{:else if tab === 'albums' && albumKey}
  <!-- Album drilldown. It renders its own nav bar; while the library loads (or when the key
       resolves to nothing) AlbumPage shows its own loading / not-found states. -->
  <AlbumPage album={openAlbum} isLoading={isLoading || songCount === 0} />
{:else if isListTab}
  <!-- Tracks: the virtualized TrackList. The min-h-0 flex chain keeps its scroll viewport bounded
       so virtualization works. On a phone a row tap follows the tap rule; on desktop it toggles
       the detail overlay. -->
  <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <TrackList
      view={listView}
      {playbackSource}
      {isLoading}
      selectedId={tracksSelectedId}
      onSelect={selectTrack}
      onActivate={compact ? activateListTrack : undefined}
      header={tracksHeader}
      empty={tracksEmpty}
    />
  </div>
{:else if isArtistView && artistName}
  <!-- Artist view: a pushed page (Back → wherever you came from, else Artists). -->
  <ScrollArea bind:viewportRef={gridViewport} class="min-h-0 flex-1">
    <!-- The hero's name is the title (Apple Music's album page): the bar's inline title fades in
         once it scrolls under, so the name is never shown twice at rest. -->
    <PageToolbarV2
      title={artistName}
      back={desktopBack}
      largeTitle={false}
      collapseAfter={artistHeroTitle}
    >
      {#snippet actions()}
        {@render albumsMenu()}
      {/snippet}
    </PageToolbarV2>

    <div bind:this={artistContentEl} class="flex flex-col gap-7 pb-6 md:gap-8 md:px-7">
      <!-- Hero, after Spotify's artist header: a big round portrait, the name large, what you have
           of them, then the action row (Play, Shuffle, Follow, More). -->
      <div class="flex flex-col gap-5">
        <div
          class="flex flex-col items-center gap-4 pt-2 md:flex-row md:items-end md:gap-7 md:pt-4"
        >
          <Cover
            eager
            artist={artistAlbums[0]?.artist ?? artistName}
            title={artistAlbums[0]?.title ?? artistName}
            coverUrl={portraitLargeUrlFor(artistName)}
            fallbackUrl={artistPage.data?.catalog.pictureUrl ?? artistAlbums[0]?.coverUrl ?? null}
            size={compact ? 160 : 208}
            corner={compact ? 80 : 104}
            caption={false}
            dprCap={3}
            class="shrink-0 shadow-[0_10px_30px_rgb(0_0_0/0.25)]"
          />
          <div
            class="flex min-w-0 flex-col items-center gap-1 text-center md:items-start md:pb-2 md:text-left"
          >
            <h1
              bind:this={artistHeroTitle}
              class="max-w-full truncate text-[34px] leading-tight font-black tracking-tight md:text-5xl lg:text-7xl"
            >
              {artistName}
            </h1>
            <p class="text-muted-foreground text-subheadline md:text-sm">
              {heroMeta}
            </p>
          </div>
        </div>
        <ArtistActionsV2
          page={showCatalog ? artistPage : null}
          canPlay={artistSongs.length > 0}
          onplay={() => playAll(artistSongs)}
          shuffleScope={shuffleScopes.library}
        />
      </div>

      {#if popularAll.length > 0 || likedArtistSongs.length > 0 || latestRelease}
        <div class="grid grid-cols-1 gap-7 lg:grid-cols-[minmax(0,1fr)_320px] lg:gap-10">
          {#if popularAll.length > 0}
            <section aria-labelledby="artist-popular-heading">
              <h2 id="artist-popular-heading" class="text-title-2 mb-1 px-4 md:px-0 md:text-lg">
                Popular
              </h2>
              <ul>
                {#each popular as song, index (song.id)}
                  {@render artistSongRow(song, index, popular, true)}
                {/each}
              </ul>
              {#if popularAll.length > 5}
                <button
                  type="button"
                  class="text-muted-foreground hover:text-foreground text-subheadline relative mt-1 ml-4 h-11 font-semibold md:ml-2 md:h-8 md:text-sm"
                  onclick={() => (popularExpanded = !popularExpanded)}
                >
                  {popularExpanded ? 'Show less' : 'See more'}
                </button>
              {/if}
            </section>
          {/if}

          {#if likedArtistSongs.length > 0 || latestRelease}
            <div class="flex flex-col gap-7 px-4 md:px-0">
              {#if likedArtistSongs.length > 0}
                <section aria-labelledby="artist-liked-heading">
                  <h2 id="artist-liked-heading" class="text-title-2 mb-3 md:text-lg">You liked</h2>
                  <button
                    type="button"
                    class="hover:bg-accent focus-visible:ring-ring -mx-2 flex w-[calc(100%+1rem)] items-center gap-4 rounded-xl p-2 text-left focus-visible:ring-2 focus-visible:outline-hidden"
                    onclick={() => playAll(likedArtistSongs)}
                    aria-label={`Play the ${likedArtistSongs.length} songs you liked by ${artistName}`}
                  >
                    <span class="relative shrink-0">
                      <Cover
                        artist={artistName}
                        title={artistName}
                        coverUrl={portraitUrlFor(artistName)}
                        fallbackUrl={artistAlbums[0]?.coverUrl ?? null}
                        size={72}
                        corner={36}
                        caption={false}
                      />
                      <span
                        class="bg-primary text-primary-foreground absolute -right-0.5 -bottom-0.5 grid size-6 place-items-center rounded-full ring-2 ring-(--background)"
                      >
                        <Heart class="size-3.5" fill="currentColor" aria-hidden="true" />
                      </span>
                    </span>
                    <span class="min-w-0">
                      <span class="text-headline block md:text-sm md:font-semibold">
                        {likedArtistSongs.length} song{likedArtistSongs.length === 1 ? '' : 's'}
                      </span>
                      <span class="text-muted-foreground text-subheadline block truncate md:text-xs"
                        >By {artistName}</span
                      >
                    </span>
                  </button>
                </section>
              {/if}

              {#if latestRelease}
                {@const href = releaseHref(latestRelease)}
                <section aria-labelledby="artist-latest-heading">
                  <h2 id="artist-latest-heading" class="text-title-2 mb-3 md:text-lg">
                    Latest release
                  </h2>
                  <svelte:element
                    this={href ? 'a' : 'div'}
                    {href}
                    class="hover:bg-accent focus-visible:ring-ring -mx-2 flex items-center gap-4 rounded-xl p-2 focus-visible:ring-2 focus-visible:outline-hidden"
                  >
                    <Cover
                      artist={artistName}
                      title={latestRelease.title}
                      coverUrl={latestRelease.coverUrl}
                      size={72}
                      corner={8}
                      caption={false}
                      class="shrink-0"
                    />
                    <span class="min-w-0">
                      <span class="text-headline block truncate md:text-sm md:font-semibold"
                        >{latestRelease.title}</span
                      >
                      <span class="text-muted-foreground text-subheadline block md:text-xs"
                        >{releaseCaption(latestRelease)}</span
                      >
                    </span>
                  </svelte:element>
                </section>
              {/if}
            </div>
          {/if}
        </div>
      {/if}

      {#if showCatalog}
        <ArtistDiscographyV2 state={artistPage} hrefFor={releaseHref} {compact} />
      {/if}

      <!-- An artist you follow but own nothing of has only the catalog to show; no empty grid. -->
      {#if !(showCatalog && ownsNothing)}
        <section aria-labelledby="artist-albums-heading">
          <h2 id="artist-albums-heading" class="text-title-2 mb-2 px-4 md:px-0 md:text-lg">
            {showCatalog ? 'In your library' : 'Albums'}
          </h2>
          <div class="px-3 md:-mx-1 md:px-0">
            <LibraryAlbumsGridV2
              albums={artistAlbums}
              hrefFor={albumHref}
              isLoading={isLoading || albumFilterLoading}
              statuses={albumStatuses}
              menu
              showArtistLink={false}
            />
          </div>
        </section>
      {/if}

      {#if artistSongs.length > 0}
        <section aria-labelledby="artist-songs-heading">
          <h2 id="artist-songs-heading" class="text-title-2 mb-1 px-4 md:px-0 md:text-lg">
            Tracks
          </h2>
          <ul
            bind:this={artistSongsEl}
            class="relative [contain:layout_style]"
            style="height: {artistSongs.length * artistRowH}px;"
          >
            {#each visibleArtistSongs as song, vi (song.id)}
              {@const index = artistStart + vi}
              {@render artistSongRow(song, index, artistSongs, false, true)}
            {/each}
          </ul>
        </section>
      {/if}
    </div>
  </ScrollArea>
{:else}
  <!-- Albums / Artists: the nav bar is the ScrollArea's first child, so its large title and search
       scroll away and the bar collapses; the viewport already reserves the tab-bar clearance. -->
  <ScrollArea bind:viewportRef={gridViewport} class="min-h-0 flex-1">
    <PageToolbarV2 title={TOOLBAR_TITLE[tab]} meta={toolbarMeta} metaFrom="lg" search={searchField}>
      {#snippet actions()}
        {#if tab === 'albums'}
          {@render albumsMenu()}
        {:else}
          {@render artistsMenu()}
        {/if}
      {/snippet}
    </PageToolbarV2>

    {#if tab === 'albums'}
      <!-- px-3 / md:px-6: each tile carries a 4px focus-ring inset, so the art lands on the same
           16 / 28px margin as the title and search field. -->
      <div class="flex flex-col gap-4 px-3 pt-2 pb-4 md:px-6 md:py-5">
        <LibraryAlbumsGridV2
          albums={filteredAlbums}
          hrefFor={albumHref}
          isLoading={isLoading || albumFilterLoading}
          statuses={albumStatuses}
          menu
          empty={albumsEmpty}
        />
        {#if filteredAlbums.length > 0}
          <div class="text-muted-foreground text-footnote text-center tabular-nums md:text-[11px]">
            {filteredAlbums.length.toLocaleString()} album{filteredAlbums.length === 1 ? '' : 's'}
          </div>
        {/if}
      </div>
    {:else}
      {#if showArtistShelves}
        <ArtistShelvesV2 bind:inView={artistShelvesInView} />
      {/if}
      <div class="pb-4 md:px-7 md:py-5">
        <LibraryArtistsGridV2
          groups={filteredArtists}
          hrefFor={artistHref}
          {isLoading}
          portraits={artistPortraits}
          indexSuppressed={showArtistShelves && artistShelvesInView}
          empty={artistsEmpty}
        />
      </div>
    {/if}
  </ScrollArea>
{/if}

<style>
  /* The now-playing equalizer (TrackList's): three bars, animated only while playing. */
  .mh-eq {
    display: inline-flex;
    align-items: flex-end;
    justify-content: center;
    gap: 2px;
    height: 13px;
  }
  .mh-eq > i {
    width: 2.5px;
    height: 35%;
    border-radius: 1px;
    background: currentColor;
  }
  /* As in TrackList: while it animates, a bar is full height and scaled from its foot, so the
     animation runs on the compositor instead of laying the page out on every frame. */
  .mh-eq.is-playing > i {
    height: 100%;
    transform-origin: bottom;
    animation: mh-eq 0.9s ease-in-out infinite;
  }
  .mh-eq > i:nth-child(1) {
    animation-delay: -0.5s;
  }
  .mh-eq > i:nth-child(2) {
    animation-delay: -0.2s;
  }
  .mh-eq > i:nth-child(3) {
    animation-delay: -0.7s;
  }
  @keyframes mh-eq {
    0%,
    100% {
      transform: scaleY(0.3);
    }
    50% {
      transform: scaleY(1);
    }
  }
  @media (prefers-reduced-motion: reduce) {
    .mh-eq.is-playing > i {
      height: 35%;
      animation: none;
    }
  }
</style>

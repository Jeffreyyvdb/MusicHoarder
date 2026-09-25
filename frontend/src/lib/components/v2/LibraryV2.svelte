<script lang="ts">
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import { replaceUrl } from '$lib/navigation/replace-url';
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
    Shuffle,
    Sparkles,
    Users,
    Video,
    X
  } from '@lucide/svelte';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { SearchField } from '$lib/components/ui/search-field';
  import AlbumPage from '$lib/components/file-browser/AlbumPage.svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import TrackList from '$lib/components/file-browser/TrackList.svelte';
  import FilterChip from '$lib/components/v2/FilterChip.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import LibraryAlbumsGridV2 from '$lib/components/v2/LibraryAlbumsGridV2.svelte';
  import LibraryArtistsGridV2 from '$lib/components/v2/LibraryArtistsGridV2.svelte';
  import TrackRowMenu, { activateTrack } from '$lib/components/v2/TrackRowMenu.svelte';
  import TrackRowText from '$lib/components/v2/TrackRowText.svelte';
  import { longpress, type LongPressPoint } from '$lib/actions/long-press';
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
    type ChipKey,
    type SortKey
  } from '$lib/track-list-view.svelte';
  import {
    ALBUM_SORT_OPTIONS,
    buildArtistGroups,
    coverUrlForSong,
    fetchAlbumCanonicalStatuses,
    getArtistImageUrl,
    isAlbumSortKey,
    sortAlbums,
    type AlbumSortKey,
    type AlbumStatusInfo,
    fetchAlbums,
    hydrateAlbums,
    type AlbumSummary,
    type AlbumSummaryDto,
    type ApiSong,
    type GroupSummary
  } from '$lib/api-client';
  import { isAnyUnreleasedSong, isUnreleasedSong } from '$lib/release-status';
  import { parseBrowseFilter, applyBrowseFilter, browseFilterLabel } from '$lib/browse-filter';
  import { formatDuration, formatFileSize, formatTotalDuration } from '$lib/formatters';
  import { toPlayerSong } from '$lib/api-client';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';
  import { cn, shuffle } from '$lib/utils';
  import { isAdmin } from '$lib/auth/capabilities';

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
  const songs = $derived(songsStore.songs);
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
    const url = new URL(page.url);
    const value = serializeChips(next);
    if (value) url.searchParams.set('f', value);
    else url.searchParams.delete('f');
    // A replace, not a page: Back must leave Tracks, not step through chip states.
    void replaceUrl(url.pathname + url.search);
  }

  // Local search box (not a URL param, so the routes stay untouched). Persisted per-tab in
  // sessionStorage so the typed text survives drilling into an item and navigating back (the
  // artist grid remounts on a real route change, which would otherwise wipe it).
  const searchKey = $derived(`mh-lib-search:${tab}`);
  // `tab` is fixed per route mount; capture the initial stored value once.
  let query = $state(untrack(() => sessionGet(`mh-lib-search:${tab}`)) ?? '');
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
    const u = new URL(page.url);
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
    const onScroll = () => sessionSet(key, String(Math.round(vp.scrollTop)));
    vp.addEventListener('scroll', onScroll, { passive: true });
    return () => vp.removeEventListener('scroll', onScroll);
  });

  // ── derivations (only clean/built songs make up the library) ────────────────
  const builtSongs = $derived(songsStore.builtSongs);

  // What the Tracks list covers — see isTrackListSong for the (deliberate) shape of it.
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
  // One batch lookup, refreshed when the album set changes. `allAlbums` is rebuilt into fresh
  // objects on every songs-store refresh, so the effect keys on a string of the identities it
  // actually sends — otherwise it re-posted the whole library every time the store refetched.
  let albumStatuses = $state<Map<string, AlbumStatusInfo>>(new Map());
  const albumIdentityKey = $derived(
    allAlbums.map((a) => `${a.artist}\u0000${a.title}`).join('\u0001')
  );
  $effect(() => {
    // The status endpoint is owner-only; a friend's grid just shows no badges.
    if (albumIdentityKey === '' || isFriend) {
      albumStatuses = new Map();
      return;
    }
    const pairs = untrack(() => allAlbums.map((a) => ({ artist: a.artist, album: a.title })));
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

  function albumMatchesQuery(a: AlbumSummary, q: string): boolean {
    return a.title.toLowerCase().includes(q) || a.artist.toLowerCase().includes(q);
  }

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

  const filteredAlbums = $derived.by(() => {
    const q = query.trim().toLowerCase();
    const matching = q ? scopedAlbums.filter((a) => albumMatchesQuery(a, q)) : scopedAlbums;
    return sortAlbums(matching, albumSort);
  });

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
  const filteredArtists = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return artistGroups;
    return artistGroups.filter((g) => g.label.toLowerCase().includes(q));
  });

  // Tracks tab: scope by browse filter only. Search and the chips are applied inside the view, so
  // that one place owns both the visible list and every per-chip count.
  const tracksScoped = $derived(applyBrowseFilter(trackListBase, browse));

  function fallbackArtist(s: ApiSong): string {
    return (s.albumArtist ?? s.artist ?? '').trim() || 'Unknown Artist';
  }
  /** Plays `list` from `from`. Never pauses: every caller is labelled Play or Shuffle, or is a tap. */
  function playQueue(list: ApiSong[], from = 0) {
    void playerStore.startQueue(
      list.map((s) => toPlayerSong(s, fallbackArtist(s))),
      from
    );
  }
  /** Plays exactly what the list is showing, in the order it is showing it. */
  function playList() {
    playQueue(listView.sorted);
  }
  /** Shuffles exactly what the list is showing, filters and sort included. */
  function shuffleList() {
    playQueue(shuffle(listView.sorted));
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
    const url = new URL(page.url);
    url.searchParams.delete('song');
    url.searchParams.delete('track');
    // Preserve the drilldown context for a ?song= link that carried no ?album=.
    if (owningAlbum && tab === 'albums' && !albumKey)
      url.searchParams.set('album', owningAlbum.key);
    void replaceUrl(url.pathname + url.search, { keepFocus: false });
  });

  // Highlighted row follows the open panel (the store is the source of truth).
  const tracksSelectedId = $derived(songDetail.isOpen ? (songDetail.target?.songId ?? null) : null);

  /** A desktop row click: toggle the detail overlay for that song. */
  function selectTrack(song: ApiSong) {
    if (songDetail.isOpen && songDetail.target?.songId === song.id) songDetail.close();
    else songDetail.open(song.id, openAlbum?.key);
  }

  /** A phone row tap (the tap rule, track-list-view.svelte.ts): play the visible list from that row, or open the loaded song. */
  function activateListTrack(song: ApiSong) {
    activateTrack(song.id, () => {
      const list = listView.sorted;
      playQueue(
        list,
        list.findIndex((s) => s.id === song.id)
      );
    });
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
    return (builtSongs.length / Math.max(1, songs.length)) * 100;
  });

  // ── artist view (/library?artist=) ──────────────────────────────────────────
  // An artist is a pushed page of its own (portrait, Play/Shuffle, albums, songs), not a filtered
  // Albums grid with a "filtering by" token. Same URL contract, so every existing link lands here.
  const artistName = $derived(browse?.artist ? browseFilterLabel(browse) : null);
  const isArtistView = $derived(tab === 'albums' && artistName !== null && !albumKey);
  // The artist's albums are the server-narrowed cards (a compilation shows only this artist's
  // tracks), and their songs, in album order, are the artist's song list.
  const artistAlbums = $derived(isArtistView ? sortAlbums(scopedAlbums, albumSort) : []);
  const artistSongs = $derived(artistAlbums.flatMap((a) => a.songs));
  $effect(() => {
    if (isArtistView && artistName) tabMemory.setTitle(page.url, artistName);
  });
  // A desktop bar shows Back only when a page asks; a pushed artist view does.
  const desktopBack = $derived(
    compact ? undefined : tabMemory.backTarget(page.url, page.data.user)
  );

  function activateArtistSong(song: ApiSong, index: number) {
    const play = () => playQueue(artistSongs, index);
    if (compact) activateTrack(song.id, play);
    else selectTrack(song);
  }

  type RowMenu = { openAt: (point: LongPressPoint) => void };
  const artistMenus: Record<number, RowMenu | undefined> = {};

  // ── nav bar text ───────────────────────────────────────────────────────────
  const TOOLBAR_TITLE = { albums: 'Albums', artists: 'Artists', tracks: 'Tracks' };
  const SEARCH_LABEL = {
    albums: 'Search albums',
    artists: 'Search artists',
    tracks: 'Search tracks'
  };

  // The chip glyphs. Their words are CHIP_LABELS (shared with the Android port); the long
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
</script>

<!-- ── shared nav-bar pieces ─────────────────────────────────────────────── -->

{#snippet searchField()}
  <SearchField bind:value={query} label={SEARCH_LABEL[tab]} />
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

{#snippet playPills(onplay: () => void, onshuffle: () => void, className = '')}
  {#if compact}
    <!-- Two equal gray capsules, tint glyph and label: Apple Music's Play / Shuffle. -->
    <div class={cn('flex shrink-0 gap-3 px-4', className)}>
      <Button variant="gray" size="pill" class="text-primary flex-1" onclick={onplay}>
        <Play fill="currentColor" /> Play
      </Button>
      <Button variant="gray" size="pill" class="text-primary flex-1" onclick={onshuffle}>
        <Shuffle /> Shuffle
      </Button>
    </div>
  {:else}
    {@render desktopPlayButtons(onplay, onshuffle)}
  {/if}
{/snippet}

{#snippet desktopPlayButtons(onplay: () => void, onshuffle: () => void)}
  <!-- md+: one treatment on Tracks, an album and an artist — Play is the prominent button (the
       view's one primary action), Shuffle the gray one beside it. -->
  <div class="flex shrink-0 gap-2">
    <Button size="sm" class="h-8 gap-1.5 rounded-full px-3" onclick={onplay}>
      <Play class="size-4" fill="currentColor" /> Play
    </Button>
    <Button variant="gray" size="sm" class="h-8 gap-1.5 rounded-full px-3" onclick={onshuffle}>
      <Shuffle class="size-4" /> Shuffle
    </Button>
  </div>
{/snippet}

<!-- Tracks header: rendered by TrackList INSIDE its scroller, so the bar is sticky and the large
     title, search, filter tokens and Play/Shuffle scroll away with the rows. -->
{#snippet tracksHeader()}
  <PageToolbarV2
    title="Tracks"
    meta={toolbarMeta}
    metaFrom="lg"
    search={searchField}
    filterRow={compact ? undefined : chipRow}
  >
    {#snippet actions()}
      {#if !compact && listView.sorted.length > 0}
        <!-- Desktop: Play/Shuffle stay in the bar, labelled. On a phone they are the list's
             first row instead. They act on the filtered list, so they follow the filters. -->
        {@render desktopPlayButtons(playList, shuffleList)}
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
      class="no-scrollbar flex shrink-0 scroll-px-4 items-center gap-2 overflow-x-auto px-4 py-1"
    >
      {#each listView.chips as key (key)}
        <button
          type="button"
          onclick={() => listView.toggleChip(key)}
          aria-label="Remove filter {CHIP_LABELS[key]}"
          class="bg-primary/12 text-primary text-subheadline focus-visible:ring-ring relative flex h-8 shrink-0 items-center gap-1 rounded-full pr-2 pl-3 font-medium whitespace-nowrap transition-transform duration-150 ease-[cubic-bezier(0.23,1,0.32,1)] outline-none after:absolute after:inset-x-0 after:-inset-y-1.5 focus-visible:ring-2 active:scale-[0.97]"
        >
          {CHIP_LABELS[key]}
          <X class="size-4" aria-hidden="true" />
        </button>
      {/each}
      <button
        type="button"
        onclick={() => listView.clearFilters()}
        class="text-primary text-subheadline relative flex h-8 shrink-0 items-center px-2 font-medium outline-none after:absolute after:inset-x-0 after:-inset-y-1.5 focus-visible:underline"
      >
        Clear
      </button>
    </div>
  {/if}
  {#if compact && listView.sorted.length > 0}
    {@render playPills(playList, shuffleList, 'pb-1')}
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

<!-- An artist's song row: the phone row design (48pt art, title over album, ⋯), not virtualized —
     an artist's songs are a few dozen at most. -->
{#snippet artistSongRow(song: ApiSong, index: number)}
  {@const isLoaded = playerStore.currentSong?.id === song.id}
  <li
    use:longpress={{ onlongpress: (p) => artistMenus[song.id]?.openAt(p) }}
    oncontextmenu={(e) => {
      e.preventDefault();
      artistMenus[song.id]?.openAt({ x: e.clientX, y: e.clientY });
    }}
    class={cn(
      'group has-[[data-row-main]:active]:bg-accent md:hover:bg-accent relative flex min-h-16 items-center pr-1 transition-colors duration-100 md:min-h-14 md:rounded-lg',
      index < artistSongs.length - 1 &&
        "after:bg-separator after:absolute after:right-0 after:bottom-0 after:left-[76px] after:h-(--hairline) after:content-[''] md:after:left-[68px]"
    )}
  >
    <button
      type="button"
      data-row-main=""
      onclick={() => activateArtistSong(song, index)}
      aria-current={isLoaded ? 'true' : undefined}
      class="focus-visible:ring-ring flex min-h-16 min-w-0 flex-1 items-center gap-3 pl-4 text-left outline-none focus-visible:ring-2 focus-visible:ring-inset md:min-h-14 md:pl-2"
    >
      <span class="relative shrink-0">
        <Cover
          artist={fallbackArtist(song)}
          title={song.album ?? titleOf(song)}
          coverUrl={coverUrlForSong(song)}
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
        secondary={`${song.album ?? '—'}${song.year ? ` · ${song.year}` : ''}`}
      />
      <span class="text-muted-foreground hidden text-xs tabular-nums md:inline">
        {formatDuration(song.durationSeconds)}
      </span>
    </button>
    <TrackRowMenu
      bind:this={() => artistMenus[song.id], (m) => (artistMenus[song.id] = m)}
      {song}
      showArtist={false}
      onplay={() => playQueue(artistSongs, index)}
    />
  </li>
{/snippet}

<!-- ── pages ─────────────────────────────────────────────────────────────── -->

{#if loadError && songs.length === 0 && !isLoading}
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
  <AlbumPage album={openAlbum} isLoading={isLoading || songs.length === 0} />
{:else if isListTab}
  <!-- Tracks: the virtualized TrackList. The min-h-0 flex chain keeps its scroll viewport bounded
       so virtualization works. On a phone a row tap follows the tap rule; on desktop it toggles
       the detail overlay. -->
  <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <TrackList
      view={listView}
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
    <PageToolbarV2 title={artistName} meta={artistMeta} back={desktopBack}>
      {#snippet actions()}
        {@render albumsMenu()}
      {/snippet}
    </PageToolbarV2>

    <div class="flex flex-col gap-6 pb-6 md:px-7">
      <!-- Hero: a round portrait (falls back to an album cover), then the artist's Play/Shuffle. -->
      <div class="flex flex-col items-center gap-4 pt-2 md:flex-row md:items-center md:gap-6">
        <Cover
          artist={artistAlbums[0]?.artist ?? artistName}
          title={artistAlbums[0]?.title ?? artistName}
          coverUrl={getArtistImageUrl(artistName)}
          fallbackUrl={artistAlbums[0]?.coverUrl ?? null}
          size={120}
          corner={60}
          caption={false}
          dprCap={3}
          class="shrink-0 shadow-[0_6px_20px_rgb(0_0_0/0.18)]"
        />
        {#if artistSongs.length > 0}
          {@render playPills(
            () => playQueue(artistSongs),
            () => playQueue(shuffle(artistSongs)),
            'w-full md:w-auto md:px-0 md:[&>*]:flex-none'
          )}
        {/if}
      </div>

      <section aria-labelledby="artist-albums-heading">
        <h2 id="artist-albums-heading" class="text-title-2 mb-2 px-4 md:px-0 md:text-lg">Albums</h2>
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

      {#if artistSongs.length > 0}
        <section aria-labelledby="artist-songs-heading">
          <h2 id="artist-songs-heading" class="text-title-2 mb-1 px-4 md:px-0 md:text-lg">Tracks</h2>
          <ul>
            {#each artistSongs as song, index (song.id)}
              {@render artistSongRow(song, index)}
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
      <div class="pb-4 md:px-7 md:py-5">
        <LibraryArtistsGridV2
          groups={filteredArtists}
          hrefFor={artistHref}
          {isLoading}
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
  .mh-eq.is-playing > i {
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
      height: 30%;
    }
    50% {
      height: 100%;
    }
  }
  @media (prefers-reduced-motion: reduce) {
    .mh-eq.is-playing > i {
      animation: none;
    }
  }
</style>

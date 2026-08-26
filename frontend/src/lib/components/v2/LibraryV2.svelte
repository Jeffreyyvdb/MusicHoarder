<script lang="ts">
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import {
    ArrowUpDown,
    Disc3,
    FileText,
    HardDrive,
    Heart,
    Link2,
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
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import AlbumPage from '$lib/components/file-browser/AlbumPage.svelte';
  import TrackList from '$lib/components/file-browser/TrackList.svelte';
  import FilterChip from '$lib/components/v2/FilterChip.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import LibraryAlbumsGridV2 from '$lib/components/v2/LibraryAlbumsGridV2.svelte';
  import LibraryArtistsGridV2 from '$lib/components/v2/LibraryArtistsGridV2.svelte';
  import {
    CHIP_KEYS,
    createTrackListView,
    parseChips,
    serializeChips,
    SORT_LABELS,
    type ChipKey
  } from '$lib/track-list-view.svelte';
  import {
    ALBUM_SORT_OPTIONS,
    buildArtistGroups,
    fetchAlbumCanonicalStatuses,
    isAlbumSortKey,
    isLocalFile,
    isMyMusic,
    mapEnrichmentStatus,
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
  import { isBuiltSong } from '$lib/album-sections';
  import { isAnyUnreleasedSong, isUnreleasedSong } from '$lib/release-status';
  import { parseBrowseFilter, applyBrowseFilter, browseFilterLabel } from '$lib/browse-filter';
  import { formatFileSize, formatTotalDuration } from '$lib/formatters';
  import { toPlayerSong } from '$lib/api-client';
  import { breadcrumbStore } from '$lib/stores/breadcrumbs.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { shuffle } from '$lib/utils';
  import { isAdmin } from '$lib/auth/capabilities';

  // The song-detail panel is now the global SongDetailHost (mounted in the app
  // shell), so Library no longer hosts its own resizable side-pane / bottom
  // Sheet — track selection just drives the shared store. The desktop/mobile
  // form-factor split lives in SongDetailHost.

  type LibraryTab = 'albums' | 'artists' | 'tracks';
  type Props = {
    /** Which sub-view this route hosts. The sub-nav navigates between routes. */
    tab: LibraryTab;
  };
  const { tab }: Props = $props();

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
    void songsStore.loadSongs();
    songsStore.startLive();
    return () => songsStore.stopLive();
  });

  // ── URL state ───────────────────────────────────────────────────────────────
  const albumKey = $derived(page.url.searchParams.get('album'));
  const trackParam = $derived(page.url.searchParams.get('track'));
  const songParam = $derived(page.url.searchParams.get('song'));
  const browse = $derived(parseBrowseFilter(page.url.searchParams));

  // Filter chips live in `?f=`, not in component state, so a filtered list is linkable (the Overview's
  // Favourite-tracks card and the /liked redirect both point at one) and the back button steps through
  // them. Strictly one direction: derive from the URL here, write to it only from the click handler
  // below. Reading and writing the same state inside an $effect is the read-modify-write loop that
  // froze the song-detail panel, and it is just as easy to reintroduce with a URL as with a store.
  const chips = $derived(parseChips(page.url.searchParams.get('f')));

  function setChips(next: ChipKey[]) {
    const url = new URL(page.url);
    const value = serializeChips(next);
    if (value) url.searchParams.set('f', value);
    else url.searchParams.delete('f');
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true, keepFocus: true });
  }

  // Local search box (matches the prototype's header search, not a URL param so
  // the v1 routes stay untouched). Persisted per-tab in sessionStorage so the
  // typed text survives drilling into an item and navigating back (the artist
  // grid remounts on a real route change, which would otherwise wipe it).
  const searchKey = $derived(`mh-lib-search:${tab}`);
  // `tab` is fixed per route mount; capture the initial stored value once.
  let query = $state(untrack(() => sessionGet(`mh-lib-search:${tab}`)) ?? '');
  $effect(() => {
    sessionSet(searchKey, query);
  });

  // ── scroll restoration for the grid scroller ────────────────────────────────
  // The album/artist grid lives inside an {#if} that is destroyed when an album
  // drilldown opens (and the whole component remounts on the artist route
  // change), so a fresh <ScrollArea> always starts at scrollTop 0. We persist the
  // viewport's scrollTop per route (ignoring the drill-in params) and restore it
  // once the grid has laid out.
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
  const builtSongs = $derived(songs.filter(isBuiltSong));

  const needsReview = (s: ApiSong) => mapEnrichmentStatus(s.enrichmentStatus) === 'needsreview';

  /**
   * What the Tracks list covers: the music you asked for — everything built, plus your own source
   * files still waiting on review.
   *
   * Wider than the album/artist grids in one direction and narrower in another, both deliberate.
   * Wider: the grids show what the builder produced, but the "Local files" chip has to be able to
   * answer "what is on my share", and a scanned file sitting at NeedsReview is already yours — it
   * has a playable file (the stream endpoint falls back to the source path), and the Inbox is where
   * you *act* on it, not where you find it. Narrower: album completion's tracks are excluded —
   * otherwise the one flat list of what you chose fills up with records you never asked for a track
   * from. They are still in Albums, on the album page and on the Overview's "New to you" shelf — the
   * places where a complete album is the point — and liking one promotes it into this list.
   *
   * Doing both here, rather than letting a chip add or remove rows, is what keeps every chip a pure
   * narrowing of one stated base.
   */
  const trackListBase = $derived(
    songs.filter((s) => isMyMusic(s) && (isBuiltSong(s) || (isLocalFile(s) && needsReview(s))))
  );

  // "Unreleased only": leaks/snippets/stems, as classified by the API. Grid-only now — the Tracks
  // list reaches the same songs through its `unreleased` chip, which composes with the others. Kept
  // as a standalone toggle here because the grids have no chip row to fold it into.
  let unreleasedOnly = $state(untrack(() => sessionGet('mh-lib-unreleased-only')) === '1');
  $effect(() => {
    sessionSet('mh-lib-unreleased-only', unreleasedOnly ? '1' : '0');
  });
  // The filter spans both tiers, but they're counted apart for the tooltip — a tracker saying
  // "unreleased" is a far stronger claim than nothing having been found.
  const unreleasedCount = $derived(builtSongs.filter(isAnyUnreleasedSong).length);
  const trackerUnreleasedCount = $derived(builtSongs.filter(isUnreleasedSong).length);
  const likelyUnreleasedCount = $derived(unreleasedCount - trackerUnreleasedCount);
  const canFilterUnreleased = $derived(unreleasedCount > 0);
  // Guard the stored preference: a library that no longer has unreleased tracks (or an API too old
  // to classify them) must not silently render as empty.
  const releaseScoped = $derived(
    unreleasedOnly && canFilterUnreleased ? builtSongs.filter(isAnyUnreleasedSong) : builtSongs
  );

  const unreleasedTitle = $derived(
    likelyUnreleasedCount === 0
      ? `Show only unreleased tracks (${trackerUnreleasedCount.toLocaleString()} confirmed by a community tracker)`
      : `Show only unreleased tracks — ${trackerUnreleasedCount.toLocaleString()} confirmed by a community tracker, ${likelyUnreleasedCount.toLocaleString()} with no catalog match anywhere`
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
    unreleased: unreleasedOnly && canFilterUnreleased
  });
  const isAlbumFilterActive = $derived(
    albumFilter.artist !== null || albumFilter.year !== null || albumFilter.unreleased
  );
  let filteredAlbumDtos = $state<AlbumSummaryDto[]>([]);
  // Keyed on a string so this re-runs on a real filter change and stays put through every background
  // refresh of the same one.
  const albumFilterKey = $derived(
    isAlbumFilterActive
      ? [albumFilter.artist ?? '', albumFilter.year ?? '', albumFilter.unreleased].join('\u0000')
      : ''
  );
  $effect(() => {
    if (albumFilterKey === '') {
      filteredAlbumDtos = [];
      return;
    }
    const requested = untrack(() => ({ ...albumFilter }));
    let cancelled = false;
    void fetchAlbums(requested)
      .then((albums) => {
        if (!cancelled) filteredAlbumDtos = albums;
      })
      .catch(() => {
        if (!cancelled) filteredAlbumDtos = [];
      });
    return () => {
      cancelled = true;
    };
  });
  const scopedAlbums = $derived(
    isAlbumFilterActive ? hydrateAlbums(filteredAlbumDtos, songsStore.songsById) : allAlbums
  );

  // Provider-link status per album (linked / localOnly / pending) for the grid corner badges.
  // One batch lookup, refreshed when the album set changes. `allAlbums` is rebuilt into fresh
  // objects on every songs-store refresh, so the effect keys on a string of the identities it
  // actually sends — otherwise it re-posted the whole library every time the store refetched.
  let albumStatuses = $state<Map<string, AlbumStatusInfo>>(new Map());
  const albumIdentityKey = $derived(allAlbums.map((a) => `${a.artist}\u0000${a.title}`).join('\u0001'));
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

  // Artists view: default to lead/album artists only (the discrete multi-artist
  // tagging would otherwise flood the grid with featured/guest performers).
  // Persisted across sessions-of-this-tab like the search box.
  let artistMode = $state<'primary' | 'all'>(
    untrack(() => sessionGet('mh-lib-artist-mode')) === 'all' ? 'all' : 'primary'
  );
  $effect(() => {
    sessionSet('mh-lib-artist-mode', artistMode);
  });

  const artistGroups = $derived(
    buildArtistGroups(releaseScoped, { primaryOnly: artistMode === 'primary' })
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
  /** Plays exactly what the list is showing, in the order it is showing it. */
  function playList() {
    const queue = listView.sorted.map((s) => toPlayerSong(s, fallbackArtist(s)));
    if (queue.length > 0) void playerStore.playSong(queue[0], queue, 0);
  }
  /** Shuffles exactly what the list is showing, filters and sort included. */
  function shuffleList() {
    const queue = shuffle(listView.sorted).map((s) => toPlayerSong(s, fallbackArtist(s)));
    if (queue.length > 0) void playerStore.playSong(queue[0], queue, 0);
  }

  const totalTracks = $derived(releaseScoped.length);
  const artistCount = $derived(artistGroups.length);

  // ── track-list state ───────────────────────────────────────────────────────
  // The filters live here rather than inside TrackList so the page toolbar can
  // render the chips and the "X of Y" summary next to the search box — one bar
  // instead of the two stacked filter rows this replaced. Both views are built
  // unconditionally (a runes factory can't be created inside an {#if}); the
  // $deriveds are lazy, so the unused one costs nothing.
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

  $effect(() => {
    if (openAlbum) {
      breadcrumbStore.setAlbum({ artist: openAlbum.artist, title: openAlbum.title });
    } else {
      breadcrumbStore.clear();
    }
    return () => breadcrumbStore.clear();
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
    if (owningAlbum && tab === 'albums' && !albumKey) url.searchParams.set('album', owningAlbum.key);
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  });

  // Highlighted row follows the open panel (the store is the source of truth).
  const tracksSelectedId = $derived(songDetail.isOpen ? (songDetail.target?.songId ?? null) : null);

  function selectTrack(song: ApiSong) {
    if (songDetail.isOpen && songDetail.target?.songId === song.id) songDetail.close();
    else songDetail.open(song.id, openAlbum?.key);
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

  function clearArtistFilter() {
    void goto('/library', { noScroll: true });
  }

  // ── page toolbar ───────────────────────────────────────────────────────────
  // The Liked hero used to be a 129px Spotify-style banner. Everything it said —
  // the name, the count, the runtime, Play/Shuffle — fits the toolbar, which the
  // page pays for anyway.
  const TOOLBAR_ICON = { albums: Disc3, artists: Users, tracks: ListMusic };
  const TOOLBAR_TITLE = { albums: 'Albums', artists: 'Artists', tracks: 'Tracks' };

  // The chip row. One record so the label, glyph and tooltip for a chip live next to each other and
  // the row renders from a loop — adding a chip is one entry here plus one in CHIP_PREDICATES.
  // Derived, not a constant: the Unreleased tooltip breaks its two confidence tiers down by count,
  // so it moves with the library.
  const CHIP_META: Record<ChipKey, { label: string; icon: typeof Heart; title: string }> = $derived({
    'spotify-liked': {
      label: 'Spotify Liked',
      icon: Music2,
      title: 'Tracks you own that are saved in your Spotify Liked Songs, newest save first'
    },
    'mh-liked': {
      label: 'MusicHoarder Liked',
      icon: Heart,
      title: 'Tracks you hearted here — the local like, independent of Spotify'
    },
    local: {
      label: 'Local files',
      icon: HardDrive,
      title:
        'Files a scan found already sitting in your source library, rather than something MusicHoarder downloaded. Includes ones still waiting on review.'
    },
    added: {
      label: 'Manually added',
      icon: Link2,
      title: 'Tracks you imported yourself from a URL'
    },
    video: { label: 'Has video', icon: Video, title: 'Tracks with a music video downloaded' },
    lyrics: { label: 'With lyrics', icon: FileText, title: 'Tracks with synced or plain lyrics' },
    unreleased: { label: 'Unreleased', icon: Sparkles, title: unreleasedTitle }
  });

  // Chips a friend can act on: liked (their own state), video and lyrics. The rest is
  // pipeline/origin vocabulary the shared dataset deliberately doesn't carry.
  const FRIEND_CHIP_KEYS: readonly ChipKey[] = ['mh-liked', 'video', 'lyrics'];
  const visibleChipKeys = $derived(isFriend ? FRIEND_CHIP_KEYS : CHIP_KEYS);

  // Library-wide pipeline health on the grid tabs; what you're looking at right
  // now on the list tabs (and only "X of Y" once a filter actually narrows it,
  // so an unfiltered list doesn't read "3,525 of 3,525").
  /**
   * "shared by X" for the library header. Reads the grantors from the last songs fetch, so it
   * names whoever actually shared the rows on screen rather than assuming a single library
   * owner. Empty for an account browsing only its own music.
   */
  const sharedBySuffix = $derived.by(() => {
    const names = songsStore.grantors.map((g) => g.displayName?.trim() || 'someone');
    if (names.length === 0) return '';
    if (names.length === 1) return ` · shared by ${names[0]}`;
    if (names.length === 2) return ` · shared by ${names[0]} and ${names[1]}`;
    return ` · shared by ${names[0]} and ${names.length - 1} others`;
  });

  const toolbarMeta = $derived.by(() => {
    if (!isListTab) {
      const enriched =
        !isFriend && enrichedPct != null ? ` · ${enrichedPct.toFixed(1)}% enriched` : '';
      return `${totalTracks.toLocaleString()} tracks · ${artistCount.toLocaleString()} artists${enriched}${sharedBySuffix}`;
    }
    const { sorted, songs, stats, sortKey, sortDir } = listView;
    const noun = 'track';
    const head =
      sorted.length === songs.length
        ? `${sorted.length.toLocaleString()} ${noun}${sorted.length === 1 ? '' : 's'}`
        : `${sorted.length.toLocaleString()} of ${songs.length.toLocaleString()}`;
    // Nothing to total or sort when the list is empty — "0 songs · — · — · by
    // liked ↓" is noise next to the empty state that already explains itself.
    if (sorted.length === 0) return head;
    return `${head} · ${formatFileSize(stats.totalBytes)} · ${formatTotalDuration(stats.totalSec)} · by ${SORT_LABELS[sortKey]} ${sortDir === 'asc' ? '↑' : '↓'}${sharedBySuffix}`;
  });
</script>

{#snippet chipRow()}
  {#each visibleChipKeys as key (key)}
    {@const meta = CHIP_META[key]}
    <FilterChip
      pressed={listView.isChipActive(key)}
      onclick={() => listView.toggleChip(key)}
      icon={meta.icon}
      count={listView.countFor(key)}
      title={meta.title}
    >
      {meta.label}
    </FilterChip>
  {/each}
{/snippet}

{#if openAlbum && tab === 'albums'}
  <!-- Album drilldown. Track selection drives the global SongDetailHost (mounted
       in the app shell), which pushes this page on desktop and is a bottom Sheet
       on mobile — so AlbumPage just renders full-width here. -->
  <AlbumPage album={openAlbum} {isLoading} />
{:else}
  <!-- Two bands for the whole page: the bar carries identity, the live summary,
       search and the play actions; the chips get a row of their own below it at
       every width. Search moves to the trailing edge on a wide screen where the
       convention expects it. -->
  <PageToolbarV2
    icon={TOOLBAR_ICON[tab]}
    title={TOOLBAR_TITLE[tab]}
    meta={toolbarMeta}
    metaFrom="lg"
    filterRow={isListTab ? chipRow : undefined}
  >
    {#snippet filters()}
      <div
        class="relative w-[10rem] shrink-0 sm:order-last sm:ml-auto sm:w-[clamp(160px,26vw,280px)]"
      >
        <Search class="text-muted-foreground absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2" />
        <input
          type="search"
          placeholder="Search artists, albums, tracks…"
          bind:value={query}
          class="border-border bg-card focus-visible:ring-ring text-nav-sm h-8 w-full rounded-full border pr-2.5 pl-8 outline-none focus-visible:ring-2"
        />
      </div>
      {#if !isListTab && canFilterUnreleased}
        <!-- Leaks/snippets/stems, per the API's release classification. The grids have no chip row,
             so this stays a standalone toggle here; on Tracks the same filter is the `unreleased`
             chip, which composes with the rest. -->
        <FilterChip
          pressed={unreleasedOnly}
          onclick={() => (unreleasedOnly = !unreleasedOnly)}
          title={unreleasedTitle}
          count={unreleasedCount}
        >
          Unreleased
        </FilterChip>
      {/if}
      {#if tab === 'albums'}
        <label class="flex shrink-0 items-center gap-1.5">
          <ArrowUpDown class="text-muted-foreground hidden size-3.5 sm:block" aria-hidden="true" />
          <span class="sr-only">Sort albums by</span>
          <select
            bind:value={albumSort}
            class="border-border bg-card focus-visible:ring-ring text-nav-sm h-8 max-w-[7.5rem] cursor-pointer rounded-full border pr-2 pl-2.5 outline-none focus-visible:ring-2 sm:max-w-none"
          >
            {#each ALBUM_SORT_OPTIONS as option (option.key)}
              <option value={option.key}>{option.label}</option>
            {/each}
          </select>
        </label>
      {/if}
    {/snippet}

    {#snippet actions()}
      {#if isListTab && listView.hasFilters}
        <Button
          variant="ghost"
          size="sm"
          class="text-nav-sm h-8 px-2.5"
          onclick={() => listView.clearFilters()}
        >
          Clear
        </Button>
      {/if}
      {#if isListTab && listView.sorted.length > 0}
        <!-- Play and Shuffle act on the filtered list, so they follow the chips: with none pressed
             this is the whole library, with Spotify Liked pressed it is that collection. -->
        <Button onclick={playList} size="sm" class="h-8 gap-1.5 rounded-full px-2.5 active:scale-95">
          <Play class="size-4" fill="currentColor" />
          <span class="text-nav-sm hidden sm:inline">Play</span>
        </Button>
        <Button
          variant="outline"
          size="sm"
          onclick={shuffleList}
          class="h-8 gap-1.5 rounded-full px-2.5 active:scale-95"
        >
          <Shuffle class="size-4" />
          <span class="text-nav-sm hidden sm:inline">Shuffle</span>
        </Button>
      {/if}
    {/snippet}
  </PageToolbarV2>

  {#if loadError && songs.length === 0 && !isLoading}
    <div class="flex flex-1 flex-col items-center justify-center gap-3 px-6 py-16 text-center">
      <p class="text-destructive text-sm">{loadError}</p>
      <Button onclick={() => void songsStore.loadSongs()}>Retry</Button>
    </div>
  {:else if isListTab}
    <!-- Tracks: the virtualized TrackList, sliced by the chip row above. Selecting a row drives the
         global SongDetailHost (app-shell-mounted), which pushes this view on desktop and is a bottom
         Sheet on mobile. The min-h-0 flex chain keeps TrackList's scroll viewport bounded so
         virtualization works. -->
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      {#if listView.sorted.length === 0 && listView.hasFilters && !isLoading}
        <div class="text-muted-foreground flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
          <ListMusic class="size-10 opacity-40" />
          <p class="text-sm font-medium">No tracks match these filters</p>
          <p class="max-w-xs text-xs">
            Every active chip has to match. Each chip's number is what you'd be left with if you
            pressed it, so a zero shows you which one is the dead end.
          </p>
          <Button variant="outline" size="sm" onclick={() => listView.clearFilters()}>
            Clear filters
          </Button>
        </div>
      {:else}
        <TrackList
          view={listView}
          {isLoading}
          selectedId={tracksSelectedId}
          onSelect={selectTrack}
        />
      {/if}
    </div>
  {:else}
    <ScrollArea bind:viewportRef={gridViewport} class="min-h-0 flex-1">
      <div class="flex flex-col gap-4 px-4 py-4 sm:px-7 sm:py-6">
        {#if browse?.artist && (tab === 'albums' || tab === 'artists')}
          <div class="flex items-center gap-2">
            <span
              class="bg-primary/10 text-primary inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[12px]"
            >
              filtering by <b class="font-semibold">{browseFilterLabel(browse)}</b>
              <button
                type="button"
                onclick={clearArtistFilter}
                aria-label="Clear artist filter"
                class="hover:text-primary/70 inline-flex transition-colors"
              >
                <X class="size-3" />
              </button>
            </span>
          </div>
        {/if}

        {#if tab === 'albums'}
          <LibraryAlbumsGridV2 albums={filteredAlbums} hrefFor={albumHref} {isLoading} statuses={albumStatuses} />
          {#if filteredAlbums.length > 0}
            <div class="text-muted-foreground text-center text-[11px]">
              {filteredAlbums.length.toLocaleString()} album{filteredAlbums.length === 1 ? '' : 's'}
            </div>
          {/if}
        {:else if tab === 'artists'}
          <LibraryArtistsGridV2 groups={filteredArtists} hrefFor={artistHref} {isLoading} bind:mode={artistMode} />
        {/if}
      </div>
    </ScrollArea>
  {/if}
{/if}

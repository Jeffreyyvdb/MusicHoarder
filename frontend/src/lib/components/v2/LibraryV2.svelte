<script lang="ts">
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import {
    ArrowUpDown,
    Disc3,
    FileText,
    Heart,
    ListMusic,
    Music2,
    Music4,
    Play,
    Search,
    Shuffle,
    Users,
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
  import { createTrackListView, SORT_LABELS } from '$lib/track-list-view.svelte';
  import {
    ALBUM_SORT_OPTIONS,
    buildAlbumsFromSongs,
    buildArtistGroups,
    fetchAlbumCanonicalStatuses,
    isAlbumSortKey,
    isMyMusic,
    mergeAlbumsByName,
    songLikedTime,
    sortAlbums,
    type AlbumSortKey,
    type AlbumStatusInfo,
    type AlbumSummary,
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

  // The song-detail panel is now the global SongDetailHost (mounted in the app
  // shell), so Library no longer hosts its own resizable side-pane / bottom
  // Sheet — track selection just drives the shared store. The desktop/mobile
  // form-factor split lives in SongDetailHost.

  type LibraryTab = 'albums' | 'artists' | 'tracks' | 'my-music' | 'liked';
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

  // "Unreleased only": leaks/snippets/stems, as classified by the API. Persisted like the other
  // view toggles, and shared by every tab so switching between albums/artists/tracks keeps the
  // same scope. Only offered when the library actually holds unreleased material.
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

  const browseScoped = $derived(applyBrowseFilter(releaseScoped, browse));

  // Same album under two destination folders (a year or artist-spelling disagreement between its
  // tracks) is one card here — see mergeAlbumsByName.
  // `allAlbums` stays unscoped: it's the drilldown/deep-link resolver, so an ?album= link must
  // still resolve while a filter is on.
  const allAlbums = $derived(mergeAlbumsByName(buildAlbumsFromSongs(builtSongs)));
  const scopedAlbums = $derived(mergeAlbumsByName(buildAlbumsFromSongs(browseScoped)));

  // Provider-link status per album (linked / localOnly / pending) for the grid corner badges.
  // One batch lookup, refreshed when the album set changes.
  let albumStatuses = $state<Map<string, AlbumStatusInfo>>(new Map());
  $effect(() => {
    const pairs = allAlbums.map((a) => ({ artist: a.artist, album: a.title }));
    if (pairs.length === 0) {
      albumStatuses = new Map();
      return;
    }
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

  // Tracks tab: scope by browse filter + local search; the TrackList does its own
  // sorting, so we only narrow by query here.
  const tracksScoped = $derived(browseScoped);

  // My music: what you actually chose, minus what album completion pulled in alongside it. Liking
  // an album-fill track promotes it back in — see isMyMusic.
  const myMusicSongs = $derived(browseScoped.filter(isMyMusic));
  const albumFillCount = $derived(browseScoped.length - myMusicSongs.length);

  // Liked tab: hearted songs, newest like first (the TrackList's 'liked' sort).
  const likedSongs = $derived(releaseScoped.filter((s) => Boolean(s.likedAtUtc)));
  // Same key the TrackList sorts on, so pressing Play starts at the row shown on top.
  function likedQueue(): ApiSong[] {
    return [...likedSongs].sort((a, b) => songLikedTime(b) - songLikedTime(a));
  }
  function likedFallbackArtist(s: ApiSong): string {
    return (s.albumArtist ?? s.artist ?? '').trim() || 'Unknown Artist';
  }
  function playLiked() {
    const queue = likedQueue().map((s) => toPlayerSong(s, likedFallbackArtist(s)));
    if (queue.length > 0) void playerStore.playSong(queue[0], queue, 0);
  }
  function shuffleLiked() {
    const queue = shuffle(likedQueue()).map((s) => toPlayerSong(s, likedFallbackArtist(s)));
    if (queue.length > 0) void playerStore.playSong(queue[0], queue, 0);
  }
  /** Shuffles exactly what the list is showing, filters and sort included. */
  function shuffleList() {
    const queue = shuffle(listView.sorted).map((s) => toPlayerSong(s, likedFallbackArtist(s)));
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
  const tracksView = createTrackListView({
    songs: () => tracksScoped,
    searchQuery: () => query
  });
  const likedView = createTrackListView({
    songs: () => likedSongs,
    searchQuery: () => query,
    initialSortKey: 'liked'
  });
  const myMusicView = createTrackListView({
    songs: () => myMusicSongs,
    searchQuery: () => query
  });
  const listView = $derived(
    tab === 'liked' ? likedView : tab === 'my-music' ? myMusicView : tracksView
  );
  const isListTab = $derived(tab === 'tracks' || tab === 'my-music' || tab === 'liked');

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
  const TOOLBAR_ICON = {
    albums: Disc3,
    artists: Users,
    'my-music': Music4,
    tracks: ListMusic,
    liked: Heart
  };
  const TOOLBAR_TITLE = {
    albums: 'Albums',
    artists: 'Artists',
    'my-music': 'My music',
    tracks: 'All tracks',
    liked: 'Liked Songs'
  };

  // Library-wide pipeline health on the grid tabs; what you're looking at right
  // now on the list tabs (and only "X of Y" once a filter actually narrows it,
  // so an unfiltered list doesn't read "3,525 of 3,525").
  const toolbarMeta = $derived.by(() => {
    if (!isListTab) {
      const enriched = enrichedPct != null ? ` · ${enrichedPct.toFixed(1)}% enriched` : '';
      return `${totalTracks.toLocaleString()} tracks · ${artistCount.toLocaleString()} artists${enriched}`;
    }
    const { sorted, songs, stats, sortKey, sortDir } = listView;
    const noun = tab === 'liked' ? 'song' : 'track';
    const head =
      sorted.length === songs.length
        ? `${sorted.length.toLocaleString()} ${noun}${sorted.length === 1 ? '' : 's'}`
        : `${sorted.length.toLocaleString()} of ${songs.length.toLocaleString()}`;
    // Nothing to total or sort when the list is empty — "0 songs · — · — · by
    // liked ↓" is noise next to the empty state that already explains itself.
    if (sorted.length === 0) return head;
    return `${head} · ${formatFileSize(stats.totalBytes)} · ${formatTotalDuration(stats.totalSec)} · by ${SORT_LABELS[sortKey]} ${sortDir === 'asc' ? '↑' : '↓'}`;
  });
</script>

{#if openAlbum && tab === 'albums'}
  <!-- Album drilldown. Track selection drives the global SongDetailHost (mounted
       in the app shell), which pushes this page on desktop and is a bottom Sheet
       on mobile — so AlbumPage just renders full-width here. -->
  <AlbumPage album={openAlbum} {isLoading} />
{:else}
  <!-- One bar for the whole page: identity, the live summary, every filter, and
       the play actions. Search sits first so it's the visible control at rest on
       a phone (the chips scroll past it), and moves to the trailing edge on a
       wide screen where the convention expects it. -->
  <PageToolbarV2
    icon={TOOLBAR_ICON[tab]}
    title={TOOLBAR_TITLE[tab]}
    meta={toolbarMeta}
    metaFrom="lg"
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
      {#if canFilterUnreleased}
        <!-- Leaks/snippets/stems, per the API's release classification. -->
        <FilterChip
          pressed={unreleasedOnly}
          onclick={() => (unreleasedOnly = !unreleasedOnly)}
          title={unreleasedTitle}
          count={unreleasedCount}
        >
          Unreleased
        </FilterChip>
      {/if}
      {#if isListTab}
        <FilterChip
          pressed={listView.lyricsOnly}
          onclick={() => (listView.lyricsOnly = !listView.lyricsOnly)}
          icon={FileText}
        >
          With lyrics
        </FilterChip>
        {#if listView.spotifyCount > 0}
          <FilterChip
            pressed={listView.spotifyOnly}
            onclick={() => listView.toggleSpotifyOnly()}
            icon={Music2}
            count={listView.spotifyCount}
            title="Only tracks your Spotify liked songs / playlists asked for, newest save first"
          >
            From Spotify
          </FilterChip>
        {/if}
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
      {#if tab === 'liked' && likedSongs.length > 0}
        <Button onclick={playLiked} size="sm" class="h-8 gap-1.5 rounded-full px-2.5 active:scale-95">
          <Play class="size-4" fill="currentColor" />
          <span class="text-nav-sm hidden sm:inline">Play</span>
        </Button>
        <Button
          variant="outline"
          size="sm"
          onclick={shuffleLiked}
          class="h-8 gap-1.5 rounded-full px-2.5 active:scale-95"
        >
          <Shuffle class="size-4" />
          <span class="text-nav-sm hidden sm:inline">Shuffle</span>
        </Button>
      {:else if tab === 'tracks' && listView.sorted.length > 0}
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
  {:else if tab === 'liked'}
    <!-- Liked songs: the shared TrackList sorted by recently-liked. Identity,
         counts and Play/Shuffle live in the toolbar above. The min-h-0 chain
         keeps virtualization bounded. -->
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      {#if likedSongs.length === 0 && !isLoading}
        <div class="text-muted-foreground flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
          <Heart class="size-10 opacity-40" />
          <p class="text-sm font-medium">No liked songs yet</p>
          <p class="max-w-xs text-xs">
            Tap the heart on any track — in the list or the song panel — and it'll show up here,
            newest first.
          </p>
        </div>
      {:else}
        <TrackList
          view={likedView}
          {isLoading}
          selectedId={tracksSelectedId}
          onSelect={selectTrack}
        />
      {/if}
    </div>
  {:else if tab === 'my-music'}
    <!-- My music: same virtualized TrackList as All tracks, narrowed to what you chose. Same min-h-0
         chain so virtualization stays bounded. -->
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      {#if myMusicSongs.length === 0 && albumFillCount > 0 && !isLoading}
        <div class="text-muted-foreground flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
          <Music4 class="size-10 opacity-40" />
          <p class="text-sm font-medium">Nothing here yet</p>
          <p class="max-w-xs text-xs">
            Every track in your library so far arrived through album completion. Like one and it moves
            in here — or check All tracks to see everything.
          </p>
        </div>
      {:else}
        <TrackList
          view={myMusicView}
          {isLoading}
          selectedId={tracksSelectedId}
          onSelect={selectTrack}
        />
      {/if}
    </div>
  {:else if tab === 'tracks'}
    <!-- All tracks: the virtualized TrackList. Selecting a row drives the global
         SongDetailHost (app-shell-mounted), which pushes this view on desktop and
         is a bottom Sheet on mobile. The min-h-0 flex chain keeps TrackList's
         scroll viewport bounded so virtualization works. -->
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      <TrackList
        view={tracksView}
        {isLoading}
        selectedId={tracksSelectedId}
        onSelect={selectTrack}
      />
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

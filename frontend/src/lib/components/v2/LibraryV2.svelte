<script lang="ts">
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { ArrowUpDown, Heart, Music4, Play, Search, Shuffle, X } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import AlbumPage from '$lib/components/file-browser/AlbumPage.svelte';
  import TrackList from '$lib/components/file-browser/TrackList.svelte';
  import LibraryAlbumsGridV2 from '$lib/components/v2/LibraryAlbumsGridV2.svelte';
  import LibraryArtistsGridV2 from '$lib/components/v2/LibraryArtistsGridV2.svelte';
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
  import { formatTotalDuration } from '$lib/formatters';
  import { toPlayerSong } from '$lib/api-client';
  import { breadcrumbStore } from '$lib/stores/breadcrumbs.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { cn, shuffle } from '$lib/utils';

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
  const likedDurationSec = $derived(
    likedSongs.reduce((n, s) => n + (s.durationSeconds ?? 0), 0)
  );

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

  const totalTracks = $derived(releaseScoped.length);
  const artistCount = $derived(artistGroups.length);

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
</script>

{#if openAlbum && tab === 'albums'}
  <!-- Album drilldown. Track selection drives the global SongDetailHost (mounted
       in the app shell), which pushes this page on desktop and is a bottom Sheet
       on mobile — so AlbumPage just renders full-width here. -->
  <AlbumPage album={openAlbum} {isLoading} />
{:else}
  <!-- Slim toolbar: section identity comes from the tab row above; just a quiet
       stat line + a compact search pill so covers start near the top (Apple).
       The stat line only appears once the row is genuinely wide (lg) — below
       that it rendered as "9,025 trac…" while starving the search box. The row
       wraps rather than squeezing: on a phone the search keeps a usable width
       and drops to its own line when the filters leave it no room. -->
  <header class="border-border flex shrink-0 flex-wrap items-center gap-2 border-b px-4 py-2 sm:gap-3 sm:px-7 sm:py-2.5">
    <div class="text-muted-foreground hidden min-w-0 flex-1 truncate text-xs lg:block">
      {totalTracks.toLocaleString()} tracks · {artistCount.toLocaleString()} artists{enrichedPct !=
      null
        ? ` · ${enrichedPct.toFixed(1)}% enriched`
        : ''}
    </div>
    {#if canFilterUnreleased}
      <!-- Leaks/snippets/stems, per the API's release classification. -->
      <button
        type="button"
        onclick={() => (unreleasedOnly = !unreleasedOnly)}
        aria-pressed={unreleasedOnly}
        title={unreleasedTitle}
        class={cn(
          'focus-visible:ring-ring h-8 shrink-0 rounded-full border px-3 text-[12.5px] whitespace-nowrap transition-colors outline-none focus-visible:ring-2',
          unreleasedOnly
            ? 'border-primary bg-primary/10 text-primary font-medium'
            : 'border-border bg-card text-muted-foreground hover:text-foreground'
        )}
      >
        Unreleased
        <span class="tabular-nums opacity-60">{unreleasedCount.toLocaleString()}</span>
      </button>
    {/if}
    {#if tab === 'albums'}
      <label class="flex shrink-0 items-center gap-1.5">
        <ArrowUpDown class="text-muted-foreground hidden size-3.5 sm:block" aria-hidden="true" />
        <span class="sr-only">Sort albums by</span>
        <select
          bind:value={albumSort}
          class="border-border bg-card focus-visible:ring-ring h-8 max-w-[7.5rem] cursor-pointer rounded-full border pr-2 pl-2.5 text-[12.5px] outline-none focus-visible:ring-2 sm:max-w-none"
        >
          {#each ALBUM_SORT_OPTIONS as option (option.key)}
            <option value={option.key}>{option.label}</option>
          {/each}
        </select>
      </label>
    {/if}
    <div class="relative min-w-[10rem] flex-1 lg:w-[clamp(160px,32vw,280px)] lg:flex-none">
      <Search class="text-muted-foreground absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2" />
      <input
        type="search"
        placeholder="Search artists, albums, tracks…"
        bind:value={query}
        class="border-border bg-card focus-visible:ring-ring h-8 w-full rounded-full border pr-2.5 pl-8 text-[12.5px] outline-none focus-visible:ring-2"
      />
    </div>
  </header>

  {#if loadError && songs.length === 0 && !isLoading}
    <div class="flex flex-1 flex-col items-center justify-center gap-3 px-6 py-16 text-center">
      <p class="text-destructive text-sm">{loadError}</p>
      <Button onclick={() => void songsStore.loadSongs()}>Retry</Button>
    </div>
  {:else if tab === 'liked'}
    <!-- Liked songs: Spotify-style hero + the shared TrackList sorted by
         recently-liked. The min-h-0 chain keeps virtualization bounded. -->
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      <div
        class="border-border flex shrink-0 items-center gap-4 border-b bg-gradient-to-br from-primary/15 via-primary/5 to-transparent px-4 py-5 sm:gap-5 sm:px-7 sm:py-6"
      >
        <div
          class="grid size-16 shrink-0 place-items-center rounded-xl bg-gradient-to-br from-primary to-primary/60 text-primary-foreground shadow-lg sm:size-20"
        >
          <Heart class="size-7 sm:size-9" fill="currentColor" />
        </div>
        <div class="min-w-0 flex-1">
          <p class="text-muted-foreground text-[11px] font-medium tracking-wider uppercase">Playlist</p>
          <h1 class="truncate text-2xl font-bold tracking-tight sm:text-3xl">Liked Songs</h1>
          <p class="text-muted-foreground mt-1 text-xs sm:text-sm">
            {likedSongs.length.toLocaleString()} song{likedSongs.length === 1 ? '' : 's'}
            {#if likedSongs.length > 0}
              · <span class="font-mono">{formatTotalDuration(likedDurationSec)}</span>
            {/if}
          </p>
        </div>
        {#if likedSongs.length > 0}
          <div class="flex shrink-0 items-center gap-2">
            <Button onclick={playLiked} class="rounded-full active:scale-95">
              <Play class="size-4" fill="currentColor" />
              <span class="hidden sm:inline">Play</span>
            </Button>
            <Button variant="outline" onclick={shuffleLiked} class="rounded-full active:scale-95">
              <Shuffle class="size-4" />
              <span class="hidden sm:inline">Shuffle</span>
            </Button>
          </div>
        {/if}
      </div>
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
          songs={likedSongs}
          searchQuery={query}
          {isLoading}
          selectedId={tracksSelectedId}
          onSelect={selectTrack}
          hideHeading
          initialSortKey="liked"
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
          songs={myMusicSongs}
          searchQuery={query}
          {isLoading}
          selectedId={tracksSelectedId}
          onSelect={selectTrack}
          hideHeading
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
        songs={tracksScoped}
        searchQuery={query}
        {isLoading}
        selectedId={tracksSelectedId}
        onSelect={selectTrack}
        hideHeading
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

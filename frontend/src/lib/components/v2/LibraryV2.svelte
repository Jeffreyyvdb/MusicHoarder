<script lang="ts">
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { Search, X } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import AlbumPage from '$lib/components/file-browser/AlbumPage.svelte';
  import TrackList from '$lib/components/file-browser/TrackList.svelte';
  import LibraryAlbumsGridV2 from '$lib/components/v2/LibraryAlbumsGridV2.svelte';
  import LibraryArtistsGridV2 from '$lib/components/v2/LibraryArtistsGridV2.svelte';
  import {
    buildAlbumsFromSongs,
    buildArtistGroups,
    fetchAlbumCanonicalStatuses,
    type AlbumStatusInfo,
    type AlbumSummary,
    type ApiSong,
    type GroupSummary
  } from '$lib/api-client';
  import { isBuiltSong } from '$lib/album-sections';
  import { parseBrowseFilter, applyBrowseFilter, browseFilterLabel } from '$lib/browse-filter';
  import { breadcrumbStore } from '$lib/stores/breadcrumbs.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';

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
  const browseScoped = $derived(applyBrowseFilter(builtSongs, browse));

  const allAlbums = $derived(buildAlbumsFromSongs(builtSongs));
  const scopedAlbums = $derived(buildAlbumsFromSongs(browseScoped));

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

  const filteredAlbums = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return scopedAlbums;
    return scopedAlbums.filter((a) => albumMatchesQuery(a, q));
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
    buildArtistGroups(builtSongs, { primaryOnly: artistMode === 'primary' })
  );
  const filteredArtists = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return artistGroups;
    return artistGroups.filter((g) => g.label.toLowerCase().includes(q));
  });

  // Tracks tab: scope by browse filter + local search; the TrackList does its own
  // sort/format filtering, so we only narrow by query here.
  const tracksScoped = $derived(browseScoped);

  const totalTracks = $derived(builtSongs.length);
  const artistCount = $derived(artistGroups.length);

  // ── album drilldown (reuses AlbumPage + TrackPanel) ─────────────────────────
  const openAlbum = $derived.by(() => {
    if (!albumKey) return null;
    return (
      filteredAlbums.find((a) => a.key === albumKey) ??
      allAlbums.find((a) => a.key === albumKey) ??
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

  const enrichedPct = $derived.by(() => {
    if (totalTracks === 0) return null;
    const matched = builtSongs.length;
    return (matched / Math.max(1, songs.length)) * 100;
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
  <!-- Header -->
  <header
    class="border-border flex shrink-0 flex-col gap-3 border-b px-4 py-4 sm:flex-row sm:items-end sm:justify-between sm:gap-4 sm:px-7 sm:py-5"
  >
    <div class="min-w-0">
      <div class="text-muted-foreground font-mono text-[10px] tracking-[0.12em] uppercase">
        {totalTracks.toLocaleString()} tracks · {artistCount.toLocaleString()} artists{enrichedPct !=
        null
          ? ` · ${enrichedPct.toFixed(1)}% enriched`
          : ''}
      </div>
      <h1 class="mt-1 text-2xl font-semibold tracking-tight">Library</h1>
      <p class="text-muted-foreground mt-1 hidden max-w-2xl text-xs sm:block">
        The clean output. Click any album to drill in — every track has its own enrichment timeline
        showing which provider supplied each field.
      </p>
    </div>
    <div class="flex shrink-0 items-center gap-2">
      <div class="relative w-full sm:w-[clamp(180px,28vw,280px)]">
        <Search class="text-muted-foreground absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2" />
        <input
          type="search"
          placeholder="Search artists, albums, tracks…"
          bind:value={query}
          class="border-border bg-card focus-visible:ring-ring h-8 w-full rounded-md border pr-2.5 pl-8 text-[12.5px] outline-none focus-visible:ring-2"
        />
      </div>
    </div>
  </header>

  {#if tab === 'tracks'}
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

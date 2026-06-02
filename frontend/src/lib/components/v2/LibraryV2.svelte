<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { Search, X } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as Resizable from '$lib/components/ui/resizable';
  import * as Sheet from '$lib/components/ui/sheet';
  import AlbumPage from '$lib/components/file-browser/AlbumPage.svelte';
  import TrackList from '$lib/components/file-browser/TrackList.svelte';
  import TrackPanel from '$lib/components/file-browser/TrackPanel.svelte';
  import LibraryAlbumsGridV2 from '$lib/components/v2/LibraryAlbumsGridV2.svelte';
  import LibraryArtistsGridV2 from '$lib/components/v2/LibraryArtistsGridV2.svelte';
  import {
    buildAlbumsFromSongs,
    buildArtistGroups,
    fetchSongs,
    openProgressStream,
    type AlbumSummary,
    type ApiSong,
    type GroupSummary,
    type ProgressSnapshot
  } from '$lib/api-client';
  import { isBuiltSong } from '$lib/album-sections';
  import { parseBrowseFilter, applyBrowseFilter, browseFilterLabel } from '$lib/browse-filter';
  import { breadcrumbStore } from '$lib/stores/breadcrumbs.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';

  // Behaviour-only: on mobile the album-drilldown / tracks-tab detail panel
  // moves from a desktop resizable side-pane into a bottom Sheet.
  const isMobile = new IsMobile();

  type LibraryTab = 'albums' | 'artists' | 'tracks';
  type Props = {
    /** Which sub-view this route hosts. The sub-nav navigates between routes. */
    tab: LibraryTab;
  };
  const { tab }: Props = $props();

  // ── data layer (reuses the existing api-client + album-sections) ───────────
  let songs = $state<ApiSong[]>([]);
  let isLoading = $state(true);
  let isMountedRef = true;

  async function loadSongs(opts?: { silent?: boolean }) {
    try {
      if (!opts?.silent) isLoading = true;
      const loaded = await fetchSongs();
      if (!isMountedRef) return;
      songs = loaded;
    } finally {
      if (isMountedRef && !opts?.silent) isLoading = false;
    }
  }

  // ── Live refresh (mirror the v1 /library behaviour) ─────────────────────────
  let liveCleanup: (() => void) | null = null;
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;
  let lastBuilt = -1;
  let sawActive = false;

  function scheduleSongRefresh() {
    if (refreshTimer) return;
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      void loadSongs({ silent: true });
    }, 3000);
  }

  function stopLive() {
    if (refreshTimer) {
      clearTimeout(refreshTimer);
      refreshTimer = null;
    }
    if (liveCleanup) {
      liveCleanup();
      liveCleanup = null;
    }
  }

  function startLive() {
    if (liveCleanup) return;
    lastBuilt = -1;
    sawActive = false;
    liveCleanup = openProgressStream(
      (snap: ProgressSnapshot) => {
        if (snap.built !== lastBuilt) {
          lastBuilt = snap.built;
          sawActive = true;
          scheduleSongRefresh();
        }
        if (snap.isComplete && sawActive) {
          sawActive = false;
          scheduleSongRefresh();
        }
      },
      () => {
        liveCleanup = null;
        if (sawActive) {
          sawActive = false;
          void loadSongs({ silent: true });
        }
      }
    );
  }

  $effect(() => {
    isMountedRef = true;
    void loadSongs();
    startLive();
    return () => {
      isMountedRef = false;
      stopLive();
    };
  });

  // ── URL state ───────────────────────────────────────────────────────────────
  const albumKey = $derived(page.url.searchParams.get('album'));
  const trackParam = $derived(page.url.searchParams.get('track'));
  const songParam = $derived(page.url.searchParams.get('song'));
  const browse = $derived(parseBrowseFilter(page.url.searchParams));

  // Local search box (matches the prototype's header search, not a URL param so
  // the v1 routes stay untouched).
  let query = $state('');

  // ── derivations (only clean/built songs make up the library) ────────────────
  const builtSongs = $derived(songs.filter(isBuiltSong));
  const browseScoped = $derived(applyBrowseFilter(builtSongs, browse));

  const allAlbums = $derived(buildAlbumsFromSongs(builtSongs));
  const scopedAlbums = $derived(buildAlbumsFromSongs(browseScoped));

  function albumMatchesQuery(a: AlbumSummary, q: string): boolean {
    return a.title.toLowerCase().includes(q) || a.artist.toLowerCase().includes(q);
  }

  const filteredAlbums = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return scopedAlbums;
    return scopedAlbums.filter((a) => albumMatchesQuery(a, q));
  });

  const artistGroups = $derived(buildArtistGroups(builtSongs));
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

  // Deep-link via ?song= — resolve the owning album and open both panels.
  let appliedSongDeepLink: number | null = null;
  $effect(() => {
    if (isLoading) return;
    if (!songParam) return;
    const songId = Number.parseInt(songParam, 10);
    if (!Number.isFinite(songId) || appliedSongDeepLink === songId) return;
    const owningAlbum = allAlbums.find((a) => a.songs.some((s) => s.id === songId));
    if (!owningAlbum) return;
    appliedSongDeepLink = songId;
    const url = new URL(page.url);
    url.searchParams.delete('song');
    url.searchParams.set('album', owningAlbum.key);
    url.searchParams.set('track', String(songId));
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  });

  const selectedTrack = $derived.by(() => {
    if (!openAlbum || !trackParam) return null;
    const id = Number.parseInt(trackParam, 10);
    if (!Number.isFinite(id)) return null;
    const idx = openAlbum.songs.findIndex((s) => s.id === id);
    if (idx < 0) return null;
    return { song: openAlbum.songs[idx], index: idx };
  });
  const trackPanelOpen = $derived(!!selectedTrack && !!openAlbum);

  function closeTrack() {
    const url = new URL(page.url);
    url.searchParams.delete('track');
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  }

  // Tracks tab selection (no album context) — open the standalone track panel.
  const tracksSelectedId = $derived(trackParam ? Number.parseInt(trackParam, 10) : null);
  const tracksSelected = $derived.by(() => {
    if (tab !== 'tracks' || tracksSelectedId == null || !Number.isFinite(tracksSelectedId)) {
      return null;
    }
    const album = allAlbums.find((a) => a.songs.some((s) => s.id === tracksSelectedId));
    if (!album) return null;
    const index = album.songs.findIndex((s) => s.id === tracksSelectedId);
    return { album, song: album.songs[index], index };
  });
  const tracksPanelOpen = $derived(!!tracksSelected);

  function selectTrack(song: ApiSong) {
    const url = new URL(page.url);
    if (tracksSelectedId === song.id) url.searchParams.delete('track');
    else url.searchParams.set('track', String(song.id));
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
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
  <!-- Album drilldown reuses the existing AlbumPage (+ TrackPanel). On mobile the
       detail pane becomes a bottom Sheet; the side-pane and Sheet branches are
       mutually exclusive so TrackPanel.registerPanel() mounts exactly once. -->
  {#if isMobile.current}
    <AlbumPage album={openAlbum} {isLoading} />
    <Sheet.Root open={trackPanelOpen} onOpenChange={(open) => !open && closeTrack()}>
      <Sheet.Content side="bottom" class="h-[88vh] gap-0 p-0 [&>button]:hidden">
        <Sheet.Title class="sr-only">Track details</Sheet.Title>
        <Sheet.Description class="sr-only">
          View track metadata, lyrics, fingerprint, and enrichment sources
        </Sheet.Description>
        {#if openAlbum && selectedTrack}
          <TrackPanel
            album={openAlbum}
            song={selectedTrack.song}
            trackIndex={selectedTrack.index}
            onClose={closeTrack}
            onResetEnrichment={() => void loadSongs()}
            timelineHref={`/track/${selectedTrack.song.id}`}
          />
        {/if}
      </Sheet.Content>
    </Sheet.Root>
  {:else if !trackPanelOpen}
    <AlbumPage album={openAlbum} {isLoading} />
  {:else}
    <Resizable.PaneGroup id="library-v2-album-panels" direction="horizontal" class="min-h-0 flex-1">
      <Resizable.Pane id="library-v2-album-main" order={1} defaultSize={68} class="flex min-h-0 flex-col">
        <AlbumPage album={openAlbum} {isLoading} />
      </Resizable.Pane>
      <Resizable.Handle />
      <Resizable.Pane id="library-v2-album-details" order={2} defaultSize={32} minSize={28} maxSize={45}>
        {#if openAlbum && selectedTrack}
          <TrackPanel
            album={openAlbum}
            song={selectedTrack.song}
            trackIndex={selectedTrack.index}
            onClose={closeTrack}
            onResetEnrichment={() => void loadSongs()}
            timelineHref={`/track/${selectedTrack.song.id}`}
          />
        {/if}
      </Resizable.Pane>
    </Resizable.PaneGroup>
  {/if}
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
    <!-- All tracks reuses the existing virtualized TrackList + TrackPanel. On
         mobile the detail pane becomes a bottom Sheet; the side-pane and Sheet
         branches are mutually exclusive so TrackPanel.registerPanel() mounts
         exactly once. The mobile wrapper keeps the min-h-0 flex chain so
         TrackList's scroll viewport stays bounded and virtualization works. -->
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      {#if isMobile.current}
        <div class="flex min-h-0 flex-1 flex-col">
          <TrackList
            songs={tracksScoped}
            searchQuery={query}
            {isLoading}
            selectedId={tracksSelectedId}
            onSelect={selectTrack}
            hideHeading
          />
        </div>
        <Sheet.Root open={tracksPanelOpen} onOpenChange={(open) => !open && closeTrack()}>
          <Sheet.Content side="bottom" class="h-[88vh] gap-0 p-0 [&>button]:hidden">
            <Sheet.Title class="sr-only">Track details</Sheet.Title>
            <Sheet.Description class="sr-only">
              View track metadata, lyrics, fingerprint, and enrichment sources
            </Sheet.Description>
            {#if tracksSelected}
              <TrackPanel
                album={tracksSelected.album}
                song={tracksSelected.song}
                trackIndex={tracksSelected.index}
                onClose={closeTrack}
                onResetEnrichment={() => void loadSongs()}
                timelineHref={`/track/${tracksSelected.song.id}`}
              />
            {/if}
          </Sheet.Content>
        </Sheet.Root>
      {:else}
        <Resizable.PaneGroup id="library-v2-tracks-panels" direction="horizontal" class="min-h-0 flex-1">
          <Resizable.Pane
            id="library-v2-tracks-main"
            order={1}
            defaultSize={tracksPanelOpen ? 68 : 100}
            class="flex min-h-0 flex-col"
          >
            <TrackList
              songs={tracksScoped}
              searchQuery={query}
              {isLoading}
              selectedId={tracksSelectedId}
              onSelect={selectTrack}
              hideHeading
            />
          </Resizable.Pane>
          {#if tracksPanelOpen}
            <Resizable.Handle />
            <Resizable.Pane id="library-v2-tracks-details" order={2} defaultSize={32} minSize={28} maxSize={45}>
              {#if tracksSelected}
                <TrackPanel
                  album={tracksSelected.album}
                  song={tracksSelected.song}
                  trackIndex={tracksSelected.index}
                  onClose={closeTrack}
                  onResetEnrichment={() => void loadSongs()}
                  timelineHref={`/track/${tracksSelected.song.id}`}
                />
              {/if}
            </Resizable.Pane>
          {/if}
        </Resizable.PaneGroup>
      {/if}
    </div>
  {:else}
    <ScrollArea class="min-h-0 flex-1">
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
          <LibraryAlbumsGridV2 albums={filteredAlbums} hrefFor={albumHref} {isLoading} />
          {#if filteredAlbums.length > 0}
            <div class="text-muted-foreground text-center text-[11px]">
              {filteredAlbums.length.toLocaleString()} album{filteredAlbums.length === 1 ? '' : 's'}
            </div>
          {/if}
        {:else if tab === 'artists'}
          <LibraryArtistsGridV2 groups={filteredArtists} hrefFor={artistHref} {isLoading} />
        {/if}
      </div>
    </ScrollArea>
  {/if}
{/if}

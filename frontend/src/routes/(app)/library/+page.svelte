<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import * as Resizable from '$lib/components/ui/resizable';
  import * as Sheet from '$lib/components/ui/sheet';
  import Gallery from '$lib/components/file-browser/Gallery.svelte';
  import AlbumPage from '$lib/components/file-browser/AlbumPage.svelte';
  import TrackPanel from '$lib/components/file-browser/TrackPanel.svelte';
  import {
    buildAlbumsFromSongs,
    fetchSongs,
    openProgressStream,
    type ApiSong,
    type ProgressSnapshot
  } from '$lib/api-client';
  import { applySectionFilter, isBuiltSong, isSectionId } from '$lib/album-sections';
  import {
    parseBrowseFilter,
    applyBrowseFilter,
    browseFilterLabel,
    browseFilterClearHref,
    browseFilterKind
  } from '$lib/browse-filter';
  import { breadcrumbStore } from '$lib/stores/breadcrumbs.svelte';
  import { cn } from '$lib/utils';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { uiVersion } from '$lib/stores/ui-version.svelte';
  import LibraryV2 from '$lib/components/v2/LibraryV2.svelte';

  // The v1 `{:else}` branch below still swaps a desktop side-pane for a mobile
  // bottom Sheet, so it keeps using `isMobile`.
  const isMobile = new IsMobile();
  // v2 now renders the redesigned Library shell in-place at every width (it does
  // its own responsive layout); v1 keeps the existing markup.
  const showV2 = $derived(uiVersion.isV2);
  let songs = $state<ApiSong[]>([]);
  let apiError = $state<string | null>(null);
  let isLoading = $state(true);
  let isMountedRef = true;

  const searchQuery = $derived(page.url.searchParams.get('q') ?? '');
  const albumKey = $derived(page.url.searchParams.get('album'));
  const sectionParam = $derived(page.url.searchParams.get('section'));
  const section = $derived(isSectionId(sectionParam) ? sectionParam : 'lib');
  const trackParam = $derived(page.url.searchParams.get('track'));
  const songParam = $derived(page.url.searchParams.get('song'));
  const isSourceView = $derived(page.url.searchParams.get('view') === 'source');

  // `silent` keeps the current grid on screen during background refreshes (no loading flash).
  async function loadSongs(opts?: { silent?: boolean }) {
    try {
      if (!opts?.silent) isLoading = true;
      const loaded = await fetchSongs();
      if (!isMountedRef) return;
      songs = loaded;
      apiError = null;
    } catch (err) {
      if (!isMountedRef) return;
      if (!opts?.silent) {
        songs = [];
        apiError = err instanceof Error ? err.message : 'Unknown API error';
      }
    } finally {
      if (isMountedRef && !opts?.silent) isLoading = false;
    }
  }

  // ── Live refresh ────────────────────────────────────────────────────────────
  // The grid is a one-shot snapshot, so albums that finish building while the page
  // is open never appear without a manual reload. Subscribe to the pipeline progress
  // stream and re-fetch (debounced) whenever the built count changes, plus once when
  // the run completes — mirroring the directory view's live behavior.
  let liveCleanup: (() => void) | null = null;
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;
  let lastBuilt = -1;
  let sawActive = false;

  function scheduleSongRefresh() {
    if (refreshTimer) return; // debounce: at most one refresh per window
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
        // A growing built count means new tracks landed in the destination → refresh.
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
        // Server closes the stream when the job ends — finalize with one last refresh.
        liveCleanup = null;
        if (sawActive) {
          sawActive = false;
          void loadSongs({ silent: true });
        }
      }
    );
  }

  $effect(() => {
    // When the v2 Library owns the page it does its own fetching + live refresh;
    // skip the v1 data layer to avoid duplicate work and breadcrumb fights.
    if (showV2) return;
    isMountedRef = true;
    void loadSongs();
    startLive();
    return () => {
      isMountedRef = false;
      stopLive();
    };
  });

  const browse = $derived(parseBrowseFilter(page.url.searchParams));
  const browseFilteredSongs = $derived(applyBrowseFilter(songs, browse));
  const galleryBrowseFilter = $derived(
    browse
      ? {
          label: browseFilterLabel(browse),
          clearHref: browseFilterClearHref(browse),
          kind: browseFilterKind(browse)
        }
      : null
  );

  // Default Library shows only clean (built) albums; the Ingest "Source folder" view
  // (?view=source) shows the raw scan. Diagnostic sections (recent/dupes/missing/queue)
  // always operate on the full set so problem tracks still surface.
  const scopedSongs = $derived(
    !isSourceView && section === 'lib'
      ? browseFilteredSongs.filter(isBuiltSong)
      : browseFilteredSongs
  );
  const sectionFilteredSongs = $derived(applySectionFilter(scopedSongs, section));
  const allAlbumsForLookup = $derived(buildAlbumsFromSongs(songs));
  const filteredAlbums = $derived(buildAlbumsFromSongs(sectionFilteredSongs));
  const openAlbum = $derived(
    albumKey
      ? (filteredAlbums.find((a) => a.key === albumKey) ??
          allAlbumsForLookup.find((a) => a.key === albumKey) ??
          null)
      : null
  );

  $effect(() => {
    if (showV2) return;
    if (openAlbum) {
      breadcrumbStore.setAlbum({ artist: openAlbum.artist, title: openAlbum.title });
    } else {
      breadcrumbStore.clear();
    }
    return () => breadcrumbStore.clear();
  });

  // Deep-link via ?song= — find the song's album and open both panels.
  let appliedSongDeepLink: number | null = null;
  $effect(() => {
    if (isLoading || apiError) return;
    if (!songParam) return;
    const songId = Number.parseInt(songParam, 10);
    if (!Number.isFinite(songId) || appliedSongDeepLink === songId) return;
    const song = songs.find((s) => s.id === songId);
    if (!song) return;
    const owningAlbum = allAlbumsForLookup.find((a) => a.songs.some((s) => s.id === songId));
    if (!owningAlbum) return;
    appliedSongDeepLink = songId;
    const url = new URL(page.url);
    url.searchParams.delete('song');
    url.searchParams.set('album', owningAlbum.key);
    url.searchParams.set('track', String(song.id));
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  });

  // Selected track within the open album, matched by unique song id.
  const selectedTrack = $derived.by(() => {
    if (!openAlbum || !trackParam) return null;
    const id = Number.parseInt(trackParam, 10);
    if (!Number.isFinite(id)) return null;
    const idx = openAlbum.songs.findIndex((s) => s.id === id);
    if (idx < 0) return null;
    return { song: openAlbum.songs[idx], index: idx };
  });

  function closeTrack() {
    const url = new URL(page.url);
    url.searchParams.delete('track');
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  }

  const trackPanelOpen = $derived(!!selectedTrack && !!openAlbum);
</script>

{#if showV2}
  <LibraryV2 tab="albums" />
{:else}
<div class={cn('flex min-h-0 flex-1 flex-col overflow-hidden')}>
  {#if apiError}
    <div class="border-border bg-card/30 text-destructive border-b px-4 py-2 text-xs md:px-6">
      {apiError}
    </div>
  {/if}

  {#snippet mainContent()}
    {#if openAlbum && albumKey}
      <AlbumPage album={openAlbum} {isLoading} />
    {:else}
      <Gallery
        songs={sectionFilteredSongs}
        {section}
        {searchQuery}
        {isLoading}
        {isSourceView}
        browseFilter={galleryBrowseFilter}
      />
    {/if}
  {/snippet}

  <!-- One responsive tree: the same Gallery/AlbumPage render at every width. The
       track panel is the only width-dependent shell — a resizable side pane on
       desktop, a bottom Sheet on mobile (its TrackPanel content is shared). -->
  {#if !isMobile.current && trackPanelOpen}
    <Resizable.PaneGroup id="library-albums-panels" direction="horizontal" class="min-h-0 flex-1">
      <Resizable.Pane id="library-albums-main" order={1} defaultSize={70} class="flex min-h-0 flex-col">
        {@render mainContent()}
      </Resizable.Pane>
      <Resizable.Handle />
      <Resizable.Pane
        id="library-albums-details"
        order={2}
        defaultSize={32}
        minSize={28}
        maxSize={45}
      >
        {#if openAlbum && selectedTrack}
          <TrackPanel
            album={openAlbum}
            song={selectedTrack.song}
            trackIndex={selectedTrack.index}
            onClose={closeTrack}
            onResetEnrichment={() => void loadSongs()}
          />
        {/if}
      </Resizable.Pane>
    </Resizable.PaneGroup>
  {:else}
    {@render mainContent()}
  {/if}

  {#if isMobile.current}
    <Sheet.Root open={trackPanelOpen} onOpenChange={(open) => !open && closeTrack()}>
      <Sheet.Content side="bottom" class="h-[88vh] gap-0 p-0 data-[side=bottom]:h-[88vh] [&>button]:hidden">
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
          />
        {/if}
      </Sheet.Content>
    </Sheet.Root>
  {/if}
</div>
{/if}

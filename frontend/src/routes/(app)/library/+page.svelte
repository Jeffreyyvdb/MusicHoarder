<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import * as Resizable from '$lib/components/ui/resizable';
  import * as Sheet from '$lib/components/ui/sheet';
  import Gallery from '$lib/components/file-browser/Gallery.svelte';
  import AlbumPage from '$lib/components/file-browser/AlbumPage.svelte';
  import TrackPanel from '$lib/components/file-browser/TrackPanel.svelte';
  import MobileLibrary from '$lib/components/mobile/MobileLibrary.svelte';
  import MobileAlbum from '$lib/components/mobile/MobileAlbum.svelte';
  import { buildAlbumsFromSongs, fetchSongs, type ApiSong } from '$lib/api-client';
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

  const isMobile = new IsMobile();
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

  async function loadSongs() {
    try {
      isLoading = true;
      const loaded = await fetchSongs();
      if (!isMountedRef) return;
      songs = loaded;
      apiError = null;
    } catch (err) {
      if (!isMountedRef) return;
      songs = [];
      apiError = err instanceof Error ? err.message : 'Unknown API error';
    } finally {
      if (isMountedRef) isLoading = false;
    }
  }

  $effect(() => {
    isMountedRef = true;
    void loadSongs();
    return () => {
      isMountedRef = false;
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

<div class={cn('flex min-h-0 flex-1 flex-col overflow-hidden')}>
  {#if apiError}
    <div class="border-border bg-card/30 text-destructive border-b px-4 py-2 text-xs md:px-6">
      {apiError}
    </div>
  {/if}

  {#if isMobile.current}
    {#if openAlbum && albumKey}
      <MobileAlbum {songs} {albumKey} />
    {:else}
      <MobileLibrary songs={scopedSongs} {section} {searchQuery} {isLoading} {isSourceView} />
    {/if}
  {:else if !trackPanelOpen}
    {#if openAlbum && albumKey}
      <AlbumPage {songs} {albumKey} {isLoading} />
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
  {:else}
    <Resizable.PaneGroup id="library-albums-panels" direction="horizontal" class="min-h-0 flex-1">
      <Resizable.Pane id="library-albums-main" order={1} defaultSize={70}>
        {#if openAlbum && albumKey}
          <AlbumPage {songs} {albumKey} {isLoading} />
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

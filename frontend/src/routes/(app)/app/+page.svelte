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
  import { applySectionFilter, isSectionId } from '$lib/album-sections';
  import { breadcrumbStore } from '$lib/stores/breadcrumbs.svelte';
  import { cn } from '$lib/utils';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { playerStore } from '$lib/stores/player.svelte';

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

  const sectionFilteredSongs = $derived(applySectionFilter(songs, section));
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
    const trackNumber = owningAlbum.songs.findIndex((s) => s.id === songId) + 1;
    const url = new URL(page.url);
    url.searchParams.delete('song');
    url.searchParams.set('album', owningAlbum.key);
    url.searchParams.set('track', String(song.trackNumber ?? trackNumber));
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  });

  // Selected track within the open album (matches by trackNumber, falls back to index+1).
  const selectedTrack = $derived.by(() => {
    if (!openAlbum || !trackParam) return null;
    const n = Number.parseInt(trackParam, 10);
    if (!Number.isFinite(n)) return null;
    const byNumber = openAlbum.songs.find((s) => (s.trackNumber ?? null) === n);
    if (byNumber) {
      const idx = openAlbum.songs.indexOf(byNumber);
      return { song: byNumber, index: idx };
    }
    if (n >= 1 && n <= openAlbum.songs.length) {
      return { song: openAlbum.songs[n - 1], index: n - 1 };
    }
    return null;
  });

  function closeTrack() {
    const url = new URL(page.url);
    url.searchParams.delete('track');
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  }

  // React to MiniPlayer's "show details" handshake — open the track panel
  // for the currently-playing song.
  let prevDetailsRequestId = 0;
  $effect(() => {
    const reqId = playerStore.detailsRequestId;
    if (reqId === prevDetailsRequestId) return;
    prevDetailsRequestId = reqId;
    const playing = playerStore.currentSong;
    if (!playing) return;
    const owningAlbum = allAlbumsForLookup.find((a) => a.songs.some((s) => s.id === playing.id));
    if (!owningAlbum) return;
    const idx = owningAlbum.songs.findIndex((s) => s.id === playing.id);
    const song = owningAlbum.songs[idx];
    if (!song) return;
    const url = new URL(page.url);
    url.searchParams.set('album', owningAlbum.key);
    url.searchParams.set('track', String(song.trackNumber ?? idx + 1));
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  });

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
      <MobileLibrary {songs} {section} {searchQuery} {isLoading} />
    {/if}
  {:else if !trackPanelOpen}
    {#if openAlbum && albumKey}
      <AlbumPage {songs} {albumKey} {isLoading} />
    {:else}
      <Gallery songs={sectionFilteredSongs} {section} {searchQuery} {isLoading} />
    {/if}
  {:else}
    <Resizable.PaneGroup id="library-albums-panels" direction="horizontal" class="min-h-0 flex-1">
      <Resizable.Pane id="library-albums-main" order={1} defaultSize={70}>
        {#if openAlbum && albumKey}
          <AlbumPage {songs} {albumKey} {isLoading} />
        {:else}
          <Gallery songs={sectionFilteredSongs} {section} {searchQuery} {isLoading} />
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

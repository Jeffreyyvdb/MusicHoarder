<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import * as Resizable from '$lib/components/ui/resizable';
  import * as Sheet from '$lib/components/ui/sheet';
  import TrackList from '$lib/components/file-browser/TrackList.svelte';
  import TrackPanel from '$lib/components/file-browser/TrackPanel.svelte';
  import { buildAlbumsFromSongs, fetchSongs, type ApiSong } from '$lib/api-client';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { uiVersion } from '$lib/stores/ui-version.svelte';
  import LibraryV2 from '$lib/components/v2/LibraryV2.svelte';

  const isMobile = new IsMobile();
  // v2 desktop renders the redesigned Library shell (All tracks tab) in-place;
  // v1 (and v2 on mobile) keeps the existing resizable TrackList layout.
  const showV2 = $derived(uiVersion.isV2 && !isMobile.current);

  let songs = $state<ApiSong[]>([]);
  let isLoading = $state(true);
  let isMountedRef = true;

  async function loadSongs() {
    try {
      isLoading = true;
      const loaded = await fetchSongs();
      if (!isMountedRef) return;
      songs = loaded;
    } finally {
      if (isMountedRef) isLoading = false;
    }
  }

  $effect(() => {
    // v2 owns its own fetching; skip the v1 data layer when it's showing.
    if (showV2) return;
    isMountedRef = true;
    void loadSongs();
    return () => {
      isMountedRef = false;
    };
  });

  const searchQuery = $derived(page.url.searchParams.get('q') ?? '');
  const trackParam = $derived(page.url.searchParams.get('track'));
  const selectedId = $derived(trackParam ? Number.parseInt(trackParam, 10) : null);

  const albums = $derived(buildAlbumsFromSongs(songs));

  // Resolve the owning album + the song's index within it, for the detail panel.
  const selected = $derived.by(() => {
    if (selectedId == null || !Number.isFinite(selectedId)) return null;
    const album = albums.find((a) => a.songs.some((s) => s.id === selectedId));
    if (!album) return null;
    const index = album.songs.findIndex((s) => s.id === selectedId);
    return { album, song: album.songs[index], index };
  });

  const panelOpen = $derived(!!selected);

  function selectTrack(song: ApiSong) {
    const url = new URL(page.url);
    if (selectedId === song.id) url.searchParams.delete('track');
    else url.searchParams.set('track', String(song.id));
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  }

  function closeTrack() {
    const url = new URL(page.url);
    url.searchParams.delete('track');
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  }
</script>

{#if showV2}
  <LibraryV2 tab="tracks" />
{:else}
<div class="flex min-h-0 flex-1 flex-col overflow-hidden">
  {#if isMobile.current}
    <TrackList {songs} {searchQuery} {isLoading} {selectedId} onSelect={selectTrack} />
  {:else}
    <!-- The PaneGroup and the full-song TrackList stay mounted across open/close so
         selecting a track only mounts the small detail pane — never tears down and
         re-sorts the entire library (which froze the page on large libraries). -->
    <Resizable.PaneGroup id="tracks-panels" direction="horizontal" class="min-h-0 flex-1">
      <!-- The pane must be a bounded flex column: TrackList's root relies on a
           flex parent (flex-1 + min-h-0) to bound its scroll viewport. A bare
           paneforge Pane is a block div, so without this the viewport grows to
           full content height and virtualization renders every row. -->
      <Resizable.Pane id="tracks-main" order={1} defaultSize={panelOpen ? 68 : 100} class="flex min-h-0 flex-col">
        <TrackList {songs} {searchQuery} {isLoading} {selectedId} onSelect={selectTrack} />
      </Resizable.Pane>
      {#if panelOpen}
        <Resizable.Handle />
        <Resizable.Pane id="tracks-details" order={2} defaultSize={32} minSize={28} maxSize={45}>
          {#if selected}
            <TrackPanel
              album={selected.album}
              song={selected.song}
              trackIndex={selected.index}
              onClose={closeTrack}
              onResetEnrichment={() => void loadSongs()}
            />
          {/if}
        </Resizable.Pane>
      {/if}
    </Resizable.PaneGroup>
  {/if}
</div>

{#if isMobile.current}
  <Sheet.Root open={panelOpen} onOpenChange={(open) => !open && closeTrack()}>
    <Sheet.Content side="bottom" class="h-[88vh] gap-0 p-0 data-[side=bottom]:h-[88vh] [&>button]:hidden">
      <Sheet.Title class="sr-only">Track details</Sheet.Title>
      <Sheet.Description class="sr-only">
        View track metadata, lyrics, fingerprint, and enrichment sources
      </Sheet.Description>
      {#if selected}
        <TrackPanel
          album={selected.album}
          song={selected.song}
          trackIndex={selected.index}
          onClose={closeTrack}
          onResetEnrichment={() => void loadSongs()}
        />
      {/if}
    </Sheet.Content>
  </Sheet.Root>
{/if}
{/if}

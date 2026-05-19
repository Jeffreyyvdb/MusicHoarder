<script lang="ts">
  import type { FileItem } from '$lib/types';
  import { Folder } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { playerStore } from '$lib/stores/player.svelte';
  import { getSongStreamUrl, parseSongId } from '$lib/api-client';
  import FileGridItem from './FileGridItem.svelte';
  import FileListItem from './FileListItem.svelte';

  type Props = {
    items: FileItem[];
    selectedId: string | null;
    onSelect: (item: FileItem) => void;
    onOpen: (item: FileItem) => void;
    viewMode: 'grid' | 'list';
    emptyMessage?: string;
  };
  const {
    items,
    selectedId,
    onSelect,
    onOpen,
    viewMode,
    emptyMessage = 'This folder is empty'
  }: Props = $props();

  const MIN_TILE_SIZE = 140;
  const MAX_TILE_SIZE = 180;
  const GRID_GAP = 8;
  const GRID_PADDING = 12;

  let containerWidth = $state(0);
  let containerEl: HTMLDivElement | undefined = $state();

  $effect(() => {
    if (!containerEl) return;
    containerWidth = containerEl.clientWidth;
    const observer = new ResizeObserver((entries) => {
      const entry = entries[0];
      if (entry) containerWidth = entry.contentRect.width;
    });
    observer.observe(containerEl);
    return () => observer.disconnect();
  });

  const layout = $derived.by(() => {
    const w = containerWidth || 800;
    const available = w - GRID_PADDING * 2;
    const columns = Math.max(
      2,
      Math.floor((available + GRID_GAP) / (MIN_TILE_SIZE + GRID_GAP))
    );
    const tileSize = Math.min(MAX_TILE_SIZE, (available - (columns - 1) * GRID_GAP) / columns);
    return { columns, tileSize };
  });

  function makeOnPlay(item: FileItem) {
    const songId = parseSongId(item.id);
    if (songId === null || item.type !== 'audio') return undefined;
    return () =>
      playerStore.playSong({
        id: songId,
        title: item.metadata?.title ?? item.name,
        artist: item.metadata?.artist ?? 'Unknown Artist',
        streamUrl: getSongStreamUrl(songId)
      });
  }
</script>

{#if items.length === 0}
  <div class="text-muted-foreground flex h-full flex-1 items-center justify-center">
    <div class="text-center">
      <Folder class="mx-auto size-12 opacity-50" />
      <p class="mt-2">{emptyMessage}</p>
    </div>
  </div>
{:else if viewMode === 'list'}
  <div bind:this={containerEl} class="h-full">
    <ScrollArea class="h-full">
      <div class="flex flex-col">
        {#each items as item (item.id)}
          {@const songId = parseSongId(item.id)}
          {@const isCurrentlyPlaying =
            playerStore.currentSong?.id === songId && playerStore.isPlaying}
          {@const isCurrentlyLoaded = playerStore.currentSong?.id === songId}
          <FileListItem
            {item}
            isSelected={selectedId === item.id}
            isPlaying={isCurrentlyPlaying}
            isLoaded={isCurrentlyLoaded}
            onSelect={() => onSelect(item)}
            onOpen={() => onOpen(item)}
            onPlay={makeOnPlay(item)}
          />
        {/each}
      </div>
    </ScrollArea>
  </div>
{:else}
  <div bind:this={containerEl} class="h-full">
    <ScrollArea class="h-full">
      <div
        class="grid"
        style="grid-template-columns: repeat({layout.columns}, minmax(0, {layout.tileSize}px)); gap: {GRID_GAP}px; padding: {GRID_PADDING}px;"
      >
        {#each items as item (item.id)}
          {@const songId = parseSongId(item.id)}
          {@const isCurrentlyPlaying =
            playerStore.currentSong?.id === songId && playerStore.isPlaying}
          {@const isCurrentlyLoaded = playerStore.currentSong?.id === songId}
          <div style="width: {layout.tileSize}px; height: {layout.tileSize}px;">
            <FileGridItem
              {item}
              isSelected={selectedId === item.id}
              isPlaying={isCurrentlyPlaying}
              isLoaded={isCurrentlyLoaded}
              onSelect={() => onSelect(item)}
              onOpen={() => onOpen(item)}
              onPlay={makeOnPlay(item)}
              tileSize={layout.tileSize}
            />
          </div>
        {/each}
      </div>
    </ScrollArea>
  </div>
{/if}

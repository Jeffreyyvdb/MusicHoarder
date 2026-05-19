<script lang="ts">
  import type { FileItem } from '$lib/types';
  import { Folder, Music, Pause, Play } from '@lucide/svelte';
  import { cn } from '$lib/utils';
  import StatusIcon from './StatusIcon.svelte';

  type Props = {
    item: FileItem;
    isSelected: boolean;
    isPlaying: boolean;
    isLoaded: boolean;
    onSelect: () => void;
    onOpen: () => void;
    onPlay?: () => void;
    tileSize: number;
  };
  const { item, isSelected, isPlaying, isLoaded, onSelect, onOpen, onPlay, tileSize }: Props =
    $props();

  const isFolder = $derived(item.type === 'folder');
  const status = $derived(item.metadata?.enrichmentStatus);
  const iconPx = $derived(Math.round(Math.max(40, Math.min(64, tileSize * 0.38))));
  const musicIconPx = $derived(Math.round(iconPx * 0.6));

  function handleKey(e: KeyboardEvent) {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onSelect();
    }
  }
</script>

<div
  role="button"
  tabindex="0"
  onclick={onSelect}
  ondblclick={onOpen}
  onkeydown={handleKey}
  class={cn(
    'group relative flex h-full w-full cursor-pointer flex-col items-center justify-center gap-2 rounded-xl p-3 text-center transition-all select-none',
    'hover:bg-secondary/50',
    isSelected && 'bg-primary/10 ring-primary ring-1',
    isLoaded && !isSelected && 'bg-primary/5'
  )}
>
  <div class="relative" style="width: {iconPx}px; height: {iconPx}px">
    {#if isFolder}
      <Folder class="text-primary size-full" />
    {:else if item.metadata?.albumArt}
      <div class="relative size-full overflow-hidden rounded-md">
        <img
          src={item.metadata.albumArt}
          alt={item.metadata.album}
          class="size-full object-cover"
          crossorigin="anonymous"
        />
        {#if onPlay}
          <div
            class={cn(
              'absolute inset-0 flex items-center justify-center bg-black/50 transition-opacity',
              isPlaying ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'
            )}
          >
            <button
              type="button"
              onclick={(e) => {
                e.stopPropagation();
                onPlay?.();
              }}
              class="bg-primary text-primary-foreground flex size-8 items-center justify-center rounded-full shadow-lg transition-transform hover:scale-110 active:scale-95"
              aria-label={isPlaying ? 'Pause' : 'Play'}
            >
              {#if isPlaying}
                <Pause class="size-3.5" />
              {:else}
                <Play class="size-3.5 translate-x-px" />
              {/if}
            </button>
          </div>
        {/if}
      </div>
    {:else}
      <div
        class={cn(
          'bg-secondary relative flex size-full items-center justify-center rounded-md transition-colors',
          isLoaded && 'bg-primary/20'
        )}
      >
        <Music
          class={cn('transition-colors', isLoaded ? 'text-primary' : 'text-muted-foreground')}
          style="width: {musicIconPx}px; height: {musicIconPx}px"
        />
        {#if onPlay}
          <div
            class={cn(
              'absolute inset-0 flex items-center justify-center rounded-md bg-black/50 transition-opacity',
              isPlaying ? 'opacity-100' : 'opacity-0 group-hover:opacity-100'
            )}
          >
            <button
              type="button"
              onclick={(e) => {
                e.stopPropagation();
                onPlay?.();
              }}
              class="bg-primary text-primary-foreground flex size-8 items-center justify-center rounded-full shadow-lg transition-transform hover:scale-110 active:scale-95"
              aria-label={isPlaying ? 'Pause' : 'Play'}
            >
              {#if isPlaying}
                <Pause class="size-3.5" />
              {:else}
                <Play class="size-3.5 translate-x-px" />
              {/if}
            </button>
          </div>
        {/if}
      </div>
    {/if}
    {#if !isFolder && status}
      <div class="absolute -right-1 -bottom-1">
        <StatusIcon {status} />
      </div>
    {/if}
    {#if !isFolder && isPlaying}
      <div
        class="bg-primary ring-background absolute -top-1 -left-1 size-2.5 animate-pulse rounded-full ring-2"
      ></div>
    {/if}
  </div>
  <div class="w-full min-w-0">
    <p class={cn('truncate text-sm leading-tight font-medium', isLoaded && 'text-primary')}>
      {item.name}
    </p>
    {#if !isFolder && item.metadata}
      <p class="text-muted-foreground truncate text-xs">{item.metadata.artist}</p>
    {/if}
  </div>
</div>

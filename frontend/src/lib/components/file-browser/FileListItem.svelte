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
  };
  const { item, isSelected, isPlaying, isLoaded, onSelect, onOpen, onPlay }: Props = $props();

  const isFolder = $derived(item.type === 'folder');
  const status = $derived(item.metadata?.enrichmentStatus);

  function formatDuration(seconds: number): string {
    const mins = Math.floor(seconds / 60);
    const secs = seconds % 60;
    return `${mins}:${secs.toString().padStart(2, '0')}`;
  }

  function formatFileSize(bytes: number): string {
    const mb = bytes / (1024 * 1024);
    return `${mb.toFixed(1)} MB`;
  }
</script>

<div
  class={cn(
    'group flex w-full items-center gap-3 px-4 py-2 transition-colors',
    'hover:bg-secondary/50',
    isSelected && 'bg-primary/10',
    isLoaded && !isSelected && 'bg-primary/5'
  )}
>
  <div
    role="button"
    tabindex="0"
    onclick={onSelect}
    ondblclick={onOpen}
    onkeydown={(e) => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        onSelect();
      }
    }}
    class={cn(
      'grid min-w-0 flex-1 cursor-pointer items-center gap-x-3 gap-y-0 text-left',
      'grid-cols-[minmax(0,1fr)]',
      'sm:grid-cols-[minmax(0,1fr)_96px]',
      'md:grid-cols-[minmax(0,1fr)_96px_72px]',
      'lg:grid-cols-[minmax(0,1fr)_96px_72px_96px_28px]'
    )}
  >
    <div class="flex min-w-0 items-center gap-3">
      <div class="relative shrink-0">
        {#if isFolder}
          <Folder class="text-primary size-8" />
        {:else if item.metadata?.albumArt}
          <div class="relative size-10 overflow-hidden rounded">
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
              'relative flex size-10 items-center justify-center rounded transition-colors',
              isLoaded ? 'bg-primary/20' : 'bg-secondary'
            )}
          >
            <Music
              class={cn(
                'size-5 transition-colors',
                isLoaded ? 'text-primary' : 'text-muted-foreground'
              )}
            />
            {#if onPlay}
              <div
                class={cn(
                  'absolute inset-0 flex items-center justify-center rounded bg-black/50 transition-opacity',
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
      </div>

      <div class="min-w-0 flex-1">
        <p class={cn('truncate leading-snug font-medium', isLoaded && 'text-primary')}>
          {item.name}
        </p>
        {#if !isFolder && item.metadata}
          <p class="text-muted-foreground truncate text-sm leading-snug">
            {item.metadata.artist} - {item.metadata.album}
          </p>
        {/if}
      </div>
    </div>

    <span class="text-muted-foreground hidden text-sm sm:block">
      {!isFolder && item.metadata ? item.metadata.format : ''}
    </span>

    <span class="text-muted-foreground hidden text-sm tabular-nums md:block">
      {!isFolder && item.metadata ? formatDuration(item.metadata.duration) : ''}
    </span>

    <span class="text-muted-foreground hidden text-sm tabular-nums lg:block">
      {!isFolder && item.metadata ? formatFileSize(item.metadata.fileSize) : ''}
    </span>

    <span class="hidden justify-self-end lg:block">
      {#if status}<StatusIcon {status} />{/if}
    </span>
  </div>
</div>

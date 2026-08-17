<script lang="ts">
  import { Check, FastForward, Pause, Play, Rewind } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import Scrubber from './Scrubber.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { formatDuration } from '$lib/formatters';
  import { blurAfterPointerClick, cn, transportGlyphClass } from '$lib/utils';

  /**
   * The Apple-Music-style naked-glyph transport (scrubber + prev/play/next + times)
   * shared by the in-app track panel and the public share page. Parents control width
   * and placement via a wrapper element.
   */
  type Props = {
    /** Whether this transport's track is the one loaded in the player. */
    isActive: boolean;
    isPlaying: boolean;
    /** Track duration in seconds, shown before the track is loaded in the player. */
    fallbackDuration: number;
    onPlayToggle: () => void;
    /**
     * Scrubber + a single big play/pause only — the fullscreen lyrics overlay's
     * bottom bar.
     */
    minimal?: boolean;
  };
  const { isActive, isPlaying, fallbackDuration, onPlayToggle, minimal = false }: Props =
    $props();

  // Prev/next walk the active playback queue, so they only act while this track is
  // the one loaded in the player; otherwise there's no queue position to move within.
  const canGoPrevious = $derived(isActive && playerStore.hasPrevious);
  const canGoNext = $derived(isActive && playerStore.hasNext);

  // Playback-speed presets (pitch-preserved — for singing/playing along, the
  // slow end is deliberately finer-grained than the fast end). The control is
  // a quiet tabular label at the row's edge rather than a MiniPlayer button:
  // most listeners never need it, so it only lives on the track-panel/share
  // transports and stays muted until a non-1× speed is active.
  const speedOptions = [0.5, 0.65, 0.75, 0.85, 1, 1.1, 1.25, 1.5];

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }
</script>

{#snippet speedMenu()}
  <DropdownMenu.Root>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <button
          {...props}
          type="button"
          class={cn(
            'focus-visible:ring-ring/50 w-8 shrink-0 rounded text-right text-[10px] font-medium tabular-nums outline-none focus-visible:ring-2',
            playerStore.playbackRate === 1
              ? 'text-muted-foreground/50 hover:text-foreground'
              : 'text-primary'
          )}
          aria-label="Playback speed"
          title="Playback speed"
        >
          {playerStore.playbackRate}×
        </button>
      {/snippet}
    </DropdownMenu.Trigger>
    <!-- z-[70]: this menu opens from inside the song-detail dialog (z-[60]);
         the default popover z-50 would render it invisibly behind the panel. -->
    <DropdownMenu.Content align="end" class="z-[70] min-w-28">
      {#each speedOptions as rate (rate)}
        <DropdownMenu.Item
          onSelect={() => playerStore.setPlaybackRate(rate)}
          class="justify-between text-xs tabular-nums"
        >
          {rate === 1 ? 'Normal' : `${rate}×`}
          {#if playerStore.playbackRate === rate}
            <Check class="text-muted-foreground size-3.5" />
          {/if}
        </DropdownMenu.Item>
      {/each}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/snippet}

<Scrubber {isActive} {fallbackDuration} />
{#if minimal}
  <div class="mt-1 flex items-center justify-between">
    <!-- w-8 ghost mirrors the speed control so the play glyph stays centered. -->
    <span class="flex items-center gap-1">
      <span class="w-8 shrink-0" aria-hidden="true"></span>
      <span class="text-muted-foreground w-10 text-xs tabular-nums">
        {isActive ? formatTime(playerStore.currentTime) : '0:00'}
      </span>
    </span>
    <Button
      variant="ghost"
      size="icon"
      class={cn(transportGlyphClass, 'size-12')}
      onclick={(e) => {
        blurAfterPointerClick(e);
        onPlayToggle();
      }}
      aria-label={isPlaying ? 'Pause' : 'Play'}
    >
      {#if isPlaying}
        <Pause class="size-8" fill="currentColor" />
      {:else}
        <Play class="size-8 translate-x-px" fill="currentColor" />
      {/if}
    </Button>
    <span class="flex items-center gap-1">
      <span class="text-muted-foreground w-10 text-right text-xs tabular-nums">
        {formatDuration(fallbackDuration)}
      </span>
      {@render speedMenu()}
    </span>
  </div>
{:else}
  <div class="mt-1.5 flex items-center gap-3">
    <!-- w-8 ghost mirrors the speed control so the transport stays centered. -->
    <span class="w-8 shrink-0" aria-hidden="true"></span>
    <span class="text-muted-foreground w-10 shrink-0 text-right text-xs tabular-nums">
      {isActive ? formatTime(playerStore.currentTime) : '0:00'}
    </span>
    <!-- Naked solid glyphs, no disc, no hover wash (a translucent circle reads as
         smudge on dark artwork). Feedback is press-scale on the glyph itself. -->
    <div class="mx-auto flex items-center gap-2">
      <Button
        variant="ghost"
        size="icon"
        class={cn(transportGlyphClass, 'size-9 disabled:opacity-30')}
        onclick={(e) => {
          blurAfterPointerClick(e);
          playerStore.playPrevious();
        }}
        disabled={!canGoPrevious}
        aria-label="Previous track"
      >
        <Rewind class="size-5.5" fill="currentColor" />
      </Button>
      <Button
        variant="ghost"
        size="icon"
        class={cn(transportGlyphClass, 'size-11')}
        onclick={(e) => {
          blurAfterPointerClick(e);
          onPlayToggle();
        }}
        aria-label={isPlaying ? 'Pause' : 'Play'}
      >
        {#if isPlaying}
          <Pause class="size-7" fill="currentColor" />
        {:else}
          <Play class="size-7 translate-x-px" fill="currentColor" />
        {/if}
      </Button>
      <Button
        variant="ghost"
        size="icon"
        class={cn(transportGlyphClass, 'size-9 disabled:opacity-30')}
        onclick={(e) => {
          blurAfterPointerClick(e);
          playerStore.playNext();
        }}
        disabled={!canGoNext}
        aria-label="Next track"
      >
        <FastForward class="size-5.5" fill="currentColor" />
      </Button>
    </div>
    <span class="text-muted-foreground w-10 shrink-0 text-xs tabular-nums">
      {formatDuration(fallbackDuration)}
    </span>
    {@render speedMenu()}
  </div>
{/if}

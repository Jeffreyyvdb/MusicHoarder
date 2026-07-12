<script lang="ts">
  import { FastForward, Pause, Play, Rewind } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import Scrubber from './Scrubber.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { formatDuration } from '$lib/formatters';

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

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }
</script>

<Scrubber {isActive} {fallbackDuration} />
{#if minimal}
  <div class="mt-1 flex items-center justify-between">
    <span class="text-muted-foreground w-10 text-xs tabular-nums">
      {isActive ? formatTime(playerStore.currentTime) : '0:00'}
    </span>
    <Button
      variant="ghost"
      size="icon"
      class="text-foreground hover:text-foreground size-12 bg-transparent transition-transform duration-100 ease-out hover:bg-transparent active:scale-90 dark:hover:bg-transparent"
      onclick={onPlayToggle}
      aria-label={isPlaying ? 'Pause' : 'Play'}
    >
      {#if isPlaying}
        <Pause class="size-8" fill="currentColor" />
      {:else}
        <Play class="size-8 translate-x-px" fill="currentColor" />
      {/if}
    </Button>
    <span class="text-muted-foreground w-10 text-right text-xs tabular-nums">
      {formatDuration(fallbackDuration)}
    </span>
  </div>
{:else}
  <div class="mt-1.5 flex items-center gap-3">
    <span class="text-muted-foreground w-10 shrink-0 text-right text-xs tabular-nums">
      {isActive ? formatTime(playerStore.currentTime) : '0:00'}
    </span>
    <!-- Naked solid glyphs, no disc, no hover wash (a translucent circle reads as
         smudge on dark artwork). Feedback is press-scale on the glyph itself. -->
    <div class="mx-auto flex items-center gap-2">
      <Button
        variant="ghost"
        size="icon"
        class="text-foreground hover:text-foreground size-9 bg-transparent transition-transform duration-100 ease-out hover:bg-transparent active:scale-90 disabled:opacity-30 dark:hover:bg-transparent"
        onclick={() => playerStore.playPrevious()}
        disabled={!canGoPrevious}
        aria-label="Previous track"
      >
        <Rewind class="size-5.5" fill="currentColor" />
      </Button>
      <Button
        variant="ghost"
        size="icon"
        class="text-foreground hover:text-foreground size-11 bg-transparent transition-transform duration-100 ease-out hover:bg-transparent active:scale-90 dark:hover:bg-transparent"
        onclick={onPlayToggle}
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
        class="text-foreground hover:text-foreground size-9 bg-transparent transition-transform duration-100 ease-out hover:bg-transparent active:scale-90 disabled:opacity-30 dark:hover:bg-transparent"
        onclick={() => playerStore.playNext()}
        disabled={!canGoNext}
        aria-label="Next track"
      >
        <FastForward class="size-5.5" fill="currentColor" />
      </Button>
    </div>
    <span class="text-muted-foreground w-10 shrink-0 text-xs tabular-nums">
      {formatDuration(fallbackDuration)}
    </span>
  </div>
{/if}

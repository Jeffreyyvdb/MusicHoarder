<script lang="ts">
  import { FastForward, Pause, Play, Rewind } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import Scrubber from './Scrubber.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { blurAfterPointerClick, cn, transportGlyphClass } from '$lib/utils';

  /**
   * The iOS Now Playing transport: the scrubber, the times UNDER it (elapsed at leading, time
   * remaining at trailing, a format capsule between), then previous / play-pause / next spaced
   * across the width at 56 / 72 / 56pt. Shared by Now Playing and the public share page; parents
   * control width and placement via a wrapper element.
   */
  type Props = {
    /** Whether this transport's track is the one loaded in the player. */
    isActive: boolean;
    isPlaying: boolean;
    /** Track duration in seconds, shown before the track is loaded in the player. */
    fallbackDuration: number;
    onPlayToggle: () => void;
    /** Scrubber, times and a single play/pause — the share page's fullscreen lyrics bar. */
    minimal?: boolean;
    /** "FLAC", "MP3 320": the quality capsule centred between the times (Apple's Lossless badge). */
    format?: string | null;
    /**
     * Keep the speed capsule up at 1× too. For surfaces with no ⋯ menu to reach Playback speed
     * from (the share page); Now Playing shows it only while a non-1× speed is on.
     */
    speedAlways?: boolean;
    /** Inside Now Playing: its menus take the dark media appearance and open above its z-60. */
    media?: boolean;
  };
  const {
    isActive,
    isPlaying,
    fallbackDuration,
    onPlayToggle,
    minimal = false,
    format = null,
    speedAlways = false,
    media = false
  }: Props = $props();

  // Prev/next walk the active playback queue, so they only act while this track is the one loaded
  // in the player; a browsed song has no queue position to move within. Previous is otherwise
  // always live (it restarts the first item), next while the queue or the radio can supply.
  const canGoPrevious = $derived(isActive && playerStore.hasPrevious);
  const canGoNext = $derived(isActive && playerStore.hasNext);

  // Playback-speed presets (pitch-preserved — for singing/playing along, the slow end is
  // deliberately finer-grained than the fast end).
  const speedOptions = [0.5, 0.65, 0.75, 0.85, 1, 1.1, 1.25, 1.5];
  // Not while the music plays on another device: the speed is this device's own (see
  // `playerStore.speedAdjustable`).
  const showSpeed = $derived(
    playerStore.speedAdjustable && (speedAlways || playerStore.playbackRate !== 1)
  );

  const duration = $derived(
    isActive && playerStore.duration > 0 ? playerStore.duration : fallbackDuration
  );
  const elapsed = $derived(isActive ? playerStore.currentTime : 0);
  const remaining = $derived(Math.max(0, duration - elapsed));

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  function speedLabel(rate: number): string {
    return rate === 1 ? 'Normal' : `${rate}×`;
  }
</script>

{#snippet speedMenu()}
  <DropdownMenu.Root>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <!-- A capsule: 20px visual, the hit area grown by the after: pseudo-element — 44pt wide
             (the times either side leave the room) and 32pt tall, growing downward only: upward
             is the scrubber's hit area, which must win the few pixels they share. -->
        <button
          {...props}
          type="button"
          class={cn(
            "text-caption-1 relative inline-flex h-5 items-center rounded-full px-2 font-semibold tabular-nums outline-none after:absolute after:-inset-x-3 after:top-0 after:-bottom-3 after:content-[''] focus-visible:ring-2 focus-visible:ring-ring",
            playerStore.playbackRate === 1
              ? cn('bg-secondary', media ? 'text-foreground' : 'text-muted-foreground')
              : 'bg-primary/15 text-primary'
          )}
          aria-label={`Playback speed, ${speedLabel(playerStore.playbackRate)}`}
          title="Playback speed"
        >
          {playerStore.playbackRate}×
        </button>
      {/snippet}
    </DropdownMenu.Trigger>
    <!-- z-[70]: this menu can open from inside Now Playing (z-[60]); the default z-50 would render
         it invisibly behind the overlay. -->
    <DropdownMenu.Content align="center" class={cn('z-[70] min-w-36', media && 'dark')}>
      <DropdownMenu.RadioGroup
        value={String(playerStore.playbackRate)}
        onValueChange={(v) => playerStore.setPlaybackRate(Number(v))}
      >
        {#each speedOptions as rate (rate)}
          <DropdownMenu.RadioItem value={String(rate)} class="tabular-nums">
            {speedLabel(rate)}
          </DropdownMenu.RadioItem>
        {/each}
      </DropdownMenu.RadioGroup>
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/snippet}

<Scrubber {isActive} fallbackDuration={duration} />
<div
  class="text-caption-1 text-muted-foreground mt-1.5 grid grid-cols-[1fr_auto_1fr] items-center gap-2 font-medium tabular-nums"
>
  <span>{formatTime(elapsed)}</span>
  <span class="flex items-center justify-center gap-1.5">
    {#if format}
      <!-- Label-colour text on the capsule: over Now Playing's coloured wash the 72% secondary
           tone on a 14% white fill lands near 4:1, short of what 12px text needs. -->
      <span
        class={cn(
          'bg-secondary inline-flex h-5 items-center rounded-full px-2 font-semibold tracking-wide',
          media ? 'text-foreground' : 'text-muted-foreground'
        )}
      >
        {format}
      </span>
    {/if}
    {#if showSpeed}
      {@render speedMenu()}
    {/if}
  </span>
  <span class="text-right">
    <span aria-hidden="true">−{formatTime(remaining)}</span>
    <span class="sr-only">{formatTime(remaining)} remaining</span>
  </span>
</div>

{#if minimal}
  <div class="mt-1 flex items-center justify-center">
    <Button
      variant="ghost"
      size="icon"
      class={cn(transportGlyphClass, 'size-16')}
      onclick={(e) => {
        blurAfterPointerClick(e);
        onPlayToggle();
      }}
      aria-label={isPlaying ? 'Pause' : 'Play'}
    >
      {#if isPlaying}
        <Pause class="size-9" fill="currentColor" />
      {:else}
        <Play class="size-9 translate-x-0.5" fill="currentColor" />
      {/if}
    </Button>
  </div>
{:else}
  <!-- Naked solid glyphs, no disc and no hover wash (a translucent circle reads as a smudge on
       artwork); feedback is press-scale on the glyph itself. Evenly spread, like iOS. -->
  <div class="mt-2 flex items-center justify-evenly">
    <Button
      variant="ghost"
      size="icon"
      class={cn(transportGlyphClass, 'size-14 disabled:opacity-30')}
      onclick={(e) => {
        blurAfterPointerClick(e);
        playerStore.playPrevious();
      }}
      disabled={!canGoPrevious}
      aria-label="Previous track"
    >
      <Rewind class="size-8" fill="currentColor" />
    </Button>
    <Button
      variant="ghost"
      size="icon"
      class={cn(transportGlyphClass, 'size-[72px]')}
      onclick={(e) => {
        blurAfterPointerClick(e);
        onPlayToggle();
      }}
      aria-label={isPlaying ? 'Pause' : 'Play'}
    >
      {#if isPlaying}
        <Pause class="size-11" fill="currentColor" />
      {:else}
        <Play class="size-11 translate-x-0.5" fill="currentColor" />
      {/if}
    </Button>
    <Button
      variant="ghost"
      size="icon"
      class={cn(transportGlyphClass, 'size-14 disabled:opacity-30')}
      onclick={(e) => {
        blurAfterPointerClick(e);
        playerStore.playNext();
      }}
      disabled={!canGoNext}
      aria-label="Next track"
    >
      <FastForward class="size-8" fill="currentColor" />
    </Button>
  </div>
{/if}

<script lang="ts">
  import { Music, Pause, Play, SkipBack, SkipForward, Volume2, VolumeX, X } from '@lucide/svelte';
  import { goto } from '$app/navigation';
  import { playerStore } from '$lib/stores/player.svelte';
  import { Button } from '$lib/components/ui/button';
  import { Slider } from '$lib/components/ui/slider';

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || Number.isNaN(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  // Progress as a 0..1 fraction. Driven into the seek bar via `transform: scaleX`
  // (composite-only) rather than a bits-ui Slider, whose per-value reflow on the
  // ~10 Hz time tick saturates the main thread and starves audio playback.
  const progress = $derived(
    playerStore.duration > 0
      ? Math.max(0, Math.min(1, playerStore.currentTime / playerStore.duration))
      : 0
  );

  const canSeek = $derived(Number.isFinite(playerStore.duration) && playerStore.duration > 0);

  let seekEl: HTMLDivElement | null = $state(null);

  function seekToClientX(clientX: number) {
    if (!seekEl || !canSeek) return;
    const rect = seekEl.getBoundingClientRect();
    if (rect.width <= 0) return;
    const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    playerStore.seek(ratio * playerStore.duration);
  }

  function onSeekPointerDown(e: PointerEvent) {
    // Capture unconditionally so a drag begun while metadata is still loading
    // keeps tracking and starts seeking the moment duration becomes known.
    (e.currentTarget as HTMLDivElement).setPointerCapture(e.pointerId);
    seekToClientX(e.clientX);
  }

  function onSeekPointerMove(e: PointerEvent) {
    const el = e.currentTarget as HTMLDivElement;
    if (!el.hasPointerCapture(e.pointerId)) return;
    seekToClientX(e.clientX);
  }

  function onSeekKeyDown(e: KeyboardEvent) {
    if (!canSeek) return;
    const d = playerStore.duration;
    const t = playerStore.currentTime;
    let next: number | null = null;
    switch (e.key) {
      case 'ArrowLeft':
        next = t - 5;
        break;
      case 'ArrowRight':
        next = t + 5;
        break;
      case 'PageDown':
        next = t - 30;
        break;
      case 'PageUp':
        next = t + 30;
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = d;
        break;
    }
    if (next === null) return;
    e.preventDefault();
    playerStore.seek(Math.max(0, Math.min(d, next)));
  }
</script>

{#if playerStore.currentSong && !playerStore.isPanelMounted}
  {@const song = playerStore.currentSong}
  <div
    class="border-border bg-background/70 fixed inset-x-3 z-50 overflow-hidden rounded-2xl border shadow-[0_-4px_24px_oklch(0%_0_0/0.08)] backdrop-blur-xl backdrop-saturate-150 bottom-[calc(84px_+_max(env(safe-area-inset-bottom),var(--mh-vv-bottom,0px)))] md:bottom-3 md:peer-data-[state=expanded]:left-[calc(var(--sidebar-width)+0.75rem)] dark:shadow-[0_-4px_20px_rgba(0,0,0,0.35)]"
  >
    <div class="bg-foreground/15 block h-0.5 w-full overflow-hidden sm:hidden" aria-hidden="true">
      <div
        class="bg-primary h-full w-full origin-left"
        style="transform: scaleX({progress})"
      ></div>
    </div>

    <div class="flex h-[56px] items-center gap-2 px-3 sm:h-[64px] sm:gap-3 sm:px-4">
      <button
        type="button"
        onclick={() => goto(`/library?song=${song.id}`)}
        class="hover:bg-primary/10 flex min-w-0 flex-1 items-center gap-2.5 rounded-md p-1 text-left transition-colors sm:w-48 sm:flex-none sm:shrink-0"
      >
        <div class="bg-primary/20 flex size-10 shrink-0 items-center justify-center rounded-md">
          <Music class="text-primary size-5" />
        </div>
        <div class="min-w-0">
          <p class="truncate text-sm leading-tight font-medium">{song.title}</p>
          <p class="text-muted-foreground truncate text-xs leading-tight">{song.artist}</p>
        </div>
      </button>

      <Button
        variant="ghost"
        size="icon"
        class="text-muted-foreground hover:text-foreground hover:bg-primary/10 size-9 shrink-0 disabled:opacity-40"
        onclick={() => playerStore.playPrevious()}
        disabled={!playerStore.hasPrevious}
        aria-label="Previous track"
      >
        <SkipBack class="size-5" />
      </Button>

      <Button
        variant="ghost"
        size="icon"
        class="text-foreground hover:text-primary hover:bg-primary/10 size-9 shrink-0"
        onclick={() => playerStore.togglePlay()}
        aria-label={playerStore.isPlaying ? 'Pause' : 'Play'}
      >
        {#if playerStore.isPlaying}
          <Pause class="size-5" />
        {:else}
          <Play class="size-5 translate-x-px" />
        {/if}
      </Button>

      <Button
        variant="ghost"
        size="icon"
        class="text-muted-foreground hover:text-foreground hover:bg-primary/10 size-9 shrink-0 disabled:opacity-40"
        onclick={() => playerStore.playNext()}
        disabled={!playerStore.hasNext}
        aria-label="Next track"
      >
        <SkipForward class="size-5" />
      </Button>

      <span
        class="text-muted-foreground hidden w-9 shrink-0 text-right text-xs tabular-nums sm:block"
      >
        {formatTime(playerStore.currentTime)}
      </span>

      <div
        bind:this={seekEl}
        role="slider"
        tabindex="0"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(progress * 100)}
        aria-label="Seek"
        class="group relative hidden h-4 min-w-0 flex-1 cursor-pointer touch-none items-center select-none sm:flex"
        onpointerdown={onSeekPointerDown}
        onpointermove={onSeekPointerMove}
        onkeydown={onSeekKeyDown}
      >
        <div class="bg-foreground/15 relative h-2 w-full overflow-hidden rounded-full">
          <div
            class="bg-primary absolute inset-0 origin-left rounded-full"
            style="transform: scaleX({progress})"
          ></div>
        </div>
        <div
          class="border-ring pointer-events-none absolute size-4 -translate-x-1/2 rounded-full border bg-white"
          style="left: {progress * 100}%"
        ></div>
      </div>

      <span
        class="text-muted-foreground hidden w-9 shrink-0 text-xs tabular-nums sm:block"
      >
        {formatTime(playerStore.duration)}
      </span>

      <div class="hidden shrink-0 items-center gap-1 sm:flex">
        <Button
          variant="ghost"
          size="icon"
          class="text-muted-foreground hover:text-foreground size-8"
          onclick={() => playerStore.setVolume(playerStore.volume === 0 ? 0.8 : 0)}
          aria-label={playerStore.volume === 0 ? 'Unmute' : 'Mute'}
        >
          {#if playerStore.volume === 0}
            <VolumeX class="size-4" />
          {:else}
            <Volume2 class="size-4" />
          {/if}
        </Button>
        <Slider
          type="single"
          value={playerStore.volume}
          max={1}
          min={0}
          step={0.02}
          class="w-20 shrink-0 cursor-pointer [&_[data-slot=slider-track]]:bg-foreground/15"
          onValueChange={(val) => {
            if (typeof val === 'number') playerStore.setVolume(val);
          }}
          aria-label="Volume"
        />
      </div>

      <Button
        variant="ghost"
        size="icon"
        class="text-muted-foreground hover:text-foreground size-8 shrink-0"
        onclick={() => playerStore.stop()}
        aria-label="Close player"
      >
        <X class="size-4" />
      </Button>
    </div>
  </div>
{/if}

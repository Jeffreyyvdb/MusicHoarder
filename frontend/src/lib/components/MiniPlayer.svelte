<script lang="ts">
  import {
    Maximize2,
    Pause,
    Play,
    Quote,
    SkipBack,
    SkipForward,
    Volume2,
    VolumeX,
    X
  } from '@lucide/svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { Button } from '$lib/components/ui/button';
  import Cover from '$lib/components/file-browser/Cover.svelte';

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

  // Apple-Music-style subtitle: "Artist — Album" when the album is known, artist
  // alone otherwise (em dash, matching the macOS now-playing bar).
  const subtitle = $derived.by(() => {
    const song = playerStore.currentSong;
    if (!song) return '';
    const album = song.album?.trim();
    return album ? `${song.artist} — ${album}` : song.artist;
  });

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

  // Volume uses the same lightweight pointer-driven track as the seek bar (a
  // bits-ui Slider here looked inconsistent and shipped extra reflow), so they
  // share visual language and behaviour.
  let volumeEl: HTMLDivElement | null = $state(null);

  function setVolumeFromClientX(clientX: number) {
    if (!volumeEl) return;
    const rect = volumeEl.getBoundingClientRect();
    if (rect.width <= 0) return;
    const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    playerStore.setVolume(ratio);
  }

  function onVolumePointerDown(e: PointerEvent) {
    (e.currentTarget as HTMLDivElement).setPointerCapture(e.pointerId);
    setVolumeFromClientX(e.clientX);
  }

  function onVolumePointerMove(e: PointerEvent) {
    const el = e.currentTarget as HTMLDivElement;
    if (!el.hasPointerCapture(e.pointerId)) return;
    setVolumeFromClientX(e.clientX);
  }

  function onVolumeKeyDown(e: KeyboardEvent) {
    const v = playerStore.volume;
    let next: number | null = null;
    switch (e.key) {
      case 'ArrowLeft':
      case 'ArrowDown':
        next = v - 0.05;
        break;
      case 'ArrowRight':
      case 'ArrowUp':
        next = v + 0.05;
        break;
      case 'Home':
        next = 0;
        break;
      case 'End':
        next = 1;
        break;
    }
    if (next === null) return;
    e.preventDefault();
    playerStore.setVolume(Math.max(0, Math.min(1, next)));
  }
</script>

{#if playerStore.currentSong && !playerStore.isPanelMounted}
  {@const song = playerStore.currentSong}
  <div
    class="mh-mini-enter border-border bg-background/70 fixed inset-x-3 z-50 overflow-hidden rounded-2xl border shadow-[0_-4px_24px_oklch(0%_0_0/0.08)] backdrop-blur-xl backdrop-saturate-150 bottom-[calc(84px_+_max(env(safe-area-inset-bottom),var(--mh-vv-bottom,0px)))] md:right-auto md:bottom-3 md:left-1/2 md:w-full md:max-w-3xl md:-translate-x-1/2 dark:shadow-[0_-4px_20px_rgba(0,0,0,0.35)]"
  >
    <div class="bg-foreground/15 block h-0.5 w-full overflow-hidden sm:hidden" aria-hidden="true">
      <div
        class="bg-primary h-full w-full origin-left"
        style="transform: scaleX({progress})"
      ></div>
    </div>

    <div class="flex h-[56px] items-center gap-2 px-3 sm:h-[64px] sm:gap-3 sm:px-4">
      <!-- LEFT: transport -->
      <div class="flex shrink-0 items-center gap-0.5 sm:gap-1">
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
      </div>

      <!-- CENTER: now-playing (art + title/artist, thin seek line under) -->
      <div class="flex min-w-0 flex-1 flex-col items-center justify-center gap-1">
        <button
          type="button"
          onclick={() => songDetail.open(song.id)}
          aria-label="Open now playing"
          class="group/np focus-visible:ring-ring/50 flex min-w-0 max-w-full items-center gap-2.5 rounded-md px-1.5 py-0.5 text-left outline-none focus-visible:ring-2"
        >
          <!-- Album art with an Apple-Music-style enlarge affordance: the cover
               eases up a touch and a dim scrim + expand glyph fade in on hover.
               Tailwind's hover/group-hover variants are already gated behind
               @media (hover: hover); motion-reduce drops the easing to a snap. -->
          <span class="relative block size-9 shrink-0 overflow-hidden rounded-[4px]">
            <Cover
              artist={song.artist}
              title={song.title}
              coverUrl={song.coverUrl ?? null}
              size={36}
              corner={4}
              caption={false}
              class="size-9 transition-transform duration-200 ease-[cubic-bezier(0.23,1,0.32,1)] group-hover/np:scale-[1.08] motion-reduce:transition-none"
            />
            <span
              class="pointer-events-none absolute inset-0 grid place-items-center bg-black/45 opacity-0 transition-opacity duration-150 ease-out group-hover/np:opacity-100 motion-reduce:transition-none"
              aria-hidden="true"
            >
              <Maximize2
                class="size-4 scale-90 text-white transition-transform duration-150 ease-[cubic-bezier(0.23,1,0.32,1)] group-hover/np:scale-100 motion-reduce:transition-none"
              />
            </span>
          </span>
          <div class="min-w-0">
            <p class="truncate text-[13px] leading-tight font-medium">{song.title}</p>
            <p class="text-muted-foreground truncate text-[11px] leading-tight">{subtitle}</p>
          </div>
        </button>

        <div class="hidden w-full max-w-[460px] items-center gap-2 sm:flex">
          <span class="text-muted-foreground w-9 shrink-0 text-right text-[10px] tabular-nums">
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
            class="group relative flex h-3 min-w-0 flex-1 cursor-pointer touch-none items-center select-none"
            onpointerdown={onSeekPointerDown}
            onpointermove={onSeekPointerMove}
            onkeydown={onSeekKeyDown}
          >
            <div class="bg-foreground/15 relative h-1 w-full overflow-hidden rounded-full">
              <div
                class="bg-foreground/40 group-hover:bg-primary absolute inset-0 origin-left rounded-full transition-colors"
                style="transform: scaleX({progress})"
              ></div>
            </div>
            <div
              class="border-ring pointer-events-none absolute size-3 -translate-x-1/2 rounded-full border bg-white opacity-0 transition-opacity group-hover:opacity-100"
              style="left: {progress * 100}%"
            ></div>
          </div>
          <span class="text-muted-foreground w-9 shrink-0 text-[10px] tabular-nums">
            {formatTime(playerStore.duration)}
          </span>
        </div>
      </div>

      <!-- RIGHT: actions -->
      <div class="flex shrink-0 items-center gap-1">
        <Button
          variant="ghost"
          size="icon"
          class="text-muted-foreground hover:text-foreground hidden size-8 sm:inline-flex"
          onclick={() => songDetail.open(song.id)}
          aria-label="Lyrics"
        >
          <Quote class="size-4" />
        </Button>

        <div class="hidden items-center gap-1.5 sm:flex">
          <Button
            variant="ghost"
            size="icon"
            class="text-muted-foreground hover:text-foreground size-8 shrink-0"
            onclick={() => playerStore.toggleMute()}
            aria-label={playerStore.volume === 0 ? 'Unmute' : 'Mute'}
          >
            {#if playerStore.volume === 0}
              <VolumeX class="size-4" />
            {:else}
              <Volume2 class="size-4" />
            {/if}
          </Button>
          <div
            bind:this={volumeEl}
            role="slider"
            tabindex="0"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={Math.round(playerStore.volume * 100)}
            aria-label="Volume"
            class="group relative flex h-3 w-16 shrink-0 cursor-pointer touch-none items-center select-none"
            onpointerdown={onVolumePointerDown}
            onpointermove={onVolumePointerMove}
            onkeydown={onVolumeKeyDown}
          >
            <div class="bg-foreground/15 relative h-1 w-full overflow-hidden rounded-full">
              <div
                class="bg-foreground/40 group-hover:bg-primary absolute inset-0 origin-left rounded-full transition-colors"
                style="transform: scaleX({playerStore.volume})"
              ></div>
            </div>
            <div
              class="border-ring pointer-events-none absolute size-3 -translate-x-1/2 rounded-full border bg-white opacity-0 transition-opacity group-hover:opacity-100"
              style="left: {playerStore.volume * 100}%"
            ></div>
          </div>
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
  </div>
{/if}

<style>
  /* Strong ease-out curve (per Emil Kowalski) — punchier than the stock CSS easings. */
  /* Touch only `transform` (the vertical rise) + opacity. On md+ the bar is
     centered via Tailwind's `translate: -50%` (the standalone `translate`
     property, independent of `transform`), so animating `transform` here
     composes with it instead of overwriting the horizontal centering. */
  .mh-mini-enter {
    animation: mh-mini-rise 280ms cubic-bezier(0.23, 1, 0.32, 1) both;
  }

  @keyframes mh-mini-rise {
    from {
      transform: translateY(8px);
      opacity: 0;
    }
    to {
      transform: translateY(0);
      opacity: 1;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .mh-mini-enter {
      animation: none;
    }
  }
</style>

<script lang="ts">
  import {
    FastForward,
    Maximize,
    Minimize,
    Pause,
    Play,
    Rewind
  } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import Scrubber from './Scrubber.svelte';
  import { getSongVideoStreamUrl } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';
  import { seekTargetForKey } from '$lib/player-seek';
  import { formatDuration } from '$lib/formatters';
  import { blurAfterPointerClick, cn, transportGlyphClass } from '$lib/utils';

  // Watch mode for the music video: a full-frame, letterboxed player in the track panel's Video
  // tab, expandable to fullscreen. Same slave-sync model as the backdrop — the store's audio
  // element is the master clock and the (muted) video follows it through the per-song offset,
  // hard-resyncing when drift exceeds DRIFT_TOLERANCE_S. That is also what makes the scrubber
  // work: it seeks the *audio*, and the next sync pass drags the video to the new position.
  // When the song shown isn't the one playing, the frame holds the first frame with a play hint.
  let {
    songId,
    offsetMs,
    title,
    artist,
    fallbackDuration = 0,
    onPlayRequest
  }: {
    songId: number;
    offsetMs: number;
    title: string;
    artist: string;
    /** Track length in seconds, used for the timeline before the song is loaded. */
    fallbackDuration?: number;
    onPlayRequest: () => void;
  } = $props();

  const DRIFT_TOLERANCE_S = 0.3;
  /** How long the fullscreen controls linger after the last pointer move. */
  const CONTROLS_IDLE_MS = 3000;

  let frameEl = $state<HTMLElement | null>(null);
  let videoEl = $state<HTMLVideoElement | null>(null);
  let videoFailed = $state(false);
  let clipOver = $state(false);

  // `expanded` drives the CSS overlay, which is both the presentation for the native
  // fullscreen element and the standalone fallback where element fullscreen is missing
  // (iPhone Safari only offers the video element's own player, which would strand the
  // audio master clock). `nativeFullscreen` tracks whether the browser actually took it,
  // so a user-driven exit (Esc, the browser's own control) can collapse the overlay too.
  let expanded = $state(false);
  let nativeFullscreen = $state(false);
  let controlsVisible = $state(true);
  let idleTimer: ReturnType<typeof setTimeout> | null = null;

  const isCurrentSong = $derived(playerStore.currentSong?.id === songId);
  const isPlaying = $derived(isCurrentSong && playerStore.isPlaying);
  const effectiveDuration = $derived(
    isCurrentSong && playerStore.duration > 0 ? playerStore.duration : fallbackDuration
  );

  $effect(() => {
    const el = videoEl;
    if (!el) return;
    if (isCurrentSong && playerStore.isPlaying && !clipOver) {
      void el.play().catch(() => {});
    } else {
      el.pause();
    }
  });

  $effect(() => {
    const el = videoEl;
    if (!el || !isCurrentSong) return;
    const mapped = playerStore.currentTime + offsetMs / 1000;

    if (mapped < 0) {
      // Song position precedes the clip's start — hold the first frame until it catches up.
      if (!el.paused) el.pause();
      if (el.currentTime !== 0) el.currentTime = 0;
      if (clipOver) clipOver = false;
      return;
    }

    const duration = el.duration;
    if (Number.isFinite(duration) && mapped >= duration - 0.05) {
      // The song outlives the clip: hold the final frame instead of looping.
      if (!clipOver) clipOver = true;
      return;
    }
    if (clipOver) clipOver = false;

    if (Math.abs(el.currentTime - mapped) > DRIFT_TOLERANCE_S) {
      el.currentTime = mapped;
    }
    if (playerStore.isPlaying && el.paused) {
      void el.play().catch(() => {});
    }
  });

  // Lock body scroll while the overlay covers the app; restore on collapse/unmount.
  $effect(() => {
    if (!expanded) return;
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = prev;
    };
  });

  // Auto-hide the chrome while the video plays so it never sits on the picture; any
  // pointer move over the frame brings it back (and on touch, the first tap does).
  $effect(() => {
    if (!isPlaying) {
      controlsVisible = true;
      return;
    }
    scheduleHide();
    return () => {
      if (idleTimer) clearTimeout(idleTimer);
      idleTimer = null;
    };
  });

  $effect(() => () => {
    if (idleTimer) clearTimeout(idleTimer);
  });

  function scheduleHide() {
    if (idleTimer) clearTimeout(idleTimer);
    idleTimer = setTimeout(() => (controlsVisible = false), CONTROLS_IDLE_MS);
  }

  function wakeControls() {
    controlsVisible = true;
    if (isPlaying) scheduleHide();
  }

  async function enterFullscreen() {
    expanded = true;
    controlsVisible = true;
    const el = frameEl as (HTMLElement & { webkitRequestFullscreen?: () => Promise<void> }) | null;
    if (!el) return;
    try {
      if (el.requestFullscreen) {
        await el.requestFullscreen();
        nativeFullscreen = true;
      } else if (el.webkitRequestFullscreen) {
        await el.webkitRequestFullscreen();
        nativeFullscreen = true;
      }
    } catch {
      // Fullscreen was refused (permissions policy, no user gesture) — the CSS
      // overlay alone still gives a full-window view.
      nativeFullscreen = false;
    }
  }

  function collapse() {
    expanded = false;
    controlsVisible = true;
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
    nativeFullscreen = false;
  }

  function toggleFullscreen() {
    if (expanded) collapse();
    else void enterFullscreen();
  }

  function onFullscreenChange() {
    // The browser's own exit (Esc, the F11/toolbar control) has to collapse the overlay too.
    if (nativeFullscreen && !document.fullscreenElement) {
      nativeFullscreen = false;
      expanded = false;
      controlsVisible = true;
    }
  }

  function togglePlayback() {
    if (isCurrentSong) playerStore.togglePlay();
    else onPlayRequest();
  }

  function onFrameClick() {
    const wasHidden = !controlsVisible;
    wakeControls();
    // A tap that only brought the chrome back should not also toggle playback — on a
    // touch screen that is the whole gesture, with no hover to reveal the controls first.
    if (!wasHidden) togglePlayback();
  }

  function onWindowKeyDown(e: KeyboardEvent) {
    if (!expanded) return;
    if (e.key === 'Escape') {
      // Capture phase + stopPropagation: the track panel lives inside a bits-ui Dialog
      // that also closes on Escape, and collapsing the video must not close the panel.
      e.stopPropagation();
      collapse();
      return;
    }
    const target = e.target as HTMLElement | null;
    if (target && (target.tagName === 'INPUT' || target.tagName === 'TEXTAREA' || target.isContentEditable))
      return;

    if (e.key === ' ' || e.key === 'k') {
      e.preventDefault();
      wakeControls();
      togglePlayback();
      return;
    }
    if (e.key === 'f') {
      e.preventDefault();
      toggleFullscreen();
      return;
    }
    if (!isCurrentSong || effectiveDuration <= 0) return;
    const next = seekTargetForKey(e.key, playerStore.currentTime, effectiveDuration);
    if (next === null) return;
    e.preventDefault();
    wakeControls();
    playerStore.seek(next);
  }

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }
</script>

<svelte:document onfullscreenchange={onFullscreenChange} />
<svelte:window onkeydowncapture={onWindowKeyDown} />

<div
  class={cn(
    'flex min-h-0 flex-1 items-center justify-center',
    !expanded && 'p-3 lg:p-6'
  )}
>
  {#if videoFailed}
    <p class="text-muted-foreground text-sm">The video could not be played.</p>
  {:else}
    <!-- `dark` on the frame: the chrome sits on a black letterbox, so the shared
         Scrubber/glyph tokens have to resolve to their dark-theme values even when
         the app is in light mode. -->
    <div
      bind:this={frameEl}
      class={cn(
        'group dark relative overflow-hidden bg-black shadow-2xl',
        expanded
          ? 'fixed inset-0 z-[80] flex size-full items-center justify-center rounded-none'
          : 'max-h-full w-full max-w-5xl rounded-xl'
      )}
      onpointermove={wakeControls}
      role="presentation"
    >
      <!-- Muted on purpose: the player's audio element carries the sound, in sync. -->
      <video
        bind:this={videoEl}
        src={getSongVideoStreamUrl(songId)}
        muted
        playsinline
        preload="auto"
        class={cn('w-full object-contain', expanded ? 'max-h-full' : 'max-h-[70vh]')}
        onerror={() => (videoFailed = true)}
      ></video>

      <!-- Click layer under the chrome: tap the picture to play/pause, double-tap for fullscreen. -->
      <button
        type="button"
        class={cn('absolute inset-0 z-0 outline-none', controlsVisible ? 'cursor-pointer' : 'cursor-none')}
        onclick={onFrameClick}
        ondblclick={toggleFullscreen}
        aria-label={isCurrentSong ? 'Toggle playback' : 'Play this song'}
      ></button>

      {#if !isPlaying}
        <span
          class="pointer-events-none absolute inset-0 z-0 flex flex-col items-center justify-center gap-2 bg-black/40 transition-opacity"
        >
          <span class="flex size-14 items-center justify-center rounded-full bg-white/90 shadow-lg">
            <Play class="ml-0.5 size-6 text-black" fill="currentColor" />
          </span>
          {#if !isCurrentSong}
            <span class="text-[12px] font-medium text-white/90">Play this song to watch in sync</span>
          {/if}
        </span>
      {/if}

      {#if clipOver && isCurrentSong}
        <span
          class="absolute right-3 bottom-20 z-10 rounded-full bg-black/70 px-2.5 py-1 text-[11px] text-white/80 backdrop-blur-sm"
        >
          Clip ended — song continues
        </span>
      {/if}

      {#if expanded}
        <!-- Fullscreen title card, so the frame still says what is playing once the app is hidden. -->
        <div
          class={cn(
            'pointer-events-none absolute inset-x-0 top-0 z-10 bg-gradient-to-b from-black/70 to-transparent px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-10 transition-opacity duration-200',
            controlsVisible ? 'opacity-100' : 'opacity-0'
          )}
        >
          <h2 class="truncate text-sm font-semibold text-white">{title}</h2>
          <p class="truncate text-xs text-white/70">{artist}</p>
        </div>
      {/if}

      <!-- Transport chrome: the shared Scrubber seeks the audio master, so scrubbing here
           moves the video with it through the sync effect above. -->
      <div
        class={cn(
          'absolute inset-x-0 bottom-0 z-10 bg-gradient-to-t from-black/90 via-black/65 to-transparent transition-opacity duration-200',
          expanded
            ? 'px-4 pt-10 pb-[max(0.75rem,env(safe-area-inset-bottom))] sm:px-8'
            : 'px-3 pt-10 pb-2',
          controlsVisible ? 'opacity-100' : 'pointer-events-none opacity-0'
        )}
      >
        <div class={cn('mx-auto w-full', expanded && 'max-w-4xl')}>
          <Scrubber isActive={isCurrentSong} {fallbackDuration} />
          <div class="mt-1 flex items-center gap-3">
            <span class="w-10 shrink-0 text-xs tabular-nums text-white/70">
              {isCurrentSong ? formatTime(playerStore.currentTime) : '0:00'}
            </span>
            <div class="mx-auto flex items-center gap-2">
              <Button
                variant="ghost"
                size="icon"
                class={cn(transportGlyphClass, 'size-9 text-white disabled:opacity-30')}
                onclick={(e) => {
                  blurAfterPointerClick(e);
                  playerStore.playPrevious();
                }}
                disabled={!isCurrentSong || !playerStore.hasPrevious}
                aria-label="Previous track"
              >
                <Rewind class="size-5" fill="currentColor" />
              </Button>
              <Button
                variant="ghost"
                size="icon"
                class={cn(transportGlyphClass, 'size-11 text-white')}
                onclick={(e) => {
                  blurAfterPointerClick(e);
                  togglePlayback();
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
                class={cn(transportGlyphClass, 'size-9 text-white disabled:opacity-30')}
                onclick={(e) => {
                  blurAfterPointerClick(e);
                  playerStore.playNext();
                }}
                disabled={!isCurrentSong || !playerStore.hasNext}
                aria-label="Next track"
              >
                <FastForward class="size-5" fill="currentColor" />
              </Button>
            </div>
            <span class="w-10 shrink-0 text-right text-xs tabular-nums text-white/70">
              {formatDuration(effectiveDuration)}
            </span>
            <Button
              variant="ghost"
              size="icon"
              class={cn(transportGlyphClass, 'size-9 shrink-0 text-white')}
              onclick={(e) => {
                blurAfterPointerClick(e);
                toggleFullscreen();
              }}
              aria-label={expanded ? 'Exit fullscreen' : 'Watch fullscreen'}
              title={expanded ? 'Exit fullscreen (Esc)' : 'Watch fullscreen (F)'}
            >
              {#if expanded}
                <Minimize class="size-5" />
              {:else}
                <Maximize class="size-5" />
              {/if}
            </Button>
          </div>
        </div>
      </div>
    </div>
  {/if}
</div>

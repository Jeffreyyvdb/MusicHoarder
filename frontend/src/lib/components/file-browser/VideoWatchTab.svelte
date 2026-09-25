<script lang="ts">
  import {
    FastForward,
    Maximize,
    Minimize,
    Pause,
    Play,
    Rewind
  } from '@lucide/svelte';
  import type { Snippet } from 'svelte';
  import { Button } from '$lib/components/ui/button';
  import Scrubber from './Scrubber.svelte';
  import { getSongVideoStreamUrl } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';
  import { seekTargetForKey } from '$lib/player-seek';
  import { formatDuration } from '$lib/formatters';
  import { blurAfterPointerClick, cn, transportGlyphClass } from '$lib/utils';

  // Watch mode for the music video: the clip fitted into all of Now Playing's middle (a phone) or
  // right column (lg) — as large as its shape allows, like Android's `fitClip` — and expandable to
  // fullscreen. Inline, the frame carries only tap-to-play and a fullscreen button — Now Playing's
  // own transport sits right under it; the fullscreen view brings its own chrome, since the app
  // is hidden there. Same slave-sync model as the backdrop — the store's audio
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
    generation = 0,
    caption,
    onPlayRequest
  }: {
    songId: number;
    offsetMs: number;
    title: string;
    artist: string;
    /** Track length in seconds, used for the timeline before the song is loaded. */
    fallbackDuration?: number;
    /** Bumped when a refetched file lands, so a failed or ended clip gets a clean slate. */
    generation?: number;
    /** A line under the picture (the sync status); the clip is fitted to leave it room. */
    caption?: Snippet;
    onPlayRequest: () => void;
  } = $props();

  const DRIFT_TOLERANCE_S = 0.3;
  /** How long the fullscreen controls linger after the last pointer move. */
  const CONTROLS_IDLE_MS = 3000;

  let frameEl = $state<HTMLElement | null>(null);
  let videoEl = $state<HTMLVideoElement | null>(null);
  let videoFailed = $state(false);
  let clipOver = $state(false);
  let videoLoadRetries = 0; // non-reactive: only read inside the onerror handler

  // The frame takes the clip's own shape, fitted into the stage less the caption: the biggest box
  // of that ratio that fits, never a cap in pixels, so a big screen gets a big picture. The ratio
  // is the decoded frame's (16:9 until the metadata says otherwise).
  let stageWidth = $state(0);
  let stageHeight = $state(0);
  let captionHeight = $state(0);
  let ratio = $state(16 / 9);
  const fit = $derived.by(() => {
    const width = stageWidth;
    const height = Math.max(0, stageHeight - (caption ? captionHeight : 0));
    if (width * height === 0) return { width: 0, height: 0 };
    return ratio >= width / height
      ? { width, height: Math.floor(width / ratio) }
      : { width: Math.floor(height * ratio), height };
  });
  function readRatio(e: Event) {
    const el = e.currentTarget as HTMLVideoElement;
    if (el.videoWidth > 0 && el.videoHeight > 0) ratio = el.videoWidth / el.videoHeight;
  }

  // A dropped stream request (proxy blip, API restarting) used to write off the tab with "could
  // not be played" — give the <video> a couple of reloads before giving up.
  const VIDEO_LOAD_RETRY_DELAYS_MS = [1000, 3000];
  function onVideoError() {
    const attempt = videoLoadRetries;
    if (attempt >= VIDEO_LOAD_RETRY_DELAYS_MS.length) {
      videoFailed = true;
      return;
    }
    videoLoadRetries = attempt + 1;
    const id = songId;
    setTimeout(() => {
      if (songId === id) videoEl?.load();
    }, VIDEO_LOAD_RETRY_DELAYS_MS[attempt]);
  }

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

  // New song (or a refetched file), fresh slate for the failure/retry state.
  $effect(() => {
    void songId;
    void generation;
    videoLoadRetries = 0;
    videoFailed = false;
    clipOver = false;
  });

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

  // A mouse double-click on the picture means fullscreen (in or out); its first click must not
  // also pause the song and its second resume it. So with a mouse the single-click toggle waits
  // out the double-click window and a dblclick cancels it. Touch and the keyboard act at once.
  const DOUBLE_CLICK_MS = 250;
  let lastPointerType = '';
  let clickTimer: ReturnType<typeof setTimeout> | null = null;
  function cancelPendingClick() {
    if (clickTimer) clearTimeout(clickTimer);
    clickTimer = null;
  }
  $effect(() => cancelPendingClick);

  function onFrameClick(event: MouseEvent) {
    if (event.detail > 1) return; // a double-click's second click: ondblclick has it
    let toggle = true;
    if (expanded) {
      const wasHidden = !controlsVisible;
      wakeControls();
      // A tap that only brought the chrome back should not also toggle playback — on a
      // touch screen that is the whole gesture, with no hover to reveal the controls first.
      toggle = !wasHidden;
    }
    // Inline there is no chrome to wake (Now Playing's transport is right below), so a tap always
    // plays or pauses.
    if (!toggle) return;
    cancelPendingClick();
    if (event.detail === 1 && lastPointerType === 'mouse') {
      clickTimer = setTimeout(() => {
        clickTimer = null;
        togglePlayback();
      }, DOUBLE_CLICK_MS);
    } else {
      togglePlayback();
    }
  }

  function onFrameDblClick() {
    cancelPendingClick();
    toggleFullscreen();
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

<!-- The stage: whatever room the parent gives it. The frame and its caption are one centred group,
     so the caption sits right under the picture rather than at the foot of the stage. -->
<div
  class="flex min-h-0 w-full flex-1 flex-col items-center justify-center"
  bind:clientWidth={stageWidth}
  bind:clientHeight={stageHeight}
>
  {#if videoFailed}
    <p class="text-subheadline text-muted-foreground">The video could not be played.</p>
  {:else}
    <!-- `dark` on the frame: the chrome sits on a black letterbox, so the shared Scrubber/glyph
         tokens resolve to their dark values whatever the surroundings. -->
    <div
      bind:this={frameEl}
      class={cn(
        'group dark relative overflow-hidden bg-black',
        expanded
          ? 'fixed inset-0 z-[80] flex size-full items-center justify-center rounded-none'
          : 'shrink-0 rounded-xl shadow-[0_24px_60px_rgb(0_0_0/0.5)]'
      )}
      style:width={expanded ? undefined : `${fit.width}px`}
      style:height={expanded ? undefined : `${fit.height}px`}
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
        class="size-full object-contain"
        onloadedmetadata={readRatio}
        onresize={readRatio}
        onloadeddata={() => (videoLoadRetries = 0)}
        onerror={onVideoError}
      ></video>

      <!-- Click layer under the chrome: tap the picture to play/pause, double-tap for fullscreen. -->
      <button
        type="button"
        class={cn('absolute inset-0 z-0 outline-none', controlsVisible ? 'cursor-pointer' : 'cursor-none')}
        onpointerdown={(e) => (lastPointerType = e.pointerType)}
        onclick={onFrameClick}
        ondblclick={onFrameDblClick}
        aria-label={isCurrentSong ? 'Toggle playback' : 'Play this song'}
      ></button>

      {#if !isPlaying}
        <span
          class="pointer-events-none absolute inset-0 z-0 flex flex-col items-center justify-center gap-2 bg-black/40"
        >
          <span class="flex size-14 items-center justify-center rounded-full bg-white/90 shadow-lg">
            <Play class="ml-0.5 size-6 text-black" fill="currentColor" />
          </span>
          {#if !isCurrentSong}
            <span class="text-footnote text-foreground font-medium">Play this song to watch in sync</span>
          {/if}
        </span>
      {/if}

      {#if clipOver && isCurrentSong}
        <span
          class={cn(
            'text-caption-1 text-foreground absolute right-3 z-10 rounded-full bg-black/70 px-2.5 py-1',
            expanded ? 'bottom-24' : 'bottom-3'
          )}
        >
          Clip ended — song continues
        </span>
      {/if}

      {#if !expanded}
        <!-- Inline: just the way into fullscreen (44pt), over the picture's top corner. -->
        <button
          type="button"
          class="focus-visible:ring-ring absolute top-1 right-1 z-10 flex size-11 items-center justify-center rounded-full outline-none focus-visible:ring-2"
          onclick={(e) => {
            blurAfterPointerClick(e);
            toggleFullscreen();
          }}
          aria-label="Watch fullscreen"
          title="Watch fullscreen (F)"
        >
          <span class="text-foreground flex size-8 items-center justify-center rounded-full bg-black/55">
            <Maximize class="size-4" />
          </span>
        </button>
      {:else}
        <!-- Fullscreen title card, so the frame still says what is playing once the app is hidden. -->
        <div
          class={cn(
            'pointer-events-none absolute inset-x-0 top-0 z-10 bg-gradient-to-b from-black/70 to-transparent px-5 pt-[max(1rem,env(safe-area-inset-top))] pb-10 transition-opacity duration-200',
            controlsVisible ? 'opacity-100' : 'opacity-0'
          )}
        >
          <h2 class="text-headline text-foreground truncate">{title}</h2>
          <p class="text-subheadline text-muted-foreground truncate">{artist}</p>
        </div>

        <!-- Transport chrome: the shared Scrubber seeks the audio master, so scrubbing here
             moves the video with it through the sync effect above. -->
        <div
          class={cn(
            'absolute inset-x-0 bottom-0 z-10 bg-gradient-to-t from-black/90 via-black/65 to-transparent px-4 pt-10 pb-[max(0.75rem,env(safe-area-inset-bottom))] transition-opacity duration-200 sm:px-8',
            controlsVisible ? 'opacity-100' : 'pointer-events-none opacity-0'
          )}
        >
          <div class="mx-auto w-full max-w-4xl">
            <Scrubber isActive={isCurrentSong} {fallbackDuration} />
            <div class="mt-1 flex items-center gap-3">
              <span class="text-caption-1 text-muted-foreground w-10 shrink-0 tabular-nums">
                {isCurrentSong ? formatTime(playerStore.currentTime) : '0:00'}
              </span>
              <div class="mx-auto flex items-center gap-2">
                <Button
                  variant="ghost"
                  size="icon"
                  class={cn(transportGlyphClass, 'size-11 disabled:opacity-30')}
                  onclick={(e) => {
                    blurAfterPointerClick(e);
                    playerStore.playPrevious();
                  }}
                  disabled={!isCurrentSong || !playerStore.hasPrevious}
                  aria-label="Previous track"
                >
                  <Rewind class="size-6" fill="currentColor" />
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  class={cn(transportGlyphClass, 'size-14')}
                  onclick={(e) => {
                    blurAfterPointerClick(e);
                    togglePlayback();
                  }}
                  aria-label={isPlaying ? 'Pause' : 'Play'}
                >
                  {#if isPlaying}
                    <Pause class="size-8" fill="currentColor" />
                  {:else}
                    <Play class="size-8 translate-x-px" fill="currentColor" />
                  {/if}
                </Button>
                <Button
                  variant="ghost"
                  size="icon"
                  class={cn(transportGlyphClass, 'size-11 disabled:opacity-30')}
                  onclick={(e) => {
                    blurAfterPointerClick(e);
                    playerStore.playNext();
                  }}
                  disabled={!isCurrentSong || !playerStore.hasNext}
                  aria-label="Next track"
                >
                  <FastForward class="size-6" fill="currentColor" />
                </Button>
              </div>
              <span class="text-caption-1 text-muted-foreground w-10 shrink-0 text-right tabular-nums">
                {formatDuration(effectiveDuration)}
              </span>
              <Button
                variant="ghost"
                size="icon"
                class={cn(transportGlyphClass, 'size-11 shrink-0')}
                onclick={(e) => {
                  blurAfterPointerClick(e);
                  toggleFullscreen();
                }}
                aria-label="Exit fullscreen"
                title="Exit fullscreen (Esc)"
              >
                <Minimize class="size-5" />
              </Button>
            </div>
          </div>
        </div>
      {/if}
    </div>
  {/if}
  {#if caption}
    <div class="w-full shrink-0 pt-3" bind:offsetHeight={captionHeight}>
      {@render caption()}
    </div>
  {/if}
</div>

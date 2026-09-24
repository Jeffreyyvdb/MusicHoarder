<script lang="ts">
  import { MediaQuery } from 'svelte/reactivity';
  import { playerStore } from '$lib/stores/player.svelte';
  import { videoBackdropPrefs } from '$lib/stores/video-backdrop-prefs.svelte';
  import { cropMatte } from '$lib/actions/crop-matte';
  import { cn } from '$lib/utils';

  // Read-only cousin of the in-app VideoBackdrop for the anonymous share page: the song's muted
  // music video, cross-faded in over the page's fixed ambient-artwork backdrop once the first
  // frame is decoded. The player's audio element is the master clock — the <video> is slaved to
  // it through the share's per-song sync offset (videoTime = audioTime + offsetMs/1000) with a
  // hard resync whenever drift exceeds DRIFT_TOLERANCE_S. The layer stack is opaque while
  // visible (video + gradient scrim), so the page's flat scrim underneath needs no coordination.
  let {
    songId,
    streamUrl,
    offsetMs = 0,
    durationSeconds = null,
    letterbox = null,
    pillarbox = null
  }: {
    songId: number;
    streamUrl: string;
    offsetMs?: number;
    durationSeconds?: number | null;
    /** The bars baked into the frame, cropped out of the fill (see crop-matte.ts). */
    letterbox?: number | null;
    pillarbox?: number | null;
  } = $props();

  const DRIFT_TOLERANCE_S = 0.3;

  // Same opt-out `videoBackdropPrefs` gives the owner inside the app (and the same
  // localStorage entry — one preference per browser, whichever page set it), plus Reduce
  // Motion as a system-level fallback for a visitor who has never touched the toggle.
  const reduceMotion = new MediaQuery('(prefers-reduced-motion: reduce)');

  let videoEl = $state<HTMLVideoElement | null>(null);
  let videoReady = $state(false); // first frame decoded — until then the <video> paints nothing
  let videoEnded = $state(false);
  let videoFailed = $state(false);

  // Gates whether the <video> mounts at all, independent of which track is playing — the
  // element pre-buffers (preload="auto") as soon as it's allowed, so the crossfade is instant
  // the moment the track actually starts, rather than fetching from cold at that point.
  const backdropAllowed = $derived(videoBackdropPrefs.enabled && !reduceMotion.current);
  const isCurrentSong = $derived(playerStore.currentSong?.id === songId);
  const showVideo = $derived(backdropAllowed && isCurrentSong && !videoEnded && !videoFailed);

  // Slave the video's transport state to the audio's. Effects only read player state and write to
  // the DOM element or local flags — never read-modify-write shared store state.
  $effect(() => {
    const el = videoEl;
    if (!el) return;
    if (playerStore.isPlaying && isCurrentSong && !videoEnded) {
      void el.play().catch(() => {});
    } else {
      el.pause();
    }
  });

  $effect(() => {
    const el = videoEl;
    if (!el || !showVideo) return;
    const mapped = playerStore.currentTime + offsetMs / 1000;

    if (mapped < 0) {
      // The song is positioned before the video's start (negative mapped time): hold the first
      // frame until the audio catches up.
      if (!el.paused) el.pause();
      if (el.currentTime !== 0) el.currentTime = 0;
      return;
    }

    const videoDuration = el.duration;
    if (Number.isFinite(videoDuration) && mapped >= videoDuration - 0.05) {
      // Song outlives the clip — fall back to artwork (the `ended` handler flips the same flag;
      // this catches seeks past the end that never fire `ended`).
      if (!videoEnded) videoEnded = true;
      return;
    }

    if (Math.abs(el.currentTime - mapped) > DRIFT_TOLERANCE_S) {
      el.currentTime = mapped;
    }
    if (playerStore.isPlaying && el.paused) {
      void el.play().catch(() => {});
    }
  });

  // Re-show the video when the visitor seeks back before its end.
  $effect(() => {
    if (!videoEnded) return;
    const mapped = playerStore.currentTime + offsetMs / 1000;
    if (durationSeconds != null && mapped < durationSeconds - 1) {
      videoEnded = false;
    }
  });
</script>

{#if backdropAllowed}
  <!-- Decorative, always-muted backdrop; the player's audio element is the actual sound. -->
  <div
    aria-hidden="true"
    class={cn(
      'mh-crossfade fixed inset-0 overflow-hidden transition-opacity duration-500',
      showVideo && videoReady ? 'opacity-100' : 'opacity-0'
    )}
  >
    <video
      bind:this={videoEl}
      src={streamUrl}
      muted
      playsinline
      preload="auto"
      tabindex="-1"
      class="absolute inset-0 size-full object-cover"
      use:cropMatte={{ letterbox, pillarbox }}
      onloadstart={() => (videoReady = false)}
      onloadeddata={() => (videoReady = true)}
      onended={() => (videoEnded = true)}
      onerror={() => (videoFailed = true)}
    ></video>
    <!-- Gradient scrim (no backdrop blur — it would mush the video, and a full-viewport
         backdrop-filter stalls the compositor on this scrolling page) for text legibility. -->
    <div
      class="from-background/75 via-background/45 to-background/85 absolute inset-0 bg-gradient-to-b"
    ></div>
  </div>
{/if}

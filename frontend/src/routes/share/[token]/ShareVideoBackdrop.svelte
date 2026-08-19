<script lang="ts">
  import { playerStore } from '$lib/stores/player.svelte';
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
    durationSeconds = null
  }: {
    songId: number;
    streamUrl: string;
    offsetMs?: number;
    durationSeconds?: number | null;
  } = $props();

  const DRIFT_TOLERANCE_S = 0.3;

  let videoEl = $state<HTMLVideoElement | null>(null);
  let videoReady = $state(false); // first frame decoded — until then the <video> paints nothing
  let videoEnded = $state(false);
  let videoFailed = $state(false);

  const isCurrentSong = $derived(playerStore.currentSong?.id === songId);
  const showVideo = $derived(isCurrentSong && !videoEnded && !videoFailed);

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

<!-- Decorative, always-muted backdrop; the player's audio element is the actual sound. -->
<div
  aria-hidden="true"
  class={cn(
    'fixed inset-0 transition-opacity duration-500',
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

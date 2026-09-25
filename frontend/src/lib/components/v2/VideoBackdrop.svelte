<script lang="ts">
  import { MediaQuery } from 'svelte/reactivity';
  import { getSongVideoStreamUrl } from '$lib/api-client';
  import { cropMatte } from '$lib/actions/crop-matte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { videoBackdropPrefs } from '$lib/stores/video-backdrop-prefs.svelte';
  import type { SongVideo } from '$lib/components/file-browser/now-playing/song-video.svelte';
  import { cn } from '$lib/utils';

  // Now Playing's background. Three layers, and no backdrop-filter anywhere (lyrics repaint every
  // frame over it): the cover, blown up and blurred into a wash; a black dim sized per cover so
  // white text always clears its contrast (see cover-dim.ts); and — when a music video is attached,
  // the pref is on and this song is the one playing — the muted video itself, cross-fading in once
  // its first frame decodes. The video fills the screen with its picture, not its frame: black bars
  // baked into the file are cropped (crop-matte.ts), or a letterbox's edge would cut a hard line
  // across the player wherever it happened to land — through Info's section control, say.
  //
  // The audio element in the player store is the master clock: the <video> is slaved to it
  // through the per-song sync offset (videoTime = audioTime + offsetMs/1000) with a hard resync
  // whenever drift exceeds DRIFT_TOLERANCE_S. The store's 10 Hz currentTime writes double as the
  // drift ticker, so no extra timer is needed; a >0.3 s divergence from seek/resume/track-change
  // trips the same rule.
  //
  // What the video IS (status, offset, admin actions) lives in the shared SongVideo, which the
  // Video mode and the Manage video sheet read too — a nudge moves this backdrop live.
  let {
    songId,
    ambientUrl,
    dimAlpha,
    video,
    showing = $bindable(false)
  }: {
    songId: number;
    ambientUrl: string | null;
    /** The dim layer's alpha for this cover (cover-dim.ts). */
    dimAlpha: number;
    video: SongVideo;
    /** Out: the video is up (the overlay adds a text halo for legibility over moving pictures). */
    showing?: boolean;
  } = $props();

  const DRIFT_TOLERANCE_S = 0.3;

  let videoEl = $state<HTMLVideoElement | null>(null);
  let videoReady = $state(false); // first frame decoded — until then the <video> paints nothing
  let videoEnded = $state(false);
  let videoFailed = $state(false);
  let videoLoadRetries = 0; // non-reactive: only read inside the onerror handler

  // Apple asks that motion have a system-level off switch; a full-bleed autoplaying video behind
  // the lyrics is exactly that motion, so Reduce Motion falls back to the static ambient artwork
  // underneath (always mounted — see the layer stack below) rather than only slowing it down.
  const reduceMotion = new MediaQuery('(prefers-reduced-motion: reduce)');

  const isCurrentSong = $derived(playerStore.currentSong?.id === songId);
  const offsetMs = $derived(video.offsetMs);
  const showVideo = $derived(
    video.playable &&
      video.songId === songId &&
      videoBackdropPrefs.enabled &&
      isCurrentSong &&
      !videoEnded &&
      !videoFailed &&
      !reduceMotion.current
  );

  $effect(() => {
    showing = showVideo && videoReady;
  });

  // New song, or a refetch settled: the new file deserves a clean slate even when the old one had
  // failed or ended.
  $effect(() => {
    void songId;
    void video.generation;
    videoReady = false;
    videoEnded = false;
    videoFailed = false;
    videoLoadRetries = 0;
  });

  // Slave the video's transport state to the audio's. Effects here only read player/pref state and
  // write to the DOM element or local flags (guarded by inequality) — never read-modify-write
  // shared store state (see the registerPanel hazard in player.svelte.ts).
  $effect(() => {
    const el = videoEl;
    if (!el) return;
    if (playerStore.isPlaying && !videoEnded) {
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

  // Re-show the video when the user seeks back before its end.
  $effect(() => {
    const info = video.info;
    if (!videoEnded || info?.status !== 'Ready') return;
    const duration = info.durationSeconds;
    const mapped = playerStore.currentTime + offsetMs / 1000;
    if (duration != null && mapped < duration - 1) {
      videoEnded = false;
    }
  });

  // A dropped stream request (proxy blip, API restarting) used to flip the backdrop to artwork
  // for the rest of the song — give the <video> a couple of reloads before giving up.
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
</script>

<!-- Layer 0 is the overlay's own black. The cover wash and the dim are ALWAYS mounted as the base:
     a <video> paints nothing until a frame is decoded (initial load, a resync seek into an
     unbuffered range, a mid-stream stall), and without the base those windows would flash black. -->
{#if ambientUrl}
  <img
    src={ambientUrl}
    alt=""
    aria-hidden="true"
    draggable="false"
    class="pointer-events-none absolute inset-0 size-full scale-150 object-cover blur-[64px] saturate-150"
  />
{/if}
<div
  class="np-dim pointer-events-none absolute inset-0"
  style="--np-dim: {dimAlpha}"
  aria-hidden="true"
></div>
{#if showVideo}
  <!-- Decorative, always-muted backdrop; the player's audio element is the actual sound.
       Cross-fades in over the ambient art once the first frame is decoded. -->
  <div
    class={cn(
      'mh-crossfade pointer-events-none absolute inset-0 overflow-hidden transition-opacity duration-500',
      videoReady ? 'opacity-100' : 'opacity-0'
    )}
  >
    <video
      bind:this={videoEl}
      src={getSongVideoStreamUrl(songId)}
      muted
      playsinline
      preload="auto"
      aria-hidden="true"
      tabindex="-1"
      class="absolute inset-0 size-full object-cover"
      use:cropMatte={{ letterbox: video.info?.letterbox, pillarbox: video.info?.pillarbox }}
      onloadstart={() => (videoReady = false)}
      onloadeddata={() => {
        videoReady = true;
        videoLoadRetries = 0; // healthy again — a later mid-play blip gets fresh retries
      }}
      onended={() => (videoEnded = true)}
      onerror={onVideoError}
    ></video>
    <!-- Gradient scrim (no backdrop blur — it would mush the video) for text legibility: heavier
         at the top and bottom where the chrome/transport text lives, lighter mid-frame. -->
    <div class="absolute inset-0 bg-gradient-to-b from-black/75 via-black/45 to-black/85"></div>
  </div>
{/if}

<style>
  .np-dim {
    background: rgb(0 0 0 / var(--np-dim, 0.6));
  }
  /* Increase Contrast and Reduce Transparency both ask for less of the picture showing through:
     deepen the dim rather than blurring more. */
  @media (prefers-contrast: more), (prefers-reduced-transparency: reduce) {
    .np-dim {
      background: rgb(0 0 0 / max(var(--np-dim, 0.6), 0.8));
    }
  }
</style>

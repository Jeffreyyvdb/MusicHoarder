<script lang="ts">
  import { Play } from '@lucide/svelte';
  import { getSongVideoStreamUrl } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';

  // Watch mode for the music video: a full-frame, letterboxed player in the track panel's Video
  // tab. Same slave-sync model as the backdrop — the store's audio element is the master clock and
  // the (muted) video follows it through the per-song offset, hard-resyncing when drift exceeds
  // DRIFT_TOLERANCE_S. Clicking the frame toggles the song's playback; seeking happens through the
  // panel's normal transport. When the song shown isn't the one playing, the frame holds the first
  // frame with a play hint.
  let {
    songId,
    offsetMs,
    onPlayRequest
  }: { songId: number; offsetMs: number; onPlayRequest: () => void } = $props();

  const DRIFT_TOLERANCE_S = 0.3;

  let videoEl = $state<HTMLVideoElement | null>(null);
  let videoFailed = $state(false);
  let clipOver = $state(false);
  let videoLoadRetries = 0; // non-reactive: only read inside the onerror handler

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

  const isCurrentSong = $derived(playerStore.currentSong?.id === songId);

  // New song, fresh slate for the failure/retry state.
  $effect(() => {
    void songId;
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

  function onFrameClick() {
    if (isCurrentSong) {
      playerStore.togglePlay();
    } else {
      onPlayRequest();
    }
  }
</script>

<div class="flex min-h-0 flex-1 items-center justify-center p-3 lg:p-6">
  {#if videoFailed}
    <p class="text-muted-foreground text-sm">The video could not be played.</p>
  {:else}
    <button
      type="button"
      class="group relative max-h-full w-full max-w-5xl overflow-hidden rounded-xl bg-black/60 shadow-2xl outline-none"
      onclick={onFrameClick}
      aria-label={isCurrentSong ? 'Toggle playback' : 'Play this song'}
    >
      <!-- Muted on purpose: the player's audio element carries the sound, in sync. -->
      <video
        bind:this={videoEl}
        src={getSongVideoStreamUrl(songId)}
        muted
        playsinline
        preload="auto"
        class="max-h-[70vh] w-full object-contain"
        onloadeddata={() => (videoLoadRetries = 0)}
        onerror={onVideoError}
      ></video>

      {#if !isCurrentSong || !playerStore.isPlaying}
        <span
          class="absolute inset-0 flex flex-col items-center justify-center gap-2 bg-black/40 transition-opacity"
        >
          <span
            class="bg-background/90 flex size-14 items-center justify-center rounded-full shadow-lg"
          >
            <Play class="ml-0.5 size-6" />
          </span>
          {#if !isCurrentSong}
            <span class="text-[12px] font-medium text-white/90">Play this song to watch in sync</span>
          {/if}
        </span>
      {/if}

      {#if clipOver && isCurrentSong}
        <span
          class="bg-background/80 text-muted-foreground absolute right-3 bottom-3 rounded-full px-2.5 py-1 text-[11px] backdrop-blur-sm"
        >
          Clip ended — song continues
        </span>
      {/if}
    </button>
  {/if}
</div>

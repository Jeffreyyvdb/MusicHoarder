<script lang="ts">
  import { page } from '$app/state';
  import { Film, Loader2, RotateCcw, Trash2, X } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Switch } from '$lib/components/ui/switch';
  import {
    getSongVideoInfo,
    getSongVideoInfoUntilSettled,
    getSongVideoStreamUrl,
    fetchSongVideo,
    setSongVideoOffset,
    resetSongVideoOffset,
    deleteSongVideo,
    type SongVideoInfo
  } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';
  import { videoBackdropPrefs } from '$lib/stores/video-backdrop-prefs.svelte';
  import { cn } from '$lib/utils';
  import { isAdmin } from '$lib/auth/capabilities';

  // The full-screen player's backdrop: the song's muted music video when one is attached (and the
  // pref is on and this song is the one playing), else the blurred ambient artwork. The audio
  // element in the player store is the master clock — the <video> is slaved to it through the
  // per-song sync offset (videoTime = audioTime + offsetMs/1000) with a hard resync whenever drift
  // exceeds DRIFT_TOLERANCE_S. The store's existing 10 Hz currentTime writes double as the drift
  // ticker, so no extra timer is needed; a >0.3 s divergence from seek/resume/track-change trips the
  // same rule.
  let { songId, ambientUrl }: { songId: number; ambientUrl: string | null } = $props();

  const DRIFT_TOLERANCE_S = 0.3;

  const isOwner = $derived(
    isAdmin(page.data.user)
  );

  let info = $state<SongVideoInfo | null>(null);
  let infoUnavailable = $state(false); // info load keeps failing; retrying in the background
  let offsetMs = $state(0); // local mirror of info.syncOffsetMs; updated optimistically on nudge
  let videoEl = $state<HTMLVideoElement | null>(null);
  let videoReady = $state(false); // first frame decoded — until then the <video> paints nothing
  let videoEnded = $state(false);
  let videoFailed = $state(false);
  let videoLoadRetries = 0; // non-reactive: only read inside the onerror handler
  let controlsOpen = $state(false);
  let controlsEl = $state<HTMLElement | null>(null);
  let urlInput = $state('');
  let busy = $state(false);

  // Hand-rolled popover dismissal: click/tap outside the cluster closes it, and Escape closes it
  // WITHOUT bubbling to the bits-ui Dialog (which would close the whole full-screen panel) —
  // hence the capture-phase listener with stopPropagation.
  $effect(() => {
    if (!controlsOpen) return;
    const onPointerDown = (e: PointerEvent) => {
      if (controlsEl && e.target instanceof Node && !controlsEl.contains(e.target)) {
        controlsOpen = false;
      }
    };
    const onKeydown = (e: KeyboardEvent) => {
      if (e.key === 'Escape') {
        e.stopPropagation();
        controlsOpen = false;
      }
    };
    document.addEventListener('pointerdown', onPointerDown, true);
    document.addEventListener('keydown', onKeydown, true);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown, true);
      document.removeEventListener('keydown', onKeydown, true);
    };
  });

  const isCurrentSong = $derived(playerStore.currentSong?.id === songId);
  // Ready and its file is actually on disk — fileMissing means the stream endpoint would 404.
  const playable = $derived(info?.status === 'Ready' && !info.fileMissing);
  const showVideo = $derived(
    playable && videoBackdropPrefs.enabled && isCurrentSong && !videoEnded && !videoFailed
  );

  // Load (and reload on song change) the video info; reset per-song playback state. The load only
  // settles on a definitive answer (info, or a 404 meaning none attached) and retries everything
  // else until unmount — a transient failure after a page refresh must read as "unavailable",
  // never as "no video, fetch it again".
  $effect(() => {
    const id = songId;
    videoReady = false;
    videoEnded = false;
    videoFailed = false;
    videoLoadRetries = 0;
    info = null;
    infoUnavailable = false;
    const abort = new AbortController();
    getSongVideoInfoUntilSettled(id, {
      signal: abort.signal,
      onRetry: () => {
        if (!abort.signal.aborted) infoUnavailable = true;
      }
    }).then(
      (result) => {
        if (abort.signal.aborted) return;
        infoUnavailable = false;
        info = result;
        offsetMs = result?.syncOffsetMs ?? 0;
      },
      () => {} // rejects only on abort
    );
    return () => abort.abort();
  });

  // While a fetch is running server-side, poll until it settles (fetches take ~30–60s).
  $effect(() => {
    if (info?.status !== 'Fetching') return;
    const id = songId;
    const timer = setInterval(() => {
      getSongVideoInfo(id).then(
        (result) => {
          if (result && result.status !== 'Fetching') {
            info = result;
            offsetMs = result.syncOffsetMs;
            // A refetch just settled — the new file deserves a clean slate even when the old
            // one had failed or ended.
            videoFailed = false;
            videoEnded = false;
            videoLoadRetries = 0;
          }
        },
        () => {}
      );
    }, 3000);
    return () => clearInterval(timer);
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

  async function onFetch() {
    busy = true;
    try {
      info = await fetchSongVideo(songId, urlInput.trim() || undefined);
      urlInput = '';
    } catch {
      /* surfaced via info.lastError on the next poll */
    } finally {
      busy = false;
    }
  }

  async function nudge(deltaMs: number) {
    const next = offsetMs + deltaMs;
    offsetMs = next; // optimistic — nudging should feel live
    try {
      info = await setSongVideoOffset(songId, next);
      offsetMs = info.syncOffsetMs;
    } catch {
      offsetMs = info?.syncOffsetMs ?? 0;
    }
  }

  async function onResetAuto() {
    try {
      info = await resetSongVideoOffset(songId);
      offsetMs = info.syncOffsetMs;
    } catch {
      /* keep current */
    }
  }

  async function onRemove() {
    busy = true;
    try {
      await deleteSongVideo(songId);
      info = null;
      offsetMs = 0;
    } finally {
      busy = false;
    }
  }

  function formatOffset(ms: number): string {
    const sign = ms < 0 ? '−' : '+';
    return `${sign}${(Math.abs(ms) / 1000).toFixed(1)}s`;
  }

  const syncLabel = $derived.by(() => {
    if (!info) return '';
    switch (info.syncSource) {
      case 'SameSource':
        return 'synced (same source)';
      case 'AutoAligned':
        return `auto-aligned ${formatOffset(info.syncOffsetMs)}${info.syncConfidence != null ? ` · ${Math.round(info.syncConfidence * 100)}%` : ''}`;
      case 'Manual':
        return `manual ${formatOffset(info.syncOffsetMs)}`;
      default:
        return 'not aligned';
    }
  });
</script>

<!-- Backdrop layers (absolute, behind the panel's z-10 content). The ambient art + scrim is
     ALWAYS mounted as the base layer: a <video> paints nothing until a frame is decoded (initial
     load, a resync seek into an unbuffered range, a mid-stream stall), and the video's gradient
     scrim is translucent — without the base underneath, those windows see straight through the
     transparent dialog to the page behind it (the song list). -->
{#if ambientUrl}
  <img
    src={ambientUrl}
    alt=""
    aria-hidden="true"
    class="absolute inset-0 size-full scale-110 object-cover opacity-50 blur-3xl"
  />
{/if}
<div class="bg-background/80 absolute inset-0 backdrop-blur-2xl"></div>
{#if showVideo}
  <!-- Decorative, always-muted backdrop; the player's audio element is the actual sound.
       Cross-fades in over the ambient art once the first frame is decoded. -->
  <div
    class={cn(
      'absolute inset-0 transition-opacity duration-500',
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
    <div
      class="from-background/75 via-background/45 to-background/85 absolute inset-0 bg-gradient-to-b"
    ></div>
  </div>
{/if}

<!-- Floating control cluster (above the panel content) -->
<div bind:this={controlsEl} class="absolute right-4 bottom-4 z-20 flex flex-col items-end gap-2">
  {#if controlsOpen}
    <div
      class="bg-popover/95 border-border w-72 rounded-xl border p-3 shadow-xl backdrop-blur-sm"
    >
      <div class="mb-2 flex items-center justify-between gap-2">
        <span class="text-sm font-medium">Music video</span>
        <span class="flex items-center gap-1.5">
          {#if info?.status === 'Fetching'}
            <span class="text-muted-foreground inline-flex items-center gap-1 text-xs">
              <Loader2 class="size-3 animate-spin" /> fetching…
            </span>
          {:else if info?.status === 'Ready' && info.fileMissing}
            <span class="text-destructive text-xs">file missing</span>
          {:else if info?.status === 'Ready'}
            <span class="text-muted-foreground text-xs">{syncLabel}</span>
          {:else if info?.status === 'Failed'}
            <span class="text-destructive text-xs">failed</span>
          {:else if infoUnavailable}
            <!-- The status request keeps failing — unknown is NOT "none"; a fetch here would
                 needlessly re-download a video that may well still exist. -->
            <span class="text-muted-foreground inline-flex items-center gap-1 text-xs">
              <Loader2 class="size-3 animate-spin" /> status unavailable — retrying
            </span>
          {:else}
            <span class="text-muted-foreground text-xs">none</span>
          {/if}
          <Button
            size="icon"
            variant="ghost"
            class="text-muted-foreground -mr-1 size-6"
            aria-label="Close music video options"
            onclick={() => (controlsOpen = false)}
          >
            <X class="size-3.5" />
          </Button>
        </span>
      </div>

      {#if info?.status === 'Failed' && info.lastError}
        <p class="text-destructive/90 mb-2 line-clamp-2 text-xs" title={info.lastError}>
          {info.lastError}
        </p>
      {/if}

      {#if info?.status === 'Ready' && info.fileMissing}
        <p class="text-destructive/90 mb-2 text-xs">
          The video file is gone from disk — refetch to restore it.
        </p>
      {/if}

      {#if playable}
        <label class="mb-2 flex items-center justify-between gap-2 text-sm">
          <span>Show as backdrop</span>
          <Switch
            checked={videoBackdropPrefs.enabled}
            onCheckedChange={(v: boolean) => videoBackdropPrefs.setEnabled(v)}
          />
        </label>
      {/if}

      {#if isOwner}
        {#if playable}
          <div class="mb-2">
            <div class="text-muted-foreground mb-1 text-xs">
              Sync nudge · {formatOffset(offsetMs)}
            </div>
            <div class="flex items-center gap-1">
              <Button size="sm" variant="outline" class="h-7 px-2 text-xs" onclick={() => nudge(-1000)}>
                −1s
              </Button>
              <Button size="sm" variant="outline" class="h-7 px-2 text-xs" onclick={() => nudge(-100)}>
                −0.1s
              </Button>
              <Button size="sm" variant="outline" class="h-7 px-2 text-xs" onclick={() => nudge(100)}>
                +0.1s
              </Button>
              <Button size="sm" variant="outline" class="h-7 px-2 text-xs" onclick={() => nudge(1000)}>
                +1s
              </Button>
              <Button
                size="sm"
                variant="ghost"
                class="h-7 px-2"
                title="Reset to automatic alignment"
                onclick={onResetAuto}
              >
                <RotateCcw class="size-3.5" />
              </Button>
            </div>
          </div>
        {/if}

        {#if info?.status !== 'Fetching'}
          <div class="mb-2 flex items-center gap-1">
            <Input
              bind:value={urlInput}
              placeholder="YouTube URL (optional)"
              class="h-8 flex-1 text-xs"
            />
            <Button
              size="sm"
              class="h-8 text-xs"
              disabled={busy || (infoUnavailable && !info)}
              title={infoUnavailable && !info
                ? 'Video status is unavailable right now — retrying'
                : undefined}
              onclick={onFetch}
            >
              {info ? 'Refetch' : 'Fetch'}
            </Button>
          </div>
        {/if}

        {#if info && info.status !== 'Fetching'}
          <Button
            size="sm"
            variant="ghost"
            class="text-destructive hover:text-destructive h-7 w-full justify-start px-2 text-xs"
            disabled={busy}
            onclick={onRemove}
          >
            <Trash2 class="mr-1 size-3.5" /> Remove video
          </Button>
        {/if}
      {/if}
    </div>
  {/if}

  {#if isOwner || info}
    <Button
      size="icon"
      variant={controlsOpen ? 'default' : 'ghost'}
      class="bg-background/40 hover:bg-background/70 size-9 rounded-full backdrop-blur-sm"
      title="Music video"
      aria-label="Music video options"
      onclick={() => (controlsOpen = !controlsOpen)}
    >
      <Film class="size-4" />
    </Button>
  {/if}
</div>

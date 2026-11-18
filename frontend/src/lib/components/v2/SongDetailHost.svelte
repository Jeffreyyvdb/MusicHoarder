<script lang="ts">
  import { holdAppInert } from '#lib/actions/inert-app.ts';
  import { untrack } from 'svelte';
  import { Dialog as DialogPrimitive } from 'bits-ui';
  import { Loader2 } from '@lucide/svelte';
  import { page } from '$app/state';
  import { Button } from '#lib/components/ui/button/index.ts';
  import TrackPanel from '#lib/components/file-browser/TrackPanel.svelte';
  import ShareWithFriendDialog from '#lib/components/file-browser/ShareWithFriendDialog.svelte';
  import AddToPlaylistSheet from '#lib/components/v2/AddToPlaylistSheet.svelte';
  import ReportDownloadSheet from '#lib/components/v2/ReportDownloadSheet.svelte';
  import VideoBackdrop from '#lib/components/v2/VideoBackdrop.svelte';
  import { SongVideo } from '#lib/components/file-browser/now-playing/song-video.svelte.ts';
  import { coverDimAlpha, DIM_UNKNOWN } from '#lib/components/file-browser/now-playing/cover-dim.ts';
  import { coverUrlForSong, coverThumbUrl } from '#lib/api-client.ts';
  import { songDetail } from '#lib/stores/song-detail.svelte.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { themeSurface } from '#lib/stores/theme-surface.svelte.ts';
  import { isAdmin } from '#lib/auth/capabilities.ts';
  import { mediaAppearance, skinLook } from '#lib/hardware/hardware-mode.svelte.ts';
  import { DISMISS_VELOCITY, EASE_PRESENT, prefersReducedMotion } from '#lib/motion.ts';
  import { keepOpenForToasts } from '#lib/components/ui/sonner/index.ts';
  import { dragDismiss, type DragDismissHandlers } from '#lib/actions/drag-dismiss.ts';

  // The single global mount of Now Playing — an Apple-Music-style full-screen overlay (a portaled
  // bits-ui Dialog: focus trap, Escape and body scroll-lock for free). It has its own MEDIA
  // APPEARANCE, independent of the app theme: the root carries `dark`, so every token inside
  // resolves to its dark value, over a wash of the cover dimmed per cover (VideoBackdrop +
  // cover-dim.ts). While it is open the installed app's status bar is painted black to match
  // (themeSurface), the tab bar hides (the shell reads songDetail.isOpen) and the mini player hides
  // (registerPanel).
  let departing = $state.raw<typeof songDetail.resolved>(null);
  $effect.pre(() => {
    if (songDetail.resolved) departing = songDetail.resolved;
  });
  const r = $derived(songDetail.resolved ?? (songDetail.isOpen ? null : departing));

  const surface = $derived(skinLook('bg-black', 'bg-background'));

  // Ambient backdrop: a large, heavily-blurred fill of the track's cover. Own-cover URLs get a
  // modest size (the blur hides detail); external URLs pass through unchanged.
  const coverUrl = $derived(r ? (coverUrlForSong(r.song) ?? r.album.coverUrl ?? null) : null);
  const ambientUrl = $derived(coverThumbUrl(coverUrl, 600));

  // How much black goes over this cover so white text keeps its contrast (cover-dim.ts). Sampled
  // once per cover from the same thumbnail the wash loads.
  let dimAlpha = $state(DIM_UNKNOWN);
  $effect(() => {
    const url = ambientUrl;
    let cancelled = false;
    void coverDimAlpha(url).then((alpha) => {
      if (!cancelled) dimAlpha = alpha;
    });
    return () => {
      cancelled = true;
    };
  });

  // The song's music video, loaded once and shared by the backdrop, the Video mode, the ⋯ menu
  // and the Manage video sheet.
  const video = new SongVideo();
  // A primitive, so an SSE refresh that rebuilds `r` for the same song does not reload the video.
  const videoSongId = $derived(songDetail.isOpen ? (r?.song.id ?? null) : null);
  $effect(() => {
    const id = videoSongId;
    return id == null ? undefined : video.load(id);
  });
  // Only while the overlay is up: a close mid-fetch must not leave a 3s poll running all session.
  $effect(() =>
    songDetail.isOpen && video.info?.status === 'Fetching' ? video.poll() : undefined
  );
  let videoShowing = $state(false);
  // TrackPanel's Video mode is up: the backdrop blurs its copy of the clip.
  let videoWatching = $state(false);

  // Keep the dataset live while the panel is open so a song opened off-Library
  // refreshes after enrichment. The initial fetch is owned by songDetail.open()
  // (via ensureLoaded) — deliberately NOT called here: reading songsStore's
  // isLoading inside this effect while loads write it creates a feedback cycle.
  $effect(() => {
    if (!songDetail.isOpen) return;
    songsStore.startLive();
    return () => songsStore.stopLive();
  });

  // Hide the global MiniPlayer only while the detail panel is actually open (the
  // overlay has its own transport), then restore it on close.
  $effect(() => {
    if (!songDetail.isOpen) return;
    return playerStore.registerPanel();
  });

  // The status bar follows the media appearance (black) while this is up. The claim is a token,
  // released when the overlay closes or this unmounts.
  $effect(() => (songDetail.isOpen ? themeSurface.claimMedia() : undefined));
  $effect(() => (songDetail.isOpen && videoShowing ? themeSurface.claimTv() : undefined));

  // Follow playback: when a new song starts while the panel is showing the song
  // that was playing, re-target the panel to it (manual play or queue
  // auto-advance). A panel opened for a different (browsed) song stays put.
  // Plain `let` — only a between-runs memo, must not be a dependency.
  let lastPlayingId: number | null = null;
  $effect(() => {
    const playingId = playerStore.currentSong?.id ?? null;
    if (playingId === null) return; // stop/clear: keep the last real song as the anchor
    const prev = lastPlayingId;
    lastPlayingId = playingId;
    if (playingId === prev) return;
    // untrack: open() writes songDetail.target; tracked reads of it here would
    // make this effect re-fire on its own write (effect_update_depth_exceeded).
    untrack(() => {
      if (songDetail.isOpen && songDetail.target?.songId === prev) {
        songDetail.open(playingId);
      }
    });
  });

  // A target that never resolves (a deleted row, an id this library view does not hold) used to
  // spin "Loading track…" forever. The panel resolves against the detail album set, which loads
  // separately from the songs, so the grace period only starts once that set (and the songs) have
  // loaded: past it, a song still missing really is not in this library view. A set that failed to
  // load says so at once, with a Retry; one still loading keeps the spinner.
  let unresolvedTimedOut = $state(false);
  let resolveAttempt = $state(0);
  const detailState = $derived(songsStore.detailAlbumsState);
  $effect(() => {
    void resolveAttempt; // Try again restarts the grace period
    unresolvedTimedOut = false;
    if (!songDetail.isOpen || r || detailState !== 'loaded' || songsStore.isLoading) return;
    const timer = setTimeout(() => (unresolvedTimedOut = true), 8000);
    return () => clearTimeout(timer);
  });
  function retryResolve() {
    resolveAttempt += 1;
    void songsStore.loadSongs();
  }
  function retryDetailAlbums() {
    void songsStore.reloadDetailAlbums();
    if (songsStore.error) void songsStore.loadSongs();
  }

  function onResetEnrichment() {
    void songsStore.loadSongs();
  }

  // "Now playing" when the overlay shows the loaded song; "Song details" for a browsed one — the
  // dialog's name says which job it is doing.
  const isNowPlaying = $derived(r != null && playerStore.currentSong?.id === r.song.id);
  const dialogTitle = $derived(isNowPlaying ? 'Now playing' : 'Song details');
  const dialogDescription = $derived(
    isAdmin(page.data.user)
      ? 'Artwork, lyrics, video, details, fingerprint and enrichment for this song.'
      : 'Artwork, lyrics, video and details for this song.'
  );

  // "Share with a friend…" opens the per-album grant sheet over Now Playing (nested, z-70, in the
  // media appearance), like its other sheets, so Done lands back on the song rather than on the
  // page underneath.
  let friendShare = $state<{ artist: string; album: string } | null>(null);
  let friendShareOpen = $state(false);
  function shareWithFriend() {
    if (!r) return;
    friendShare = { artist: r.album.artist, album: r.album.title };
    friendShareOpen = true;
  }

  // "Add to playlist…" likewise opens over Now Playing, for the song on screen.
  let playlistAdd = $state<{ songId: number; title: string } | null>(null);
  let playlistAddOpen = $state(false);
  function addToPlaylist() {
    if (!r) return;
    playlistAdd = { songId: r.song.id, title: r.song.title ?? r.song.fileName };
    playlistAddOpen = true;
  }

  // "Report wrong download…" too. A removal reloads the library, and the song then drops out of
  // the overlay's album like any other vanished song.
  let report = $state<{ songId: number; title: string } | null>(null);
  let reportOpen = $state(false);
  function reportSong() {
    if (!r) return;
    report = { songId: r.song.id, title: r.song.title ?? r.song.fileName };
    reportOpen = true;
  }

  // ── Drag to dismiss ─────────────────────────────────────────────────────────
  // A touch pulls the whole overlay down from anywhere on it; a mouse from the top bar and the
  // artwork (#lib/actions/drag-dismiss decides what is a pull). The overlay follows the finger with
  // no transition; on release it closes past 25% of its height or on a flick faster than
  // DISMISS_VELOCITY, else springs back. Transform/opacity only, written straight onto the
  // element. Under Reduce Motion the drag fades instead of moving.
  let contentEl = $state<HTMLElement | null>(null);
  let overlayEl = $state<HTMLElement | null>(null);
  let drag: { height: number; reduced: boolean } | null = null;

  function settle(el: HTMLElement | null) {
    if (!el) return;
    el.style.transition = `transform 350ms ${EASE_PRESENT}, opacity 350ms ${EASE_PRESENT}`;
    el.style.transform = '';
    el.style.opacity = '';
    el.addEventListener('transitionend', () => (el.style.transition = ''), { once: true });
  }

  const dismissDrag: DragDismissHandlers = {
    onstart() {
      drag = {
        height: contentEl?.offsetHeight || window.innerHeight,
        reduced: prefersReducedMotion()
      };
      if (contentEl) contentEl.style.transition = 'none';
      if (overlayEl) overlayEl.style.transition = 'none';
    },
    onmove(offset) {
      if (!drag || !contentEl) return;
      const progress = Math.min(1, offset / drag.height);
      if (drag.reduced) contentEl.style.opacity = String(1 - progress);
      else contentEl.style.transform = offset > 0 ? `translate3d(0, ${offset}px, 0)` : '';
      if (overlayEl) overlayEl.style.opacity = String(1 - progress);
    },
    onend(offset, velocity) {
      const ended = drag;
      drag = null;
      if (ended && (offset > ended.height * 0.25 || velocity > DISMISS_VELOCITY)) {
        // Close from where the finger left it: the exit keyframes only define `to`, so the
        // slide-out starts at the inline transform (and the scrim at its inline opacity).
        if (contentEl) contentEl.style.transition = '';
        if (overlayEl) overlayEl.style.transition = '';
        songDetail.close();
      } else {
        settle(contentEl);
        settle(overlayEl);
      }
    },
    oncancel() {
      drag = null;
      settle(contentEl);
      settle(overlayEl);
    }
  };

  // The page behind a full-screen Now Playing leaves the accessibility tree (see inert-app.ts).
  $effect(() => (songDetail.isOpen ? holdAppInert() : undefined));

  // A fresh open starts from a clean element (a drag-dismiss leaves its inline transform behind).
  $effect(() => {
    if (!songDetail.isOpen) return;
    untrack(() => {
      if (contentEl) {
        contentEl.style.transform = '';
        contentEl.style.opacity = '';
      }
      if (overlayEl) overlayEl.style.opacity = '';
    });
  });
</script>

<DialogPrimitive.Root open={songDetail.isOpen} onOpenChange={(open) => !open && songDetail.close()}>
  <DialogPrimitive.Portal>
    <DialogPrimitive.Overlay
      bind:ref={overlayEl}
      class={['mh-np-scrim fixed inset-0 z-[60]', surface]}
    />
    <!-- `dark` makes the overlay's tokens the dark set whatever the app theme (the media
         appearance); `mh-np` re-points a few of them to Now Playing's vibrancy tokens (below).
         --mh-content-pad is set here because this is portaled out of the shell that defines it.
         Safe-area padding is for the installed app and landscape notches; the backdrops span the
         padding box. -->
    <DialogPrimitive.Content
      bind:ref={contentEl}
      data-mh-desk={skinLook(undefined, '')}
      data-mh-tv-on={videoShowing || undefined}
      class={[
        'mh-np text-foreground fixed inset-0 z-[60] flex flex-col overflow-hidden pt-[env(safe-area-inset-top)] pr-[env(safe-area-inset-right)] pb-[env(safe-area-inset-bottom)] pl-[env(safe-area-inset-left)] focus-visible:outline-hidden',
        mediaAppearance(),
        surface
      ]}
      style="--mh-content-pad: 0px"
      onInteractOutside={keepOpenForToasts}
    >
      {#if r}
        <VideoBackdrop
          songId={r.song.id}
          {ambientUrl}
          {dimAlpha}
          {video}
          blurred={videoWatching}
          bind:showing={videoShowing}
        />
      {:else if ambientUrl}
        <img
          src={ambientUrl}
          alt=""
          aria-hidden="true"
          data-mh-decor="backdrop"
          class="pointer-events-none absolute inset-0 size-full scale-150 object-cover blur-[64px] saturate-150"
        />
        <div
          aria-hidden="true"
          data-mh-decor="backdrop"
          class="pointer-events-none absolute inset-0"
          style="background: rgb(0 0 0 / {dimAlpha})"
        ></div>
      {/if}

      <!-- Keep the departing dialog's own surface after its media claim is released. -->
      <div
        class="mh-surface-edges absolute inset-0"
        style="--mh-page-surface: var(--background)"
        aria-hidden="true"
      ></div>

      <DialogPrimitive.Title class="sr-only">{dialogTitle}</DialogPrimitive.Title>
      <DialogPrimitive.Description class="sr-only">{dialogDescription}</DialogPrimitive.Description>

      <!-- Over a moving music video, a soft dark halo behind every piece of text keeps it
           readable; over the still, dimmed cover it is not needed. -->
      <div
        use:dragDismiss={dismissDrag}
        class="relative z-10 flex h-full min-h-0 flex-col {videoShowing
          ? '[text-shadow:0_0_4px_rgb(0_0_0/0.6),0_1px_14px_rgb(0_0_0/0.5)]'
          : ''}"
      >
        {#if r}
          <TrackPanel
            album={r.album}
            song={r.song}
            trackIndex={r.index}
            requestedMode={songDetail.target?.mode}
            requestSeq={songDetail.target?.seq ?? 0}
            {video}
            onClose={() => songDetail.close()}
            {onResetEnrichment}
            timelineHref={`/track/${r.song.id}`}
            onShareWithFriend={shareWithFriend}
            onAddToPlaylist={addToPlaylist}
            onReport={reportSong}
            bind:watching={videoWatching}
          />
        {:else if detailState === 'error'}
          <div class="flex h-full flex-col items-center justify-center gap-4 px-8 text-center">
            <p class="text-body text-muted-foreground max-w-xs text-balance">
              Couldn’t load this song.
            </p>
            <div class="flex gap-3">
              <Button variant="gray" size="pill" onclick={() => songDetail.close()}>Close</Button>
              <Button variant="gray" size="pill" onclick={retryDetailAlbums}>Retry</Button>
            </div>
          </div>
        {:else if unresolvedTimedOut && !songsStore.isLoading}
          <div class="flex h-full flex-col items-center justify-center gap-4 px-8 text-center">
            <p class="text-body text-muted-foreground max-w-xs text-balance">
              This song isn’t in your library view any more.
            </p>
            <div class="flex gap-3">
              <Button variant="gray" size="pill" onclick={() => songDetail.close()}>Close</Button>
              <Button variant="gray" size="pill" onclick={retryResolve}>Try again</Button>
            </div>
          </div>
        {:else}
          <div
            class="text-subheadline text-muted-foreground flex h-full items-center justify-center gap-2"
          >
            <Loader2 class="size-4 animate-spin" /> Loading track…
          </div>
        {/if}
      </div>
    </DialogPrimitive.Content>
  </DialogPrimitive.Portal>
</DialogPrimitive.Root>

{#if playlistAdd}
  <AddToPlaylistSheet
    bind:open={playlistAddOpen}
    nested
    songIds={[playlistAdd.songId]}
    label={playlistAdd.title}
  />
{/if}

{#if report && isAdmin(page.data.user)}
  <ReportDownloadSheet bind:open={reportOpen} nested songId={report.songId} label={report.title} />
{/if}

{#if friendShare && isAdmin(page.data.user)}
  <ShareWithFriendDialog
    bind:open={friendShareOpen}
    nested
    artist={friendShare.artist}
    album={friendShare.album}
    onnavigate={() => songDetail.close()}
  />
{/if}

<style>
  /* Now Playing's vibrancy tokens — the one sanctioned use of alpha text. The dim
     layer keeps the BRIGHTEST part of the wash at or below ~70/255 grey (cover-dim.ts), so white
     clears 9.4:1, the 72% secondary tone 5.8:1 and the 55% inactive lyric lines 4.2:1; on a
     white-8% cell the 72% tone still clears 4.8:1. Secondary and tertiary text both map to the 72%
     tone (the tertiary grey would sit too low on a coloured wash); fills and hairlines are white
     washes that pick up the cover instead of a grey that reads as a smudge on colour.
     Colour over an arbitrary cover is the one thing no dim can promise: the brand green lands at
     ~2.5:1 on a magenta wash. So, as in Apple Music, what you tap here is white (the tint becomes
     the label colour — liked state is carried by the heart's fill, in a light green of its own),
     and the warning / destructive text tones lift to pastel variants that hold ≥ 4.5:1 on a cell
     over the wash. The focus ring is solid white: a 70% ring drawn at /50 was ~1.8:1.
     Menus and sheets opened from here are portaled out of this element and keep the plain dark
     tokens on their solid surfaces. The `.dark.mh-np` pair outranks the plain `.dark` block. */
  :global(.dark.mh-np) {
    --np-secondary: rgb(255 255 255 / 0.72);
    --np-fill: rgb(255 255 255 / 0.12);
    --np-liked: #4ce070;
    --primary: #ffffff;
    --primary-foreground: #000000;
    --ring: #ffffff;
    --warning-text: #ffc266;
    --destructive-text: #ffc2be;
    --muted-foreground: var(--np-secondary);
    --muted-foreground-dim: var(--np-secondary);
    --secondary: rgb(255 255 255 / 0.14);
    --secondary-hover: rgb(255 255 255 / 0.2);
    --muted: rgb(255 255 255 / 0.1);
    --accent: rgb(255 255 255 / 0.1);
    --input: rgb(255 255 255 / 0.12);
    --card: rgb(255 255 255 / 0.08);
    --separator: rgb(255 255 255 / 0.14);
    --border: rgb(255 255 255 / 0.16);
    --segmented-thumb: rgb(255 255 255 / 0.28);
  }
  @media (prefers-contrast: more) {
    :global(.dark.mh-np) {
      --np-secondary: rgb(255 255 255 / 0.86);
      --np-fill: rgb(255 255 255 / 0.2);
      --separator: rgb(255 255 255 / 0.4);
      --border: rgb(255 255 255 / 0.45);
    }
  }

  /* The rise and the fade-in fill `backwards` only: a filled animation outranks the inline
     transform/opacity that drag-to-dismiss writes, so `both` would leave the overlay deaf to the
     finger once the rise had run. The fall and fade-out keep `both` to hold their end state until
     bits-ui unmounts, and only define `to`, so a drag-to-dismiss closes from where the finger left
     it. */
  :global(.mh-np[data-state='open']) {
    animation: mh-np-rise 350ms cubic-bezier(0.32, 0.72, 0, 1) backwards;
  }
  :global(.mh-np[data-state='closed']) {
    animation: mh-np-fall 350ms cubic-bezier(0.32, 0.72, 0, 1) both;
  }
  :global(.mh-np-scrim[data-state='open']) {
    animation: mh-np-fade-in 350ms cubic-bezier(0.32, 0.72, 0, 1) backwards;
  }
  :global(.mh-np-scrim[data-state='closed']) {
    animation: mh-np-fade-out 350ms cubic-bezier(0.32, 0.72, 0, 1) both;
  }
  @keyframes mh-np-rise {
    from {
      transform: translate3d(0, 100%, 0);
    }
    to {
      transform: translate3d(0, 0, 0);
    }
  }
  @keyframes mh-np-fall {
    to {
      transform: translate3d(0, 100%, 0);
    }
  }
  @keyframes mh-np-fade-in {
    from {
      opacity: 0;
    }
  }
  @keyframes mh-np-fade-out {
    to {
      opacity: 0;
    }
  }
</style>

<script lang="ts">
  import { holdAppHidden, holdAppInert } from '#lib/actions/inert-app.ts';
  import { tick, untrack } from 'svelte';
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
  import {
    coverDimAlpha,
    DIM_UNKNOWN
  } from '#lib/components/file-browser/now-playing/cover-dim.ts';
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

  // The overlay's time on screen, beside `songDetail.isOpen` (what was asked for):
  // - `present`, from the open until the fall has finished. All that time the app is hidden from
  //   assistive technology with `aria-hidden`, which costs no restyle, once focus is in here.
  // - `covering` (songDetail.covering), from the end of the rise until the fall is nearly done.
  //   `inert` on the app and hiding the mini player and tab bar each restyle or re-lay the whole
  //   app; made the moment the overlay opens or closes, that work lands in the first frames of the
  //   350ms rise or fall, which carry most of its distance, and the slide jumps.
  let present = $state(false);
  let focusedIn = $state(false);
  const covering = $derived(songDetail.covering);
  $effect(() => () => songDetail.setCovering(false));
  let opener: HTMLElement | null = null;
  $effect(() => {
    if (!songDetail.isOpen) return;
    present = true;
    // Reopened mid-fall, focus is still in the departing dialog: the opener is the one before it.
    untrack(() => {
      const active = document.activeElement;
      if (active instanceof HTMLElement && !contentEl?.contains(active)) opener = active;
    });
  });
  $effect(() => {
    const open = songDetail.isOpen;
    const el = contentEl;
    if (open && !el) return;
    let cancelled = false;
    // The slide is a transition that bits-ui starts by setting `data-starting-style` or
    // `data-ending-style` in its own update, after this one: it can be read a frame later.
    requestAnimationFrame(() => {
      if (cancelled) return;
      const slides = el?.getAnimations() ?? [];
      const slid = Promise.allSettled(slides.map((a) => a.finished));
      if (open) {
        void slid.then(() => {
          if (!cancelled) songDetail.setCovering(true);
        });
        return;
      }
      // 40% of the fall's time is ~90% of its distance on this curve: the mini player and tab bar
      // are back as their place is uncovered, and the restyle lands in the slow tail.
      void Promise.race([slid, wait(timeUntil(slides, 0.4))]).then(() => {
        if (!cancelled) songDetail.setCovering(false);
      });
      void slid.then(async () => {
        if (cancelled) return;
        present = false;
        focusedIn = false;
        await tick();
        returnFocus(el);
      });
    });
    return () => {
      cancelled = true;
    };
  });

  function timeUntil(slides: Animation[], fraction: number): number {
    const slide = slides[0];
    const total = Number(slide?.effect?.getComputedTiming().duration ?? 0);
    return Math.max(0, total * fraction - Number(slide?.currentTime ?? 0));
  }

  function wait(ms: number): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, ms));
  }

  // bits-ui moves focus in here a frame after the open. Chrome will not hide an element that holds
  // focus, and keeps refusing once it has, so focus moves first and the app is hidden after it.
  function focusIn(event: Event) {
    event.preventDefault();
    const dialog = contentEl;
    if (!dialog) return;
    if (!focusFirst(dialog.querySelectorAll<HTMLElement>(FOCUSABLE))) {
      dialog.focus({ preventScroll: true });
    }
    focusedIn = true;
  }

  const FOCUSABLE = ['button', '[href]', 'input', 'select', 'textarea', '[tabindex]']
    .map((tag) => `${tag}:not([disabled]):not([tabindex="-1"])`)
    .join(', ');

  /** Focuses the first that takes it: one hidden by CSS for another layout does not. */
  function focusFirst(candidates: Iterable<HTMLElement>): boolean {
    for (const el of candidates) {
      if (!el.matches(FOCUSABLE) || el.closest('[aria-hidden="true"]')) continue;
      el.focus({ preventScroll: true });
      if (document.activeElement === el) return true;
    }
    return false;
  }

  // bits-ui lets go of its focus trap as the overlay starts closing, and the app stops being inert
  // 40% into the fall: Tab would walk out of a dialog still on screen into an app still hidden from
  // assistive technology. Until the fall is over, Tab stays where it is, unless focus is in a sheet
  // that outlives the overlay, which keeps its own trap.
  const leaving = $derived(present && !songDetail.isOpen);
  $effect(() => {
    if (!leaving) return;
    const holdTab = (event: KeyboardEvent) => {
      const active = document.activeElement;
      const here = !active || active === document.body || (contentEl?.contains(active) ?? false);
      if (event.key === 'Tab' && here) event.preventDefault();
    };
    document.addEventListener('keydown', holdTab, true);
    return () => document.removeEventListener('keydown', holdTab, true);
  });

  // bits-ui returns focus as the fall starts, while the app is still inert and hidden, where
  // focus() is ignored; so the overlay returns it itself once the fall is over, once per open.
  function returnFocus(dialog: HTMLElement | null) {
    const target = opener;
    if (!target) return;
    opener = null;
    const active = document.activeElement;
    const lost = !active || active === document.body || (dialog?.contains(active) ?? false);
    if (!lost) return;
    // Opened from the mini player, the button pressed was unmounted with it; it is back by now.
    if (target.isConnected) target.focus({ preventScroll: true });
    else focusFirst(document.querySelectorAll<HTMLElement>('[data-mh-np-opener]'));
  }

  // Hide the global MiniPlayer while the overlay covers it (it has its own transport).
  $effect(() => (covering ? playerStore.registerPanel() : undefined));

  // The status bar follows the media appearance (black) while this is up. The claim is a token,
  // released when the overlay closes or this unmounts. It only repaints <html> and <body>
  // (`--mh-page-surface` does not inherit, app.css), so it can change with the slide.
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
        // Close from where the finger left it: the inline values go in the same frame as the close
        // starts, so the fall (and the scrim's fade) runs on from them.
        for (const el of [contentEl, overlayEl]) {
          if (!el) continue;
          el.style.transition = '';
          el.style.transform = '';
          el.style.opacity = '';
        }
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
  // `inert` waits for the rise (see `covering`); from the open to the end of the fall, `aria-hidden`
  // keeps a screen reader from walking into the page behind.
  const hideApp = $derived(present && focusedIn);
  $effect(() => (hideApp ? holdAppHidden() : undefined));
  $effect(() => (covering ? holdAppInert() : undefined));

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
      onOpenAutoFocus={focusIn}
      onCloseAutoFocus={(e) => e.preventDefault()}
      preventBodyPointerEvents={false}
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
            playFromList={songDetail.target?.play}
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
  /* Now Playing's vibrancy tokens (`.dark.mh-np`) live in app.css so the public share player shares them.
     The slide is a transition between bits-ui's presence states rather than keyframes, so a close
     during the rise, or an open during the fall, turns round where the panel is instead of first
     jumping to the far end. bits-ui marks the first frame `data-starting-style` and the close
     `data-ending-style`, and unmounts once the transition has finished. Drag-to-dismiss holds the
     panel with an inline transform under `transition: none` and clears both as it closes, so the
     fall starts where the finger let go. */
  :global(.mh-np) {
    transition: transform 350ms cubic-bezier(0.32, 0.72, 0, 1);
  }
  :global(.mh-np[data-starting-style]),
  :global(.mh-np[data-ending-style]) {
    transform: translate3d(0, 100%, 0);
  }
  :global(.mh-np-scrim) {
    transition: opacity 350ms cubic-bezier(0.32, 0.72, 0, 1);
  }
  :global(.mh-np-scrim[data-starting-style]),
  :global(.mh-np-scrim[data-ending-style]) {
    opacity: 0;
  }
</style>

<script lang="ts">
  import {
    Disc3,
    EyeOff,
    Heart,
    HeartOff,
    Maximize2,
    Mic2,
    Pause,
    Play,
    Quote,
    Rewind,
    FastForward,
    Volume2,
    VolumeX,
    X
  } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { fly } from 'svelte/transition';
  import { motionFly } from '$lib/motion';
  import { cubicOut } from 'svelte/easing';
  import { goto } from '$app/navigation';
  import { playerStore } from '$lib/stores/player.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { seekTargetForKey } from '$lib/player-seek';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { longpress, type LongPressPoint } from '$lib/actions/long-press';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { albumKeyForSong } from '$lib/api-client';
  import { artistOf } from '$lib/track-list-view.svelte';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { formatDuration } from '$lib/formatters';
  import { blurAfterPointerClick, cn, transportGlyphClass } from '$lib/utils';

  // Two layouts. Compact (below md): an iOS mini player — a glass capsule docked above the tab bar
  // (or whatever owns the bottom slot), art · title/artist · play · next, the whole bar opening
  // Now Playing on a tap or a swipe up, and everything else behind a long-press. md+: the macOS
  // Music bar, centred, with the seek line, like, lyrics, volume and hide.
  // The compact bar positions from the geometry vars in app.css, like the tab bar and the content
  // padding; the md+ bar sits 12px off the bottom, which app.css's md+ 88px clearance allows for.
  const isMobile = new IsMobile();

  function miniExit() {
    return motionFly({ y: 8, duration: 200, easing: cubicOut });
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

  // Liked state comes from the library row (PlayerSong doesn't carry it); the
  // lookup misses for non-library playback (e.g. share links), hiding the heart.
  const librarySong = $derived.by(() => {
    const id = playerStore.currentSong?.id;
    return id == null ? undefined : songsStore.songs.find((s) => s.id === id);
  });
  const isLiked = $derived(Boolean(librarySong?.likedAtUtc));

  async function toggleLike() {
    if (!librarySong) return;
    try {
      await songsStore.toggleLike(librarySong.id);
    } catch (err) {
      toast.error('Could not update favourites', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }

  function openNowPlaying() {
    const id = playerStore.currentSong?.id;
    if (id != null) songDetail.open(id);
  }

  // ── compact: long-press menu ──────────────────────────────────────────────
  // iOS never sends `contextmenu` for a touch-and-hold, so the menu opens from the longpress action
  // (and from a right-click, for a narrow desktop window). It anchors at the finger via an
  // invisible trigger placed at that point. Its visible twins are the heart and the ⋯ menu in Now
  // Playing, where "Hide player" also lives.
  let menuOpen = $state(false);
  let menuPoint = $state({ x: 0, y: 0 });
  let barEl: HTMLDivElement | null = $state(null);

  // At the finger's x but on the capsule's top edge, so the menu rises above the whole bar (as an
  // iOS context menu clears the thing it was opened on) instead of covering its titles.
  function openMenuAt(point: LongPressPoint) {
    const rect = barEl?.getBoundingClientRect();
    menuPoint = { x: rect ? point.x - rect.left : 0, y: 0 };
    menuOpen = true;
  }

  function onContextMenu(e: MouseEvent) {
    e.preventDefault();
    openMenuAt({ x: e.clientX, y: e.clientY });
  }

  const albumHref = $derived(
    librarySong ? `/library?album=${encodeURIComponent(albumKeyForSong(librarySong))}` : null
  );
  const artistHref = $derived(
    librarySong ? `/library?artist=${encodeURIComponent(artistOf(librarySong))}` : null
  );

  // ── compact: swipe up to open ─────────────────────────────────────────────
  // A mostly-vertical upward drag past a small threshold opens Now Playing, the way the iOS
  // mini player expands. A mouse keeps plain clicks.
  const SWIPE_OPEN_PX = 24;
  let swipe: { id: number; x: number; y: number } | null = null;

  function onSwipeDown(e: PointerEvent) {
    if (e.pointerType === 'mouse' || !e.isPrimary) return;
    swipe = { id: e.pointerId, x: e.clientX, y: e.clientY };
  }
  function onSwipeMove(e: PointerEvent) {
    if (!swipe || e.pointerId !== swipe.id) return;
    const dx = e.clientX - swipe.x;
    const dy = e.clientY - swipe.y;
    if (dy < -SWIPE_OPEN_PX && Math.abs(dy) > Math.abs(dx)) {
      swipe = null;
      openNowPlaying();
    }
  }
  function onSwipeEnd(e: PointerEvent) {
    if (swipe && e.pointerId === swipe.id) swipe = null;
  }

  let seekEl: HTMLDivElement | null = $state(null);
  // Drives the thumb + thickened track on a touch drag, which produces no `:hover` at all
  // (Tailwind gates `hover:` behind `@media (hover: hover)`).
  let seekDragging = $state(false);

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
    seekDragging = true;
    seekToClientX(e.clientX);
  }

  function onSeekPointerMove(e: PointerEvent) {
    const el = e.currentTarget as HTMLDivElement;
    if (!el.hasPointerCapture(e.pointerId)) return;
    seekToClientX(e.clientX);
  }

  function onSeekPointerUp() {
    seekDragging = false;
  }

  function onSeekKeyDown(e: KeyboardEvent) {
    if (!canSeek) return;
    const next = seekTargetForKey(e.key, playerStore.currentTime, playerStore.duration);
    if (next === null) return;
    e.preventDefault();
    playerStore.seek(next);
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

{#if playerStore.currentSong && !playerStore.isPanelMounted && !playerStore.isMiniPlayerDismissed}
  {@const song = playerStore.currentSong}
  {#if isMobile.current}
    <!-- Compact: a glass capsule, 56px, docked 8px above the tab bar / decision toolbar
         (--mh-player-bottom). No progress line and no close button — Hide player is in the
         long-press menu and in Now Playing's ⋯ menu. touch-none so a swipe up reaches us instead
         of scrolling nothing. -->
    <div
      bind:this={barEl}
      role="group"
      aria-label="Now playing"
      use:longpress={{ onlongpress: openMenuAt }}
      oncontextmenu={onContextMenu}
      onpointerdown={onSwipeDown}
      onpointermove={onSwipeMove}
      onpointerup={onSwipeEnd}
      onpointercancel={onSwipeEnd}
      class="mh-mini-enter mh-glass mh-chrome fixed right-[max(16px,env(safe-area-inset-right))] bottom-(--mh-player-bottom) left-[max(16px,env(safe-area-inset-left))] z-40 flex h-(--mh-player-h) touch-none items-center rounded-full pr-1 select-none"
      out:fly={miniExit()}
    >
      <!-- The bar's main target: art + titles, stretching to the transport. It owns the leading
           inset, so the capsule's rounded end opens Now Playing too.
           The art sits in the capsule's end cap the way Apple Music's does: a 36px square with a
           quarter-size 9px radius, 14px in from the leading edge. That puts its corner curves
           roughly concentric with the cap's semicircle, with about the same gap there as above and
           below the art. A 40px square with 6px corners at 8px came within ~3px of the capsule at
           its corners and read as a hard square pushed into a round end. -->
      <button
        type="button"
        onclick={openNowPlaying}
        aria-label="Open now playing"
        class="focus-visible:ring-ring flex h-full min-w-0 flex-1 items-center gap-3 rounded-full pl-3.5 text-left outline-none focus-visible:ring-2 focus-visible:ring-inset"
      >
        <Cover
          artist={song.artist}
          title={song.title}
          coverUrl={song.coverUrl ?? null}
          size={36}
          corner={9}
          caption={false}
          class="size-9 shrink-0"
        />
        <span class="flex min-w-0 flex-col">
          <span class="text-subheadline truncate font-semibold">{song.title}</span>
          <span class="text-footnote text-muted-foreground truncate">{song.artist}</span>
        </span>
      </button>
      <Button
        variant="ghost"
        size="icon"
        class={cn(transportGlyphClass, 'size-11 shrink-0')}
        onclick={(e) => {
          blurAfterPointerClick(e);
          playerStore.togglePlay();
        }}
        aria-label={playerStore.isPlaying ? 'Pause' : 'Play'}
      >
        {#if playerStore.isPlaying}
          <Pause class="size-6" fill="currentColor" strokeWidth={1.5} />
        {:else}
          <Play class="size-6 translate-x-px" fill="currentColor" strokeWidth={1.5} />
        {/if}
      </Button>
      <Button
        variant="ghost"
        size="icon"
        class={cn(transportGlyphClass, 'size-11 shrink-0 disabled:opacity-40')}
        onclick={(e) => {
          blurAfterPointerClick(e);
          playerStore.playNext();
        }}
        disabled={!playerStore.hasNext}
        aria-label="Next track"
      >
        <FastForward class="size-6" fill="currentColor" strokeWidth={1.5} />
      </Button>

      <DropdownMenu.Root bind:open={menuOpen}>
        <!-- Invisible anchor at the finger; never a tap target of its own. -->
        <DropdownMenu.Trigger>
          {#snippet child({ props })}
            <span
              {...props}
              tabindex="-1"
              aria-hidden="true"
              class="pointer-events-none absolute size-px"
              style="left: {menuPoint.x}px; top: {menuPoint.y}px"
            ></span>
          {/snippet}
        </DropdownMenu.Trigger>
        <DropdownMenu.Content side="top" align="center" sideOffset={8} class="w-60 pointer-coarse:w-72">
          {#if librarySong}
            <DropdownMenu.Group>
              <DropdownMenu.Item onSelect={toggleLike}>
                {#if isLiked}
                  <HeartOff /> Remove from favourites
                {:else}
                  <Heart /> Add to favourites
                {/if}
              </DropdownMenu.Item>
              {#if albumHref}
                <DropdownMenu.Item onSelect={() => void goto(albumHref)}>
                  <Disc3 /> Go to album
                </DropdownMenu.Item>
              {/if}
              {#if artistHref}
                <DropdownMenu.Item onSelect={() => void goto(artistHref)}>
                  <Mic2 /> Go to artist
                </DropdownMenu.Item>
              {/if}
            </DropdownMenu.Group>
            <DropdownMenu.Separator />
          {/if}
          <DropdownMenu.Item onSelect={() => playerStore.dismissMiniPlayer()}>
            <EyeOff /> Hide player
          </DropdownMenu.Item>
        </DropdownMenu.Content>
      </DropdownMenu.Root>
    </div>
  {:else}
    <!-- md+: the macOS Music bar — transport leading, now-playing centre with a seek line,
         actions trailing — centred over the page, 12px off the bottom. -->
    <div
      class="mh-mini-enter mh-glass mh-chrome fixed bottom-3 left-1/2 z-40 w-[calc(100%-1.5rem)] max-w-3xl -translate-x-1/2 overflow-hidden rounded-2xl"
      out:fly={miniExit()}
    >
      <div class="flex h-16 items-center gap-3 px-4">
        <!-- LEFT: transport. Apple Music bar style: naked solid glyphs, no hover
             wash (a translucent circle reads as smudge in dark mode) — feedback
             is press-scale on the glyph itself. -->
        <div class="flex shrink-0 items-center gap-1">
          <Button
            variant="ghost"
            size="icon"
            class={cn(transportGlyphClass, 'size-9 shrink-0 disabled:opacity-30')}
            onclick={(e) => {
              blurAfterPointerClick(e);
              playerStore.playPrevious();
            }}
            disabled={!playerStore.hasPrevious}
            aria-label="Previous track"
          >
            <Rewind class="size-5" fill="currentColor" />
          </Button>

          <Button
            variant="ghost"
            size="icon"
            class={cn(transportGlyphClass, 'size-9 shrink-0')}
            onclick={(e) => {
              blurAfterPointerClick(e);
              playerStore.togglePlay();
            }}
            aria-label={playerStore.isPlaying ? 'Pause' : 'Play'}
          >
            {#if playerStore.isPlaying}
              <Pause class="size-5.5" fill="currentColor" />
            {:else}
              <Play class="size-5.5 translate-x-px" fill="currentColor" />
            {/if}
          </Button>

          <Button
            variant="ghost"
            size="icon"
            class={cn(transportGlyphClass, 'size-9 shrink-0 disabled:opacity-30')}
            onclick={(e) => {
              blurAfterPointerClick(e);
              playerStore.playNext();
            }}
            disabled={!playerStore.hasNext}
            aria-label="Next track"
          >
            <FastForward class="size-5" fill="currentColor" />
          </Button>
        </div>

        <!-- CENTER: now-playing, LEFT-aligned — art + title/artist with a slim seek
             line directly under it (Apple-Music compact bar). -->
        <div class="flex min-w-0 flex-1 flex-col items-start justify-center gap-1">
          <button
            type="button"
            onclick={openNowPlaying}
            aria-label="Open now playing"
            class="group/np focus-visible:ring-ring flex max-w-full min-w-0 items-center gap-2.5 rounded-md px-1.5 py-0.5 text-left outline-none focus-visible:ring-2"
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

          <!-- Slim seek line under the now-playing block; thickens on hover/focus/drag so
               it's easy to grab and drag. The visible line stays 12px tall; an invisible
               `after:` pseudo-element (the same expanded-hit-region pattern as ui/switch) grows
               the real hit target to 28px without widening the row it sits in. It needs its
               horizontal insets as well: with only `inset-y` it is 0px wide and catches nothing. -->
          <div
            bind:this={seekEl}
            role="slider"
            tabindex="0"
            aria-valuemin={0}
            aria-valuemax={100}
            aria-valuenow={Math.round(progress * 100)}
            aria-valuetext={`${formatDuration(playerStore.currentTime)} of ${formatDuration(playerStore.duration)}`}
            aria-label="Seek"
            class="group/seek relative flex h-3 w-full max-w-[440px] cursor-pointer touch-none items-center rounded-full outline-none select-none after:absolute after:inset-x-0 after:-inset-y-2 after:content-['']"
            onpointerdown={onSeekPointerDown}
            onpointermove={onSeekPointerMove}
            onpointerup={onSeekPointerUp}
            onpointercancel={onSeekPointerUp}
            onlostpointercapture={onSeekPointerUp}
            onkeydown={onSeekKeyDown}
          >
            <div
              class={cn(
                'bg-foreground/20 relative h-[3px] w-full overflow-hidden rounded-full transition-[height] duration-150 ease-out group-hover/seek:h-[7px] group-focus-visible/seek:h-[7px] motion-reduce:transition-none',
                seekDragging && 'h-[7px]'
              )}
            >
              <div
                class="bg-foreground/45 group-hover/seek:bg-primary group-focus-visible/seek:bg-primary absolute inset-0 origin-left rounded-full transition-colors"
                style="transform: scaleX({progress})"
              ></div>
            </div>
            <div
              class={cn(
                'border-ring pointer-events-none absolute size-3 -translate-x-1/2 rounded-full border bg-white opacity-0 shadow-sm transition-opacity group-hover/seek:opacity-100 group-focus-visible/seek:opacity-100 pointer-coarse:opacity-100',
                seekDragging && 'opacity-100'
              )}
              style="left: {progress * 100}%"
            ></div>
          </div>
        </div>

        <!-- RIGHT: actions -->
        <div class="flex shrink-0 items-center gap-1">
          {#if librarySong}
            <Button
              variant="ghost"
              size="icon"
              class="{isLiked
                ? 'text-primary hover:text-primary'
                : 'text-muted-foreground hover:text-foreground'} size-8 shrink-0 active:scale-90"
              onclick={toggleLike}
              aria-label={isLiked ? 'Remove from favourites' : 'Add to favourites'}
              aria-pressed={isLiked}
            >
              <Heart class="size-4" fill={isLiked ? 'currentColor' : 'none'} />
            </Button>
          {/if}
          <Button
            variant="ghost"
            size="icon"
            class="text-muted-foreground hover:text-foreground size-8"
            onclick={openNowPlaying}
            aria-label="Lyrics"
          >
            <Quote class="size-4" />
          </Button>

          <!-- Mouse/trackpad only: a plain in-app slider is not the platform's volume control on
               touch, where the hardware buttons already do that job (Apple discourages an
               in-app volume slider on mobile). Gated on pointer precision, not viewport width, so
               a touch-first iPad in this width range hides it too. -->
          <div class="hidden items-center gap-1.5 pointer-fine:flex">
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
              aria-valuetext={`Volume ${Math.round(playerStore.volume * 100)}%`}
              aria-label="Volume"
              class="group relative flex h-3 w-16 shrink-0 cursor-pointer touch-none items-center select-none after:absolute after:inset-x-0 after:-inset-y-2 after:content-['']"
              onpointerdown={onVolumePointerDown}
              onpointermove={onVolumePointerMove}
              onkeydown={onVolumeKeyDown}
            >
              <div class="bg-foreground/15 relative h-1 w-full overflow-hidden rounded-full">
                <div
                  class="bg-foreground/55 group-hover:bg-primary absolute inset-0 origin-left rounded-full transition-colors"
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
            onclick={() => playerStore.dismissMiniPlayer()}
            aria-label="Hide player"
          >
            <X class="size-4" />
          </Button>
        </div>
      </div>
    </div>
  {/if}
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

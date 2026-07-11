<script lang="ts">
  import { page } from '$app/state';
  import { fly } from 'svelte/transition';
  import {
    ChevronDown,
    FastForward,
    Loader2,
    Maximize2,
    Music,
    Pause,
    Play,
    Rewind
  } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import Scrubber from '$lib/components/file-browser/Scrubber.svelte';
  import LyricsPanel from '$lib/components/file-browser/LyricsPanel.svelte';
  import { playerStore, type PlayerSong } from '$lib/stores/player.svelte';
  import { formatDuration } from '$lib/formatters';
  import {
    fetchShareLyrics,
    shareCoverUrl,
    shareStreamUrl,
    type ShareLyrics,
    type ShareTrack
  } from '$lib/share-client';
  import type { PageProps } from './$types';

  const { data }: PageProps = $props();

  const payload = $derived(data.payload);
  const tracks = $derived(payload?.tracks ?? []);
  const isAlbumShare = $derived(tracks.length > 1);

  // The track whose art/metadata/lyrics the page is presenting. Starts at the shared song
  // and follows playback as the album queue advances.
  let followedId = $state<number | null>(null);
  const activeId = $derived(followedId ?? payload?.sharedSongId ?? -1);
  const activeTrack = $derived(tracks.find((t) => t.id === activeId) ?? tracks[0]);

  $effect(() => {
    const playingId = playerStore.currentSong?.id;
    if (playingId != null && tracks.some((t) => t.id === playingId)) followedId = playingId;
  });

  const albumArtist = $derived(payload?.album.artist ?? activeTrack?.artist ?? 'Unknown artist');
  const displayArtist = $derived(activeTrack?.artist ?? albumArtist);
  const albumLabel = $derived(
    [payload?.album.title, payload?.album.year].filter(Boolean).join(' · ')
  );

  const coverUrl = $derived(
    activeTrack?.hasCoverArt ? shareCoverUrl(data.token, activeTrack.id) : null
  );
  const ambientUrl = $derived(
    activeTrack?.hasCoverArt ? shareCoverUrl(data.token, activeTrack.id, 600) : null
  );
  // Absolute URL for link-preview crawlers (og:image must not be relative).
  const ogImage = $derived(coverUrl ? new URL(coverUrl, page.url.origin).href : null);
  const pageTitle = $derived(
    payload && activeTrack
      ? isAlbumShare
        ? `${payload.album.title ?? 'Album'} — ${albumArtist}`
        : `${activeTrack.title} — ${displayArtist}`
      : 'Shared music'
  );

  function toPlayerSong(track: ShareTrack): PlayerSong {
    return {
      id: track.id,
      title: track.title,
      artist: track.artist ?? albumArtist,
      streamUrl: shareStreamUrl(data.token, track.id),
      coverUrl: track.hasCoverArt ? shareCoverUrl(data.token, track.id) : null,
      album: payload?.album.title ?? null
    };
  }

  const isCurrentlyLoaded = $derived(
    activeTrack != null && playerStore.currentSong?.id === activeTrack.id
  );
  const isCurrentlyPlaying = $derived(isCurrentlyLoaded && playerStore.isPlaying);
  const canGoPrevious = $derived(isCurrentlyLoaded && playerStore.hasPrevious);
  const canGoNext = $derived(isCurrentlyLoaded && playerStore.hasNext);

  function playTrack(track: ShareTrack) {
    const queue = tracks.map(toPlayerSong);
    void playerStore.playSong(
      toPlayerSong(track),
      queue,
      tracks.findIndex((t) => t.id === track.id)
    );
  }

  function handlePlayToggle() {
    if (!activeTrack) return;
    if (isCurrentlyLoaded) {
      playerStore.togglePlay();
      return;
    }
    playTrack(activeTrack);
  }

  // Lyrics for the active track, loaded from the share's anonymous lyrics endpoint. The
  // LyricsPanel is only mounted once text has arrived (keyed per track) so its internal
  // auth-only auto-fetch never fires for anonymous visitors.
  let lyrics = $state<ShareLyrics | null>(null);
  let lyricsForId = $state<number | null>(null);
  let lyricsLoading = $state(false);

  $effect(() => {
    const track = activeTrack;
    if (!track || lyricsForId === track.id) return;
    if (!track.hasSyncedLyrics && !track.hasPlainLyrics) {
      lyrics = null;
      lyricsForId = track.id;
      return;
    }
    lyricsLoading = true;
    fetchShareLyrics(data.token, track.id)
      .then((result) => {
        if (activeTrack?.id !== track.id) return; // switched away while in flight
        lyrics = result;
        lyricsForId = track.id;
      })
      .catch(() => {
        lyrics = null;
        lyricsForId = track.id;
      })
      .finally(() => (lyricsLoading = false));
  });

  const showLyricsSection = $derived(
    activeTrack != null &&
      (activeTrack.hasSyncedLyrics || activeTrack.hasPlainLyrics || activeTrack.isInstrumental)
  );
  const lyricsReady = $derived(lyrics !== null && lyricsForId === activeTrack?.id);
  const canExpandLyrics = $derived(
    activeTrack != null && activeTrack.isInstrumental !== true && lyricsReady && lyrics !== null
  );

  // Mobile fullscreen lyrics overlay (Apple Music / Spotify style): just the lyrics with a
  // scrubber + play/pause, no page scrolling.
  let lyricsExpanded = $state(false);

  $effect(() => {
    if (!canExpandLyrics) lyricsExpanded = false;
  });

  // Lock body scroll while the fullscreen lyrics overlay is open.
  $effect(() => {
    if (!lyricsExpanded) return;
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = prev;
    };
  });

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  const activeDurationSeconds = $derived(
    activeTrack?.durationMs ? activeTrack.durationMs / 1000 : 0
  );
</script>

<svelte:window
  onkeydown={(e) => {
    if (e.key === 'Escape' && lyricsExpanded) lyricsExpanded = false;
  }}
/>

<svelte:head>
  <title>{pageTitle}</title>
  <meta name="robots" content="noindex" />
  {#if payload && activeTrack}
    <meta property="og:type" content="music.song" />
    <meta property="og:title" content={pageTitle} />
    <meta
      property="og:description"
      content={isAlbumShare
        ? `Listen to ${tracks.length} tracks, shared from a MusicHoarder library.`
        : `Listen to “${activeTrack.title}”, shared from a MusicHoarder library.`}
    />
    {#if ogImage}
      <meta property="og:image" content={ogImage} />
    {/if}
  {/if}
</svelte:head>

{#snippet transport()}
  <div class="mx-auto mt-6 w-full max-w-[340px] lg:mx-0">
    <Scrubber isActive={isCurrentlyLoaded} fallbackDuration={activeDurationSeconds} />
    <div class="mt-1.5 flex items-center gap-3">
      <span class="text-muted-foreground w-10 shrink-0 text-right text-xs tabular-nums">
        {isCurrentlyLoaded ? formatTime(playerStore.currentTime) : '0:00'}
      </span>
      <div class="mx-auto flex items-center gap-2">
        <Button
          variant="ghost"
          size="icon"
          class="text-foreground hover:text-foreground size-9 bg-transparent transition-transform duration-100 ease-out hover:bg-transparent active:scale-90 disabled:opacity-30 dark:hover:bg-transparent"
          onclick={() => playerStore.playPrevious()}
          disabled={!canGoPrevious}
          aria-label="Previous track"
        >
          <Rewind class="size-5.5" fill="currentColor" />
        </Button>
        <Button
          variant="ghost"
          size="icon"
          class="text-foreground hover:text-foreground size-11 bg-transparent transition-transform duration-100 ease-out hover:bg-transparent active:scale-90 dark:hover:bg-transparent"
          onclick={handlePlayToggle}
          aria-label={isCurrentlyPlaying ? 'Pause' : 'Play'}
        >
          {#if isCurrentlyPlaying}
            <Pause class="size-7" fill="currentColor" />
          {:else}
            <Play class="size-7 translate-x-px" fill="currentColor" />
          {/if}
        </Button>
        <Button
          variant="ghost"
          size="icon"
          class="text-foreground hover:text-foreground size-9 bg-transparent transition-transform duration-100 ease-out hover:bg-transparent active:scale-90 disabled:opacity-30 dark:hover:bg-transparent"
          onclick={() => playerStore.playNext()}
          disabled={!canGoNext}
          aria-label="Next track"
        >
          <FastForward class="size-5.5" fill="currentColor" />
        </Button>
      </div>
      <span class="text-muted-foreground w-10 shrink-0 text-xs tabular-nums">
        {formatDuration(activeDurationSeconds)}
      </span>
    </div>
  </div>
{/snippet}

{#snippet trackRows()}
  {#each tracks as track, i (track.id)}
    {@const isRowLoaded = playerStore.currentSong?.id === track.id}
    {@const isRowPlaying = isRowLoaded && playerStore.isPlaying}
    <li>
      <button
        type="button"
        onclick={() => playTrack(track)}
        class="hover:bg-foreground/5 group flex w-full items-center gap-3 px-4 py-2.5 text-left transition-colors"
      >
        <span
          class="text-muted-foreground w-6 shrink-0 text-right text-xs tabular-nums group-hover:hidden {isRowLoaded
            ? 'hidden'
            : ''}"
        >
          {track.trackNumber ?? i + 1}
        </span>
        <span
          class="w-6 shrink-0 {isRowLoaded ? 'inline-flex' : 'hidden group-hover:inline-flex'} justify-end"
        >
          {#if isRowPlaying}
            <Pause class="text-primary size-3.5" fill="currentColor" />
          {:else}
            <Play class="text-primary size-3.5" fill="currentColor" />
          {/if}
        </span>
        <span
          class="min-w-0 flex-1 truncate text-sm {isRowLoaded ? 'text-primary font-medium' : ''}"
        >
          {track.title}
        </span>
        <span class="text-muted-foreground shrink-0 text-xs tabular-nums">
          {formatDuration(track.durationMs ? track.durationMs / 1000 : null)}
        </span>
      </button>
    </li>
  {/each}
{/snippet}

{#snippet sharedVia()}
  Shared via
  <a href="/" class="text-foreground/70 hover:text-foreground font-medium hover:underline">
    MusicHoarder
  </a>
{/snippet}

<!-- Public share page: no app chrome, no auth — just the shared music, presented with the
     same ambient-artwork treatment as the in-app now-playing overlay. -->
<div class="bg-background text-foreground relative min-h-dvh" style="--mh-content-pad: 0px">
  {#if ambientUrl}
    <img
      src={ambientUrl}
      alt=""
      aria-hidden="true"
      class="fixed inset-0 size-full scale-110 object-cover opacity-50 blur-3xl"
    />
  {/if}
  <!-- Plain translucent scrim, deliberately NOT backdrop-blur: unlike the in-app overlay
       (which locks body scroll), this page scrolls over the fixed backdrop on mobile, and a
       full-viewport backdrop-filter forces a re-raster on every scrolled frame — it stalls
       the compositor outright. The ambient img's own blur is static and cheap. -->
  <div class="bg-background/80 fixed inset-0"></div>

  {#if !payload || !activeTrack}
    <main class="relative z-10 flex min-h-dvh flex-col items-center justify-center gap-3 px-6">
      <Music class="text-muted-foreground size-10 opacity-40" />
      <h1 class="text-lg font-semibold">This link isn’t available</h1>
      <p class="text-muted-foreground max-w-sm text-center text-sm">
        The share link doesn’t exist or has been revoked by the person who created it.
      </p>
    </main>
  {:else}
    <!-- Mobile: single scrolling column (hero → lyrics card → tracklist). Desktop: the
         in-app track-panel layout — fixed left rail (art, title, transport, tracklist)
         with the theater lyrics filling the right column, no page scroll. -->
    <main
      class="relative z-10 mx-auto flex w-full max-w-xl flex-col items-center px-6 pt-12 pb-14 sm:pt-16 lg:h-dvh lg:max-w-6xl lg:flex-row lg:items-stretch lg:justify-center lg:gap-12 lg:overflow-hidden lg:px-10 lg:pt-10 lg:pb-10 xl:gap-16"
    >
      <!-- Left rail (desktop) / hero (mobile) -->
      <div
        class="flex w-full flex-col items-center lg:h-full lg:w-[340px] lg:shrink-0 lg:items-stretch lg:justify-center"
      >
        <Cover
          artist={displayArtist}
          title={payload.album.title ?? activeTrack.title}
          {coverUrl}
          size={340}
          corner={12}
          caption={false}
          class="aspect-square !h-auto w-64 shrink-0 !shadow-[0_24px_48px_rgba(0,0,0,0.45)] sm:w-72 lg:w-full"
        />

        <div class="mt-6 w-full text-center lg:text-left">
          <h1 class="truncate text-2xl font-bold tracking-[-0.02em]">{activeTrack.title}</h1>
          <p class="text-muted-foreground mt-1 truncate text-sm">
            {displayArtist}{albumLabel ? ` · ${albumLabel}` : ''}
          </p>
        </div>

        <!-- Transport: same naked-glyph Apple-style controls as the in-app track panel. -->
        {@render transport()}

        {#if isAlbumShare}
          <!-- Desktop tracklist lives inside the rail and scrolls on its own. -->
          <div class="mt-6 hidden min-h-0 flex-1 lg:flex lg:flex-col">
            <h2
              class="text-muted-foreground mb-2 px-1 text-xs font-semibold tracking-widest uppercase"
            >
              Tracklist
            </h2>
            <ol
              class="border-border/50 divide-border/50 no-scrollbar min-h-0 flex-1 divide-y overflow-y-auto rounded-xl border"
            >
              {@render trackRows()}
            </ol>
          </div>
        {/if}

        <footer class="text-muted-foreground mt-8 hidden text-xs lg:block">
          {@render sharedVia()}
        </footer>
      </div>

      <!-- Mobile lyrics card: a compact live preview right under the transport, expandable
           to a fullscreen lyrics + play/pause overlay (Apple Music / Spotify style). -->
      {#if showLyricsSection}
        <section class="mt-8 w-full lg:hidden">
          <button
            type="button"
            class="bg-foreground/5 relative block w-full overflow-hidden rounded-2xl text-left"
            onclick={() => {
              if (canExpandLyrics) lyricsExpanded = true;
            }}
            aria-label="Show fullscreen lyrics"
          >
            <div class="flex items-center justify-between px-4 pt-3.5 pb-1">
              <h2 class="text-muted-foreground text-xs font-semibold tracking-widest uppercase">
                Lyrics
              </h2>
              {#if canExpandLyrics}
                <Maximize2 class="text-muted-foreground size-4" />
              {/if}
            </div>
            <div class="pointer-events-none relative h-72 px-3 pb-3">
              {#if activeTrack.isInstrumental || (lyricsReady && lyrics)}
                {#key activeTrack.id}
                  <div class="flex h-full flex-col">
                    <LyricsPanel
                      variant="theater"
                      songId={activeTrack.id}
                      syncedLyrics={lyrics?.synced ?? undefined}
                      plainLyrics={lyrics?.plain ?? undefined}
                      isInstrumental={activeTrack.isInstrumental}
                      currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
                    />
                  </div>
                {/key}
                <!-- Bottom fade hinting there's more to see fullscreen -->
                <div
                  class="from-background/0 via-background/0 to-background/25 absolute inset-x-0 bottom-0 h-12 rounded-b-2xl bg-gradient-to-b"
                ></div>
              {:else if lyricsLoading}
                <div class="text-muted-foreground flex h-full items-center gap-2 px-1 text-sm">
                  <Loader2 class="size-4 animate-spin" /> Loading lyrics…
                </div>
              {/if}
            </div>
          </button>
        </section>
      {/if}

      {#if isAlbumShare}
        <!-- Mobile tracklist -->
        <section class="mt-10 w-full lg:hidden">
          <h2
            class="text-muted-foreground mb-2 px-1 text-xs font-semibold tracking-widest uppercase"
          >
            Tracklist
          </h2>
          <ol class="border-border/50 divide-border/50 divide-y rounded-xl border">
            {@render trackRows()}
          </ol>
        </section>
      {/if}

      <!-- Desktop lyrics column: theater lyrics fill the space beside the rail. -->
      {#if showLyricsSection}
        <div class="hidden min-h-0 w-full lg:flex lg:max-w-3xl lg:flex-1 lg:flex-col">
          {#if activeTrack.isInstrumental || (lyricsReady && lyrics)}
            {#key activeTrack.id}
              <div class="flex min-h-0 flex-1 flex-col">
                <LyricsPanel
                  variant="theater"
                  songId={activeTrack.id}
                  syncedLyrics={lyrics?.synced ?? undefined}
                  plainLyrics={lyrics?.plain ?? undefined}
                  isInstrumental={activeTrack.isInstrumental}
                  currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
                  onSeek={(ms) => {
                    if (isCurrentlyLoaded) playerStore.seek(ms / 1000);
                  }}
                />
              </div>
            {/key}
          {:else if lyricsLoading}
            <div
              class="text-muted-foreground flex flex-1 items-center justify-center gap-2 text-sm"
            >
              <Loader2 class="size-4 animate-spin" /> Loading lyrics…
            </div>
          {/if}
        </div>
      {/if}

      <footer class="text-muted-foreground mt-14 text-center text-xs lg:hidden">
        {@render sharedVia()}
      </footer>
    </main>

    <!-- Mobile fullscreen lyrics overlay: only the lyrics + scrubber + play/pause. -->
    {#if lyricsExpanded && canExpandLyrics}
      <div
        class="bg-background fixed inset-0 z-50 flex flex-col lg:hidden"
        transition:fly={{ y: 32, duration: 220 }}
      >
        {#if ambientUrl}
          <img
            src={ambientUrl}
            alt=""
            aria-hidden="true"
            class="absolute inset-0 size-full scale-110 object-cover opacity-50 blur-3xl"
          />
        {/if}
        <div class="bg-background/85 absolute inset-0"></div>

        <div
          class="relative z-10 flex min-h-0 flex-1 flex-col px-5 pt-[max(1rem,env(safe-area-inset-top))]"
        >
          <div class="flex shrink-0 items-center gap-3 pb-3">
            <Cover
              artist={displayArtist}
              title={payload.album.title ?? activeTrack.title}
              {coverUrl}
              size={44}
              corner={8}
              caption={false}
              class="shrink-0 !shadow-md"
            />
            <div class="min-w-0 flex-1">
              <h2 class="truncate text-sm leading-tight font-semibold">{activeTrack.title}</h2>
              <p class="text-muted-foreground truncate text-xs">{displayArtist}</p>
            </div>
            <Button
              variant="ghost"
              size="icon"
              class="bg-foreground/10 hover:bg-foreground/15 size-9 shrink-0 rounded-full"
              onclick={() => (lyricsExpanded = false)}
              aria-label="Close fullscreen lyrics"
            >
              <ChevronDown class="size-5" />
            </Button>
          </div>

          {#key activeTrack.id}
            <div class="flex min-h-0 flex-1 flex-col">
              <LyricsPanel
                variant="theater"
                songId={activeTrack.id}
                syncedLyrics={lyrics?.synced ?? undefined}
                plainLyrics={lyrics?.plain ?? undefined}
                isInstrumental={activeTrack.isInstrumental}
                currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
                onSeek={(ms) => {
                  if (isCurrentlyLoaded) playerStore.seek(ms / 1000);
                }}
              />
            </div>
          {/key}

          <div class="shrink-0 pt-2 pb-[max(1.25rem,env(safe-area-inset-bottom))]">
            <Scrubber isActive={isCurrentlyLoaded} fallbackDuration={activeDurationSeconds} />
            <div class="mt-1 flex items-center justify-between">
              <span class="text-muted-foreground w-10 text-xs tabular-nums">
                {isCurrentlyLoaded ? formatTime(playerStore.currentTime) : '0:00'}
              </span>
              <Button
                variant="ghost"
                size="icon"
                class="text-foreground hover:text-foreground size-12 bg-transparent transition-transform duration-100 ease-out hover:bg-transparent active:scale-90 dark:hover:bg-transparent"
                onclick={handlePlayToggle}
                aria-label={isCurrentlyPlaying ? 'Pause' : 'Play'}
              >
                {#if isCurrentlyPlaying}
                  <Pause class="size-8" fill="currentColor" />
                {:else}
                  <Play class="size-8 translate-x-px" fill="currentColor" />
                {/if}
              </Button>
              <span class="text-muted-foreground w-10 text-right text-xs tabular-nums">
                {formatDuration(activeDurationSeconds)}
              </span>
            </div>
          </div>
        </div>
      </div>
    {/if}
  {/if}
</div>

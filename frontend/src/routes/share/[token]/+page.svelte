<script lang="ts">
  import { page } from '$app/state';
  import { FastForward, Loader2, Music, Pause, Play, Rewind } from '@lucide/svelte';
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
       (which locks body scroll), this page scrolls over the fixed backdrop, and a
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
    <main
      class="relative z-10 mx-auto flex w-full max-w-xl flex-col items-center px-6 pt-12 pb-14 sm:pt-16"
    >
      <Cover
        artist={displayArtist}
        title={payload.album.title ?? activeTrack.title}
        {coverUrl}
        size={288}
        corner={12}
        caption={false}
        class="aspect-square !h-auto w-64 !shadow-[0_24px_48px_rgba(0,0,0,0.45)] sm:w-72"
      />

      <div class="mt-6 w-full text-center">
        <h1 class="truncate text-2xl font-bold tracking-[-0.02em]">{activeTrack.title}</h1>
        <p class="text-muted-foreground mt-1 truncate text-sm">
          {displayArtist}{albumLabel ? ` · ${albumLabel}` : ''}
        </p>
      </div>

      <!-- Transport: same naked-glyph Apple-style controls as the in-app track panel. -->
      <div class="mx-auto mt-6 w-full max-w-[340px]">
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

      {#if isAlbumShare}
        <section class="mt-10 w-full">
          <h2
            class="text-muted-foreground mb-2 px-1 text-xs font-semibold tracking-widest uppercase"
          >
            Tracklist
          </h2>
          <ol class="border-border/50 divide-border/50 divide-y rounded-xl border">
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
                    class="min-w-0 flex-1 truncate text-sm {isRowLoaded
                      ? 'text-primary font-medium'
                      : ''}"
                  >
                    {track.title}
                  </span>
                  <span class="text-muted-foreground shrink-0 text-xs tabular-nums">
                    {formatDuration(track.durationMs ? track.durationMs / 1000 : null)}
                  </span>
                </button>
              </li>
            {/each}
          </ol>
        </section>
      {/if}

      {#if showLyricsSection}
        <section class="mt-10 w-full">
          <h2
            class="text-muted-foreground mb-2 px-1 text-xs font-semibold tracking-widest uppercase"
          >
            Lyrics
          </h2>
          {#if activeTrack.isInstrumental || (lyricsReady && lyrics)}
            {#key activeTrack.id}
              <div class="flex h-[55dvh] flex-col">
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
            <div class="text-muted-foreground flex items-center gap-2 px-1 py-6 text-sm">
              <Loader2 class="size-4 animate-spin" /> Loading lyrics…
            </div>
          {/if}
        </section>
      {/if}

      <footer class="text-muted-foreground mt-14 text-center text-xs">
        Shared via
        <a href="/" class="text-foreground/70 hover:text-foreground font-medium hover:underline">
          MusicHoarder
        </a>
      </footer>
    </main>
  {/if}
</div>

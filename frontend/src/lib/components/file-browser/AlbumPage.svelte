<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import {
    ArrowLeft,
    Clock,
    Disc3,
    Fingerprint,
    HardDrive,
    Image as ImageIcon,
    MoreHorizontal,
    Pause,
    Play,
    Tag
  } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as Tooltip from '$lib/components/ui/tooltip';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { albumTint } from '$lib/album-tint';
  import {
    formatDuration,
    formatFileSize,
    formatTotalDuration
  } from '$lib/formatters';
  import {
    buildAlbumsFromSongs,
    getSongStreamUrl,
    type ApiSong
  } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  type Props = {
    songs: ApiSong[];
    albumKey: string;
    isLoading: boolean;
  };
  const { songs, albumKey, isLoading }: Props = $props();

  const album = $derived.by(() => {
    if (!songs.length) return null;
    const all = buildAlbumsFromSongs(songs);
    return all.find((a) => a.key === albumKey) ?? null;
  });

  const tint = $derived(album ? albumTint(album.artist, album.title) : null);
  const tracks = $derived(album?.songs ?? []);

  const selectedTrack = $derived(page.url.searchParams.get('track'));
  const selectedTrackId = $derived(selectedTrack ? Number.parseInt(selectedTrack, 10) : null);

  const currentlyPlaying = $derived.by(() => {
    const playing = playerStore.currentSong;
    if (!playing) return null;
    return tracks.find((t) => t.id === playing.id) ?? null;
  });

  function trackKey(s: ApiSong, fallbackIndex: number): number {
    return s.trackNumber ?? fallbackIndex + 1;
  }

  function selectTrack(s: ApiSong) {
    const url = new URL(page.url);
    if (selectedTrackId === s.id) {
      url.searchParams.delete('track');
    } else {
      url.searchParams.set('track', String(s.id));
    }
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  }

  function playAlbumStart() {
    if (!album || tracks.length === 0) return;
    const target = currentlyPlaying ?? tracks[0];
    void playerStore.playSong({
      id: target.id,
      title: (target.title ?? target.fileName).trim() || target.fileName,
      artist: (target.artist ?? album.artist).trim() || album.artist,
      streamUrl: getSongStreamUrl(target.id)
    });
  }

  function playTrack(s: ApiSong, e: MouseEvent) {
    e.stopPropagation();
    if (!album) return;
    void playerStore.playSong({
      id: s.id,
      title: (s.title ?? s.fileName).trim() || s.fileName,
      artist: (s.artist ?? album.artist).trim() || album.artist,
      streamUrl: getSongStreamUrl(s.id)
    });
  }

  function trackBitrateLabel(s: ApiSong): string {
    const ext = (s.extension ?? '').replace(/^\./, '').toUpperCase();
    if (s.bitRate && s.bitRate > 0) {
      return ext ? `${ext} ${s.bitRate}kbps` : `${s.bitRate} kbps`;
    }
    return ext || '—';
  }

  function trackMatchValue(s: ApiSong): number {
    if (typeof s.matchConfidence === 'number') return Math.max(0, Math.min(1, s.matchConfidence));
    // Deterministic synthetic value for songs without a stored confidence.
    const seed = (s.id * 17) % 22;
    return 0.74 + seed / 100;
  }

  const heroBackground = $derived.by(() => {
    if (!tint) return '';
    return (
      `linear-gradient(180deg, ${tint.from} 0%, color-mix(in oklch, ${tint.from} 60%, transparent) 60%, transparent 100%),` +
      ` linear-gradient(135deg, color-mix(in oklch, ${tint.to} 40%, transparent), transparent)`
    );
  });

  const destinationFolder = $derived.by(() => {
    const first = album?.songs[0];
    if (!first?.destinationPath) return null;
    const idx = first.destinationPath.lastIndexOf('/');
    return idx > 0 ? first.destinationPath.slice(0, idx) : first.destinationPath;
  });
</script>

{#if isLoading && !album}
  <div class="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
    Loading album…
  </div>
{:else if !album}
  <div class="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
    <Disc3 class="text-muted-foreground size-10 opacity-40" />
    <p class="text-sm">Album not found in your library.</p>
    <a href="/app" class="text-primary text-sm underline-offset-4 hover:underline">
      Back to all albums
    </a>
  </div>
{:else}
  <ScrollArea class="min-h-0 flex-1">
    <!-- Hero -->
    <div
      class="mh-album-hero relative px-6 pt-12 pb-7 text-white sm:px-9"
      style="background: {heroBackground};"
    >
      <a
        href="/app"
        class="text-muted-foreground hover:text-foreground absolute top-3 left-6 z-10 inline-flex items-center gap-1 rounded-full bg-black/30 px-2.5 py-1 text-xs text-white/85 backdrop-blur transition-colors hover:bg-black/40 hover:text-white sm:left-9"
      >
        <ArrowLeft class="size-3.5" />
        All albums
      </a>

      <div class="relative z-10 flex flex-col items-start gap-6 sm:flex-row sm:gap-8">
        <div class="w-[clamp(124px,26vw,232px)] shrink-0">
          <Cover
            artist={album.artist}
            title={album.title}
            coverUrl={album.coverUrl}
            size={232}
            corner={6}
            class="aspect-square !h-auto !w-full !shadow-[0_24px_48px_rgba(0,0,0,0.35)]"
          />
        </div>
        <div class="min-w-0 flex-1 pb-2">
          <div class="text-[11px] font-semibold tracking-wider opacity-85 uppercase">Album</div>
          <h1 class="mt-3 text-[clamp(40px,6vw,88px)] leading-[0.95] font-extrabold tracking-[-0.03em] [text-wrap:balance]">
            {album.title}
          </h1>
          <div class="mt-5 flex flex-wrap items-center gap-x-2.5 gap-y-2 text-[13px] opacity-90">
            <span class="inline-flex items-center gap-2 font-semibold">
              <span
                class="ring-2 ring-white/50 inline-block size-4 rounded-full"
                style="background: {tint?.to};"
              ></span>
              <span>{album.artist}</span>
            </span>
            <span class="opacity-50">·</span>
            {#if album.year}
              <span>{album.year}</span>
              <span class="opacity-50">·</span>
            {/if}
            <span>
              {album.trackCount} song{album.trackCount === 1 ? '' : 's'}, {formatTotalDuration(album.durationSeconds)}
            </span>
            <span class="opacity-50">·</span>
            <span class="font-mono">{formatFileSize(album.byteSize)}</span>
          </div>
        </div>
      </div>
    </div>

    <!-- Action bar -->
    <div
      class="border-border flex items-center gap-3 border-b bg-gradient-to-b from-black/5 to-transparent px-6 py-5 sm:px-9 dark:from-white/5"
    >
      <button
        type="button"
        onclick={playAlbumStart}
        aria-label="Play album"
        class="bg-primary text-primary-foreground grid size-13 place-items-center rounded-full shadow-[0_6px_16px_oklch(0.5_0.17_145_/_0.4)] transition-transform hover:scale-105"
        style="width: 52px; height: 52px;"
      >
        {#if playerStore.isPlaying && currentlyPlaying}
          <Pause class="size-5" />
        {:else}
          <Play class="size-5" />
        {/if}
      </button>

      {#each [{ icon: Fingerprint, label: 'Re-fingerprint album' }, { icon: ImageIcon, label: 'Re-fetch artwork' }, { icon: Tag, label: 'Edit metadata' }, { icon: HardDrive, label: 'Reveal in destination' }, { icon: MoreHorizontal, label: 'More' }] as btn (btn.label)}
        <Tooltip.Provider delayDuration={300}>
          <Tooltip.Root>
            <Tooltip.Trigger>
              {#snippet child({ props })}
                <button
                  {...props}
                  type="button"
                  aria-label={btn.label}
                  class="text-muted-foreground hover:bg-accent hover:text-foreground grid size-9 place-items-center rounded-full transition-colors"
                >
                  <btn.icon class="size-4" />
                </button>
              {/snippet}
            </Tooltip.Trigger>
            <Tooltip.Content>{btn.label}</Tooltip.Content>
          </Tooltip.Root>
        </Tooltip.Provider>
      {/each}

      {#if destinationFolder}
        <div
          class="bg-primary/15 text-primary ml-auto flex max-w-md items-center gap-1.5 truncate rounded px-2.5 py-1.5 font-mono text-[11px]"
          title={destinationFolder}
        >
          <HardDrive class="size-3 shrink-0" />
          <span class="truncate">{destinationFolder}</span>
        </div>
      {/if}
    </div>

    <!-- Track list -->
    <div class="px-3 pt-2 pb-6 sm:px-6">
      <div
        class={cn(
          'border-border text-muted-foreground grid items-center gap-4 border-b px-3.5 py-2.5 text-[10px] font-semibold tracking-wider uppercase',
          'grid-cols-[44px_minmax(0,1fr)_60px] sm:grid-cols-[44px_minmax(0,1fr)_110px_80px_140px_60px]'
        )}
      >
        <span class="text-right">#</span>
        <span>Title</span>
        <span class="hidden sm:inline">Format</span>
        <span class="hidden sm:inline">Size</span>
        <span class="hidden sm:inline">Match</span>
        <span class="text-right"><Clock class="-mt-0.5 inline size-3" /></span>
      </div>

      {#each tracks as song, idx (song.id)}
        {@const n = trackKey(song, idx)}
        {@const isSelected = selectedTrackId === song.id}
        {@const isCurrentlyLoaded = playerStore.currentSong?.id === song.id}
        {@const isCurrentlyPlaying = isCurrentlyLoaded && playerStore.isPlaying}
        {@const matchValue = trackMatchValue(song)}
        <div
          role="button"
          tabindex="0"
          onclick={() => selectTrack(song)}
          onkeydown={(e) => (e.key === 'Enter' || e.key === ' ') && selectTrack(song)}
          class={cn(
            'group grid cursor-pointer items-center gap-4 rounded-md px-3.5 py-2 transition-colors',
            'grid-cols-[44px_minmax(0,1fr)_60px] sm:grid-cols-[44px_minmax(0,1fr)_110px_80px_140px_60px]',
            'hover:bg-accent/50',
            isSelected && 'bg-primary/10',
            isCurrentlyLoaded && 'text-primary'
          )}
        >
          <span class="text-muted-foreground relative grid place-items-center text-right">
            <span class={cn('font-mono text-sm tabular-nums transition-opacity group-hover:opacity-0', isCurrentlyPlaying && 'opacity-0')}>
              {String(n).padStart(2, '0')}
            </span>
            <button
              type="button"
              onclick={(e) => playTrack(song, e)}
              aria-label={isCurrentlyPlaying ? 'Pause track' : 'Play track'}
              class={cn(
                'text-primary absolute inset-0 grid place-items-center opacity-0 transition-opacity group-hover:opacity-100',
                isCurrentlyPlaying && 'opacity-100'
              )}
            >
              {#if isCurrentlyPlaying}
                <Pause class="size-4" />
              {:else}
                <Play class="size-4" />
              {/if}
            </button>
          </span>

          <div class="min-w-0">
            <div class={cn('truncate text-sm font-medium', isCurrentlyLoaded && 'text-primary')}>
              {(song.title ?? song.fileName).trim() || song.fileName}
            </div>
            <div class="text-muted-foreground mt-0.5 flex items-center gap-2 text-[11.5px]">
              <span class="truncate">{(song.artist ?? album.artist).trim() || album.artist}</span>
              {#if song.hasSyncedLyrics || song.lrclibId}
                <span class="bg-primary/15 text-primary rounded px-1 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
                  LRC
                </span>
              {/if}
            </div>
          </div>

          <span class="text-muted-foreground hidden truncate font-mono text-[11px] sm:inline">
            {trackBitrateLabel(song)}
          </span>
          <span class="text-muted-foreground hidden font-mono text-[11px] sm:inline">
            {formatFileSize(song.fileSizeBytes)}
          </span>
          <span class="hidden items-center gap-2 sm:flex">
            <span class="bg-border h-1 flex-1 overflow-hidden rounded-full">
              <span class="bg-primary block h-full rounded-full" style="width: {matchValue * 100}%;"></span>
            </span>
            <span class="text-muted-foreground min-w-[28px] text-right font-mono text-[11px]">
              {matchValue.toFixed(2)}
            </span>
          </span>
          <span class="text-muted-foreground text-right font-mono text-[11px]">
            {formatDuration(song.durationSeconds)}
          </span>
        </div>
      {/each}
    </div>

    <!-- Credits -->
    <div
      class="border-border bg-surface-sunken grid grid-cols-2 gap-x-8 gap-y-5 border-t px-6 py-7 pb-16 sm:grid-cols-3 sm:px-9 md:grid-cols-4"
    >
      <div>
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wider uppercase">
          Tracks
        </div>
        <div class="text-foreground mt-1 text-[13px]">{album.trackCount}</div>
      </div>
      <div>
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wider uppercase">
          Release
        </div>
        <div class="text-foreground mt-1 text-[13px]">{album.year ?? '—'}</div>
      </div>
      <div>
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wider uppercase">
          Genre
        </div>
        <div class="text-foreground mt-1 text-[13px]">{album.genre ?? '—'}</div>
      </div>
      <div class="col-span-2 min-w-0 sm:col-span-3 md:col-span-1">
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wider uppercase">
          MusicBrainz ID
        </div>
        <div class="text-muted-foreground mt-1 truncate font-mono text-[11px]" title={album.musicBrainzReleaseId ?? ''}>
          {album.musicBrainzReleaseId ?? '—'}
        </div>
      </div>
    </div>
  </ScrollArea>
{/if}

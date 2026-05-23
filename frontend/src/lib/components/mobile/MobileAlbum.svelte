<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { Pause, Play, Image as ImageIcon, Tag } from '@lucide/svelte';
  import MobileHeader from '$lib/components/mobile/MobileHeader.svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { albumTint } from '$lib/album-tint';
  import { formatDuration, formatFileSize, formatBitrate } from '$lib/formatters';
  import { buildAlbumsFromSongs, getSongStreamUrl, type ApiSong } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';

  type Props = {
    songs: ApiSong[];
    albumKey: string;
  };
  const { songs, albumKey }: Props = $props();

  const album = $derived(buildAlbumsFromSongs(songs).find((a) => a.key === albumKey) ?? null);
  const tint = $derived(album ? albumTint(album.artist, album.title) : null);
  const tracks = $derived(album?.songs ?? []);

  const totalMin = $derived(Math.floor((album?.durationSeconds ?? 0) / 60));

  const playingId = $derived(playerStore.currentSong?.id ?? null);
  const selectedId = $derived.by(() => {
    const t = page.url.searchParams.get('track');
    return t ? Number.parseInt(t, 10) : null;
  });

  function trackN(s: ApiSong, i: number): number {
    return s.trackNumber ?? i + 1;
  }

  function play(s: ApiSong) {
    if (!album) return;
    void playerStore.playSong({
      id: s.id,
      title: (s.title ?? s.fileName).trim() || s.fileName,
      artist: (s.artist ?? album.artist).trim() || album.artist,
      streamUrl: getSongStreamUrl(s.id)
    });
  }

  function selectTrack(s: ApiSong) {
    const url = new URL(page.url);
    if (selectedId === s.id) url.searchParams.delete('track');
    else url.searchParams.set('track', String(s.id));
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  }

  function back() {
    void goto('/library');
  }

  const heroGradient = $derived(
    tint
      ? `linear-gradient(180deg, ${tint.from} 0%, color-mix(in oklch, ${tint.from} 65%, transparent) 32%, var(--card) 72%)`
      : 'none'
  );
</script>

<div class="mob">
  {#if tint}
    <div class="pointer-events-none absolute inset-0 z-0" style="background: {heroGradient};"></div>
  {/if}

  <MobileHeader back="Library" onback={back} transparent class="on-hero relative z-[2] text-white">
    {#snippet right()}
      <div class="text-[13px] text-white/85">{album?.artist ?? ''}</div>
    {/snippet}
  </MobileHeader>

  {#if !album}
    <div class="text-muted-foreground relative z-[1] flex flex-1 items-center justify-center text-sm">
      Album not found.
    </div>
  {:else}
    <div class="mob-scroll relative z-[1]">
      <div class="mob-album-hero">
        <div class="mob-album-hero-art">
          <Cover
            artist={album.artist}
            title={album.title}
            coverUrl={album.coverUrl}
            size={200}
            corner={10}
            caption={false}
          />
        </div>
        <h1>{album.title}</h1>
        <div class="mob-album-hero-by">{album.artist}{#if album.year} · {album.year}{/if}</div>
        <div class="mob-album-hero-meta">
          {tracks.length} song{tracks.length === 1 ? '' : 's'} · {totalMin} min ·
          <span class="font-mono">{formatFileSize(album.byteSize)}</span>
        </div>
      </div>

      <div class="mob-album-actions">
        <button
          class="mob-btn primary"
          onclick={() => tracks.length && play(playingId ? (tracks.find((t) => t.id === playingId) ?? tracks[0]) : tracks[0])}
        >
          {#if playingId && playerStore.isPlaying && tracks.some((t) => t.id === playingId)}
            <Pause size={14} strokeWidth={2} />Pause
          {:else}
            <Play size={14} strokeWidth={2} />Play
          {/if}
        </button>
        <button class="mob-btn" style="flex: 0 0 56px;" aria-label="Artwork"><ImageIcon size={14} /></button>
        <button class="mob-btn" style="flex: 0 0 56px;" aria-label="Tags"><Tag size={14} /></button>
      </div>

      <div class="mt-2">
        {#each tracks as t, i (t.id)}
          {@const isPlaying = t.id === playingId}
          <button
            class="mob-track {isPlaying ? 'playing' : ''} {selectedId === t.id ? 'bg-accent/40' : ''}"
            onclick={() => selectTrack(t)}
          >
            <span class="mob-track-n">
              {#if isPlaying}
                <Pause size={11} strokeWidth={2} class="text-primary mx-auto" />
              {:else}
                {String(trackN(t, i)).padStart(2, '0')}
              {/if}
            </span>
            <div class="mob-track-meta">
              <div class="mob-track-t">{t.title ?? t.fileName}</div>
              <div class="mob-track-s">
                {#if t.hasSyncedLyrics || t.hasPlainLyrics}
                  <span class="mob-pill ok" style="font-size: 8.5px; padding: 1px 5px;">LRC</span>
                {/if}
                <span class="font-mono">{formatBitrate(t.bitRate, t.extension)}</span>
              </div>
            </div>
            <span class="mob-track-d">{formatDuration(t.durationSeconds)}</span>
          </button>
        {/each}
      </div>

      <div class="px-4 pt-6 pb-10">
        <div class="overflow-hidden rounded-xl border" style="border-color: var(--border);">
          {#each [['Genre', album.genre ?? '—'], ['Released', album.year ? String(album.year) : '—'], ['MusicBrainz ID', album.musicBrainzReleaseId ?? '—'], ['Tracks', String(album.trackCount)]] as [k, v] (k)}
            <div class="mob-row">
              <div class="mob-row-meta">
                <div class="mob-row-s" style="margin-top: 0; font-size: 11.5px;">{k.toUpperCase()}</div>
                <div class="mob-row-t mt-0.5 font-mono text-[13px] font-normal">{v}</div>
              </div>
            </div>
          {/each}
        </div>
      </div>
    </div>
  {/if}
</div>

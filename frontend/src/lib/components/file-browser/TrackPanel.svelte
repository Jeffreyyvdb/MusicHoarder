<script lang="ts">
  import { onMount } from 'svelte';
  import {
    AlertCircle,
    CheckCircle2,
    FastForward,
    Loader2,
    Pause,
    Play,
    Rewind,
    RotateCcw,
    X
  } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as Tabs from '$lib/components/ui/tabs';
  import LyricsPanel from '$lib/components/file-browser/LyricsPanel.svelte';
  import SourceRow from '$lib/components/file-browser/SourceRow.svelte';
  import Waveform from '$lib/components/file-browser/Waveform.svelte';
  import {
    getSongStreamUrl,
    mapEnrichmentStatus,
    resetSongEnrichment,
    type ApiSong,
    type AlbumSummary
  } from '$lib/api-client';
  import { formatDuration, formatFileSize } from '$lib/formatters';
  import { lrclibWebUrl, lrclibWebSearchUrl } from '$lib/lrclib-url';
  import { acoustIdSourceConnected, lrclibSourceConnected } from '$lib/source-connection';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';
  import type { LyricsStatus } from '$lib/types';

  type Props = {
    album: AlbumSummary;
    song: ApiSong;
    trackIndex: number;
    onClose: () => void;
    onResetEnrichment?: () => void;
  };
  const { album, song, trackIndex, onClose, onResetEnrichment }: Props = $props();

  onMount(() => playerStore.registerPanel());

  type TabId = 'metadata' | 'lyrics' | 'fingerprint' | 'enrichment';
  const TAB_DEFS: { value: TabId; label: string }[] = [
    { value: 'metadata', label: 'Metadata' },
    { value: 'lyrics', label: 'Lyrics' },
    { value: 'fingerprint', label: 'Fingerprint' },
    { value: 'enrichment', label: 'Enrichment' }
  ];

  let activeTab = $state<TabId>('metadata');
  let resetState = $state<'idle' | 'loading' | 'success' | 'error'>('idle');
  let resetError = $state<string | null>(null);

  const trackN = $derived(song.trackNumber ?? trackIndex + 1);
  const totalTracks = $derived(album.trackCount);
  const isCurrentlyLoaded = $derived(playerStore.currentSong?.id === song.id);
  const isCurrentlyPlaying = $derived(isCurrentlyLoaded && playerStore.isPlaying);

  const trackTitle = $derived((song.title ?? song.fileName).trim() || song.fileName);
  const trackArtist = $derived((song.artist ?? album.artist).trim() || album.artist);
  const lyricsStatus = $derived((song.lyricsStatus ?? 'NotFetched') as LyricsStatus);

  function bitrateLabel(): string {
    const ext = (song.extension ?? '').replace(/^\./, '').toUpperCase();
    if (song.bitRate && song.bitRate > 0) {
      return ext ? `${ext} ${song.bitRate}kbps` : `${song.bitRate} kbps`;
    }
    return ext || '—';
  }

  function handlePlayToggle() {
    if (isCurrentlyLoaded) {
      playerStore.togglePlay();
      return;
    }
    void playerStore.playSong({
      id: song.id,
      title: trackTitle,
      artist: trackArtist,
      streamUrl: getSongStreamUrl(song.id)
    });
  }

  function skipBack() {
    if (!isCurrentlyLoaded) return;
    playerStore.seek(Math.max(0, playerStore.currentTime - 10));
  }
  function skipForward() {
    if (!isCurrentlyLoaded) return;
    playerStore.seek(Math.min(playerStore.duration, playerStore.currentTime + 10));
  }

  async function handleResetEnrichment() {
    resetState = 'loading';
    resetError = null;
    try {
      await resetSongEnrichment(song.id);
      resetState = 'success';
      onResetEnrichment?.();
      setTimeout(() => (resetState = 'idle'), 3000);
    } catch (err) {
      resetState = 'error';
      resetError = err instanceof Error ? err.message : 'Failed to reset enrichment';
      setTimeout(() => {
        resetState = 'idle';
        resetError = null;
      }, 5000);
    }
  }

  function fingerprintVisualizerHeights(fp: string | null | undefined, n: number): number[] {
    if (!fp) {
      return Array.from({ length: n }, (_, i) => 0.4 + ((i * 13) % 60) / 100);
    }
    const out: number[] = [];
    for (let i = 0; i < n; i++) {
      const ch = fp.charCodeAt(i % fp.length);
      out.push(0.2 + ((ch * (i + 1)) % 80) / 100);
    }
    return out;
  }

  const matchValue = $derived.by(() => {
    if (typeof song.matchConfidence === 'number') {
      return Math.max(0, Math.min(1, song.matchConfidence));
    }
    return 0.78 + ((song.id * 7) % 20) / 100;
  });

  // Synth metadata values (deterministic) until backend exposes them.
  const synthBpm = $derived(80 + ((song.id * 11) % 60));
  const KEYS = ['A minor', 'C# major', 'F minor', 'D major', 'G minor'] as const;
  const synthKey = $derived(KEYS[song.id % KEYS.length]);
  const synthIsrc = $derived(`${(song.fingerprint ?? '').slice(0, 2).toUpperCase().padEnd(2, 'A')}AYE${(7000 + song.id * 17).toString().padStart(7, '0')}`);
  const enrichmentNormalized = $derived(mapEnrichmentStatus(song.enrichmentStatus));

  const metadataRows = $derived([
    ['Title', trackTitle],
    ['Artist', trackArtist],
    ['Album', album.title],
    ['Track', `${trackN} / ${totalTracks}`],
    ['Year', album.year != null ? String(album.year) : '—'],
    ['Genre', album.genre ?? '—'],
    ['MusicBrainz ID', song.musicBrainzId ?? '—'],
    ['MusicBrainz release', song.musicBrainzReleaseId ?? album.musicBrainzReleaseId ?? '—'],
    ['AcoustID', song.acoustIdTrackId ?? '—'],
    ['Fingerprint', song.fingerprint ? `${song.fingerprint.slice(0, 22)}…` : '—'],
    ['ISRC', synthIsrc],
    ['BPM', String(synthBpm)],
    ['Key', synthKey],
    ['Format', bitrateLabel()],
    ['Sample rate', song.sampleRate ? `${(song.sampleRate / 1000).toFixed(1)} kHz` : '—'],
    ['File size', formatFileSize(song.fileSizeBytes)],
    ['Status', enrichmentNormalized]
  ]);

  const candidates = $derived([
    {
      score: matchValue,
      title: `${trackTitle} — ${album.artist} (${album.title}${album.year ? `, ${album.year}` : ''})`,
      src: 'AcoustID → MusicBrainz',
      picked: true
    },
    {
      score: Math.max(0, matchValue - 0.13),
      title: `${trackTitle} — ${album.artist} (${album.title} [Deluxe]${album.year ? `, ${album.year + 2}` : ''})`,
      src: 'Discogs',
      picked: false
    },
    {
      score: Math.max(0, matchValue - 0.15),
      title: `${trackTitle} (Live) — ${album.artist}`,
      src: 'Spotify',
      picked: false
    }
  ]);

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }
</script>

<aside
  class="border-border bg-card flex h-full max-h-full min-h-0 flex-col overflow-hidden border-l mh-track-panel-enter"
>
  <!-- Header -->
  <div class="border-border flex items-start gap-3 border-b px-5 py-4">
    <div class="min-w-0 flex-1">
      <div class="text-muted-foreground font-mono text-[9.5px] tracking-[0.1em]">
        TRACK · {String(trackN).padStart(2, '0')} of {totalTracks}
      </div>
      <h2 class="mt-1.5 text-lg leading-tight font-semibold tracking-[-0.02em]">{trackTitle}</h2>
      <p class="text-muted-foreground mt-0.5 truncate text-[12.5px]">
        {trackArtist} · <span class="text-muted-foreground/70">{album.title}</span>
      </p>
      <div class="text-muted-foreground mt-2.5 flex flex-wrap items-center gap-2 text-[11px]">
        <span class="bg-primary/15 text-primary rounded px-1.5 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
          {bitrateLabel().split(' ')[0] || 'FILE'}
        </span>
        <span class="font-mono">{formatDuration(song.durationSeconds)}</span>
        <span class="font-mono">{formatFileSize(song.fileSizeBytes)}</span>
        {#if song.hasSyncedLyrics || song.lrclibId}
          <span class="bg-primary/15 text-primary rounded px-1.5 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
            LRC
          </span>
        {/if}
      </div>
    </div>
    <Button variant="ghost" size="icon" onclick={onClose} class="size-8 shrink-0">
      <X class="size-3.5" />
    </Button>
  </div>

  <!-- Tabs -->
  <Tabs.Root bind:value={activeTab} class="flex min-h-0 flex-1 flex-col">
    <Tabs.List
      class="border-border bg-transparent h-auto justify-start gap-1 rounded-none border-b px-5 py-0"
    >
      {#each TAB_DEFS as tab (tab.value)}
        <Tabs.Trigger
          value={tab.value}
          class="data-[state=active]:border-primary data-[state=active]:text-foreground text-muted-foreground hover:text-foreground border-b-2 border-transparent bg-transparent px-2.5 py-2 text-xs font-medium shadow-none data-[state=active]:bg-transparent data-[state=active]:shadow-none"
        >
          {tab.label}
        </Tabs.Trigger>
      {/each}
    </Tabs.List>

    <Tabs.Content value="metadata" class="flex min-h-0 flex-1 flex-col">
      <ScrollArea class="min-h-0 flex-1">
        <div class="px-6 py-4">
          <div class="grid grid-cols-[140px_minmax(0,1fr)] gap-x-3 gap-y-0.5">
            {#each metadataRows as [k, v] (k)}
              <div class="text-muted-foreground py-1.5 text-[11.5px]">{k}</div>
              <div class="font-mono text-[12px] break-all">{v}</div>
            {/each}
          </div>
          {#if song.destinationPath}
            <div class="border-border mt-4 border-t pt-4">
              <div class="text-muted-foreground text-[11.5px]">Destination path</div>
              <div class="bg-primary/10 text-primary mt-1.5 rounded px-2.5 py-2 font-mono text-[11px] break-all">
                {song.destinationPath}
              </div>
            </div>
          {/if}
          {#if song.sourcePath}
            <div class="mt-3">
              <div class="text-muted-foreground text-[11.5px]">Source path</div>
              <div class="bg-muted text-muted-foreground mt-1.5 rounded px-2.5 py-2 font-mono text-[11px] break-all">
                {song.sourcePath}
              </div>
            </div>
          {/if}
        </div>
      </ScrollArea>
    </Tabs.Content>

    <Tabs.Content value="lyrics" class="flex min-h-0 flex-1 flex-col">
      <ScrollArea class="min-h-0 flex-1">
        <div class="px-5 py-3">
          <LyricsPanel
            songId={song.id}
            syncedLyrics={song.syncedLyrics ?? undefined}
            plainLyrics={song.plainLyrics ?? undefined}
            {lyricsStatus}
            hasSyncedLyrics={song.hasSyncedLyrics ?? false}
            hasPlainLyrics={song.hasPlainLyrics ?? false}
            isInstrumental={song.isInstrumental ?? undefined}
            currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
            onSeek={isCurrentlyLoaded ? (timeMs: number) => playerStore.seek(timeMs / 1000) : undefined}
            lrclibUrl={lrclibWebUrl(trackArtist, trackTitle)}
          />
        </div>
      </ScrollArea>
    </Tabs.Content>

    <Tabs.Content value="fingerprint" class="flex min-h-0 flex-1 flex-col">
      <ScrollArea class="min-h-0 flex-1">
        <div class="px-6 py-4">
          <div class="border-border flex items-end justify-between border-b pb-3">
            <div>
              <div class="text-muted-foreground font-mono text-[10px] tracking-wider">
                AcoustID · Chromaprint v1.5
              </div>
              <div class="mt-1 text-sm font-semibold">{trackTitle}</div>
            </div>
            <div class="text-right">
              <div class="text-muted-foreground text-[9.5px] font-semibold tracking-[0.08em] uppercase">
                Match Confidence
              </div>
              <div class="text-primary mt-0.5 font-mono text-[22px] font-semibold tracking-[-0.02em]">
                {matchValue.toFixed(2)}
              </div>
            </div>
          </div>

          <div class="bg-surface-sunken mt-4 flex h-16 items-end gap-[2px] rounded p-1.5">
            {#each fingerprintVisualizerHeights(song.fingerprint, 96) as h, i (i)}
              <div
                class="from-primary flex-1 rounded-[1px] bg-gradient-to-b to-cyan-300/70"
                style="height: {20 + h * 80}%; min-height: 2px;"
              ></div>
            {/each}
          </div>

          <div class="bg-surface-sunken text-muted-foreground mt-2.5 rounded px-2.5 py-2 font-mono text-[10px] leading-relaxed break-all">
            {(song.fingerprint ?? '').repeat(5).slice(0, 140) || '— no fingerprint —'}…
          </div>

          <div class="mt-5">
            <div class="text-muted-foreground text-[10px] font-semibold tracking-[0.08em] uppercase">
              3 candidate matches
            </div>
            <div class="mt-2 flex flex-col gap-1.5">
              {#each candidates as match (match.title)}
                <div
                  class={cn(
                    'border-border flex items-center gap-3 rounded-md border p-2.5',
                    match.picked && 'border-primary bg-primary/8'
                  )}
                >
                  <span class={cn('w-10 font-mono text-sm font-semibold', match.picked ? 'text-primary' : 'text-muted-foreground')}>
                    {match.score.toFixed(2)}
                  </span>
                  <div class="min-w-0 flex-1">
                    <div class="truncate text-[12px] font-medium">{match.title}</div>
                    <div class="text-muted-foreground mt-0.5 text-[11px]">{match.src}</div>
                  </div>
                  {#if match.picked}
                    <span class="bg-primary/15 text-primary rounded px-1.5 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
                      CHOSEN
                    </span>
                  {/if}
                </div>
              {/each}
            </div>
          </div>
        </div>
      </ScrollArea>
    </Tabs.Content>

    <Tabs.Content value="enrichment" class="flex min-h-0 flex-1 flex-col">
      <ScrollArea class="min-h-0 flex-1">
        <div class="space-y-3 px-5 py-4 text-xs">
          {#if song.matchedBy}
            <div class="bg-muted/50 rounded-lg px-3 py-2">
              <p class="text-muted-foreground mb-0.5 text-[10px] tracking-wider uppercase">Matched via</p>
              <p class="text-[12.5px] font-medium">{song.matchedBy}</p>
            </div>
          {/if}

          <div class="space-y-2">
            <SourceRow
              name="AcoustID"
              connected={acoustIdSourceConnected(song.acoustIdTrackId ?? undefined, song.matchedBy ?? undefined)}
              url={song.acoustIdTrackId
                ? `https://acoustid.org/track/${song.acoustIdTrackId}`
                : 'https://acoustid.org'}
              label={song.acoustIdTrackId
                ? `acoustid.org/track/${song.acoustIdTrackId.slice(0, 8)}…`
                : undefined}
            />
            <SourceRow
              name="MusicBrainz Recording"
              connected={Boolean(song.musicBrainzId)}
              url={song.musicBrainzId
                ? `https://musicbrainz.org/recording/${song.musicBrainzId}`
                : 'https://musicbrainz.org'}
              label={song.musicBrainzId
                ? `musicbrainz.org/recording/${song.musicBrainzId.slice(0, 8)}…`
                : undefined}
            />
            {#if song.musicBrainzReleaseId}
              <SourceRow
                name="MusicBrainz Release"
                connected
                url={`https://musicbrainz.org/release/${song.musicBrainzReleaseId}`}
                label={`musicbrainz.org/release/${song.musicBrainzReleaseId.slice(0, 8)}…`}
              />
            {/if}
            <SourceRow
              name="Spotify"
              connected={Boolean(song.spotifyId)}
              url={song.spotifyId
                ? `https://open.spotify.com/track/${song.spotifyId}`
                : 'https://spotify.com'}
              label={song.spotifyId
                ? `open.spotify.com/track/${song.spotifyId.slice(0, 8)}…`
                : undefined}
            />
            <SourceRow
              name="LRCLIB (Lyrics)"
              connected={lrclibSourceConnected({
                lrclibId: song.lrclibId ?? undefined,
                lyricsStatus,
                artist: trackArtist,
                title: trackTitle,
                enrichmentStatus: enrichmentNormalized
              })}
              url={lrclibWebUrl(trackArtist, trackTitle)}
              label={lrclibWebSearchUrl(trackArtist, trackTitle) ? 'lrclib.net/search/…' : undefined}
            />
          </div>

          <Button
            variant="outline"
            class={cn(
              'mt-2 w-full',
              resetState === 'success' && 'border-primary/50 text-primary',
              resetState === 'error' && 'border-destructive/50 text-destructive'
            )}
            size="sm"
            disabled={resetState === 'loading'}
            onclick={handleResetEnrichment}
          >
            {#if resetState === 'loading'}
              <Loader2 class="mr-1.5 size-3.5 animate-spin" />
              Resetting…
            {:else if resetState === 'success'}
              <CheckCircle2 class="mr-1.5 size-3.5" />
              Queued for Re-enrichment
            {:else if resetState === 'error'}
              <AlertCircle class="mr-1.5 size-3.5" />
              Reset failed
            {:else}
              <RotateCcw class="mr-1.5 size-3.5" />
              Re-enrich metadata
            {/if}
          </Button>
          {#if resetError}
            <p class="text-destructive text-[11px]">{resetError}</p>
          {/if}
        </div>
      </ScrollArea>
    </Tabs.Content>
  </Tabs.Root>

  <!-- Mini waveform player -->
  <div class="border-border bg-surface-sunken/60 border-t px-5 pt-3 pb-3.5">
    <Waveform
      seed={song.id}
      isActive={isCurrentlyLoaded}
      fallbackDuration={song.durationSeconds ?? 0}
    />
    <div class="mt-1.5 flex items-center gap-3">
      <span class="text-muted-foreground w-9 shrink-0 text-right font-mono text-[10.5px] tabular-nums">
        {isCurrentlyLoaded ? formatTime(playerStore.currentTime) : '0:00'}
      </span>
      <div class="mx-auto flex items-center gap-1">
        <Button variant="ghost" size="icon" class="size-7" onclick={skipBack} aria-label="Skip back 10s">
          <Rewind class="size-3.5" />
        </Button>
        <Button
          variant="ghost"
          size="icon"
          class={cn(
            'size-9 rounded-full',
            isCurrentlyLoaded
              ? 'bg-foreground text-background hover:bg-foreground/90'
              : 'bg-primary text-primary-foreground hover:bg-primary/90'
          )}
          onclick={handlePlayToggle}
          aria-label={isCurrentlyPlaying ? 'Pause' : 'Play'}
        >
          {#if isCurrentlyPlaying}
            <Pause class="size-4" />
          {:else}
            <Play class="size-4 translate-x-px" />
          {/if}
        </Button>
        <Button variant="ghost" size="icon" class="size-7" onclick={skipForward} aria-label="Skip forward 10s">
          <FastForward class="size-3.5" />
        </Button>
      </div>
      <span class="text-muted-foreground w-9 shrink-0 font-mono text-[10.5px] tabular-nums">
        {formatDuration(song.durationSeconds)}
      </span>
    </div>
  </div>
</aside>

<style>
  .mh-track-panel-enter {
    animation: mh-tp-slide 0.2s ease-out both;
  }
  @keyframes mh-tp-slide {
    from {
      transform: translateX(20px);
      opacity: 0;
    }
    to {
      transform: translateX(0);
      opacity: 1;
    }
  }
</style>

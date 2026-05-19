<script lang="ts">
  import type { FileItem } from '$lib/types';
  import { Button } from '$lib/components/ui/button';
  import { Separator } from '$lib/components/ui/separator';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as Tabs from '$lib/components/ui/tabs';
  import { Slider } from '$lib/components/ui/slider';
  import {
    X,
    Music,
    Disc3,
    User,
    Calendar,
    Clock,
    HardDrive,
    Waves,
    FileAudio,
    CheckCircle2,
    AlertCircle,
    Loader2,
    Fingerprint,
    RotateCcw,
    Pause,
    Play
  } from '@lucide/svelte';
  import { cn } from '$lib/utils';
  import {
    resetSongEnrichment,
    getSongStreamUrl,
    parseSongId
  } from '$lib/api-client';
  import { lrclibWebSearchUrl, lrclibWebUrl } from '$lib/lrclib-url';
  import { acoustIdSourceConnected, lrclibSourceConnected } from '$lib/source-connection';
  import { playerStore } from '$lib/stores/player.svelte';
  import InfoRow from './InfoRow.svelte';
  import SourceRow from './SourceRow.svelte';
  import StatusBadge from './StatusBadge.svelte';
  import LyricsPanel from './LyricsPanel.svelte';

  type Props = {
    file: FileItem | null;
    onClose: () => void;
    onResetEnrichment?: () => void;
  };
  const { file, onClose, onResetEnrichment }: Props = $props();

  let resetState = $state<'idle' | 'loading' | 'success' | 'error'>('idle');
  let resetError = $state<string | null>(null);

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || Number.isNaN(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  function formatDuration(seconds: number): string {
    const mins = Math.floor(seconds / 60);
    const secs = seconds % 60;
    return `${mins}:${secs.toString().padStart(2, '0')}`;
  }

  function formatFileSize(bytes: number): string {
    const mb = bytes / (1024 * 1024);
    return `${mb.toFixed(1)} MB`;
  }

  const songId = $derived(file ? parseSongId(file.id) : null);
  const isThisSong = $derived(
    playerStore.currentSong?.id === songId && songId !== null
  );

  function handlePlay() {
    if (songId === null || !file?.metadata) return;
    void playerStore.playSong({
      id: songId,
      title: file.metadata.title,
      artist: file.metadata.artist,
      streamUrl: getSongStreamUrl(songId)
    });
  }

  async function handleResetEnrichment() {
    if (songId === null) return;
    resetState = 'loading';
    resetError = null;
    try {
      await resetSongEnrichment(songId);
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

  const playerCurrentTime = $derived(isThisSong ? playerStore.currentTime : 0);
  const playerDuration = $derived(isThisSong ? playerStore.duration : 0);
  const playerIsPlaying = $derived(isThisSong && playerStore.isPlaying);
</script>

{#if file && file.type === 'audio' && file.metadata}
  {@const metadata = file.metadata}
  <div class="border-border bg-card flex h-full max-h-full flex-col overflow-hidden border-l">
    <div class="border-border flex items-center justify-between border-b px-4 py-3">
      <h2 class="font-semibold">Track Details</h2>
      <Button variant="ghost" size="icon" onclick={onClose} class="size-8">
        <X class="size-4" />
      </Button>
    </div>

    <ScrollArea class="min-h-0 flex-1">
      <div class="p-4">
        <div class="mb-4 flex gap-4 sm:mb-0 sm:flex-col sm:gap-0">
          <div
            class="bg-secondary size-24 shrink-0 overflow-hidden rounded-lg sm:mb-4 sm:aspect-square sm:size-auto"
          >
            {#if metadata.albumArt}
              <img
                src={metadata.albumArt}
                alt={metadata.album}
                class="size-full object-cover"
                crossorigin="anonymous"
              />
            {:else}
              <div class="flex size-full items-center justify-center">
                <Music class="text-muted-foreground size-10 sm:size-16" />
              </div>
            {/if}
          </div>

          <div class="min-w-0 flex-1 sm:mb-4 sm:text-center">
            <h3 class="text-base font-semibold text-balance sm:text-lg">{metadata.title}</h3>
            <p class="text-muted-foreground text-sm sm:text-base">{metadata.artist}</p>
            <div class="mt-2 sm:hidden">
              <StatusBadge status={metadata.enrichmentStatus} />
            </div>
          </div>
        </div>

        <div class="mb-4 hidden justify-center sm:flex">
          <StatusBadge status={metadata.enrichmentStatus} />
        </div>

        {#if songId !== null}
          <div class="border-border bg-secondary/30 mb-4 rounded-lg border p-3">
            <div class="flex items-center justify-center">
              <Button
                variant="ghost"
                size="icon"
                class={cn(
                  'size-10 rounded-full transition-all',
                  playerIsPlaying
                    ? 'bg-primary text-primary-foreground hover:bg-primary/90'
                    : 'hover:bg-primary/10 hover:text-primary'
                )}
                onclick={isThisSong ? () => playerStore.togglePlay() : handlePlay}
                aria-label={playerIsPlaying ? 'Pause' : 'Play'}
              >
                {#if playerIsPlaying}
                  <Pause class="size-4" />
                {:else}
                  <Play class="size-4 translate-x-px" />
                {/if}
              </Button>
            </div>
            <div class="mt-2 flex items-center gap-2">
              <span class="text-muted-foreground w-9 shrink-0 text-right text-xs tabular-nums">
                {formatTime(playerCurrentTime)}
              </span>
              <Slider
                type="single"
                value={playerCurrentTime}
                max={playerDuration > 0 ? playerDuration : 1}
                min={0}
                step={0.01}
                disabled={!isThisSong || playerDuration === 0}
                class={cn('flex-1', !isThisSong && 'opacity-40')}
                onValueChange={(val) => {
                  if (isThisSong && typeof val === 'number') playerStore.seek(val);
                }}
                aria-label="Seek"
              />
              <span class="text-muted-foreground w-9 shrink-0 text-xs tabular-nums">
                {formatTime(playerDuration)}
              </span>
            </div>
          </div>
        {/if}

        <Separator class="my-4" />

        <Tabs.Root value="info" class="w-full">
          <Tabs.List class="grid w-full grid-cols-3">
            <Tabs.Trigger value="info">Info</Tabs.Trigger>
            <Tabs.Trigger value="lyrics">Lyrics</Tabs.Trigger>
            <Tabs.Trigger value="sources">Sources</Tabs.Trigger>
          </Tabs.List>

          <Tabs.Content value="info" class="mt-4 space-y-3">
            <InfoRow icon={Disc3} label="Album" value={metadata.album} />
            <InfoRow icon={User} label="Artist" value={metadata.artist} />
            <InfoRow
              icon={Calendar}
              label="Year"
              value={metadata.year > 0 ? metadata.year.toString() : 'Unknown'}
            />
            <InfoRow icon={Music} label="Genre" value={metadata.genre} />
            <Separator class="my-3" />
            <InfoRow icon={Clock} label="Duration" value={formatDuration(metadata.duration)} />
            <InfoRow icon={FileAudio} label="Format" value={metadata.format} />
            {#if metadata.bitrate > 0}
              <InfoRow icon={Waves} label="Bitrate" value={`${metadata.bitrate} kbps`} />
            {/if}
            {#if metadata.sampleRate > 0}
              <InfoRow
                icon={Waves}
                label="Sample Rate"
                value={`${(metadata.sampleRate / 1000).toFixed(1)} kHz`}
              />
            {/if}
            <InfoRow icon={HardDrive} label="File Size" value={formatFileSize(metadata.fileSize)} />
            {#if metadata.fingerprint}
              <Separator class="my-3" />
              <div class="space-y-1.5">
                <div class="text-muted-foreground flex items-center gap-2 text-sm">
                  <Fingerprint class="size-4" />
                  <span>Audio Fingerprint</span>
                </div>
                <code class="bg-secondary block rounded px-2 py-1.5 font-mono text-xs break-all">
                  {metadata.fingerprint}
                </code>
              </div>
            {/if}
          </Tabs.Content>

          <Tabs.Content value="lyrics" class="mt-4">
            <LyricsPanel
              {songId}
              syncedLyrics={metadata.syncedLyrics}
              plainLyrics={metadata.plainLyrics}
              lyricsStatus={metadata.lyricsStatus}
              hasSyncedLyrics={metadata.hasSyncedLyrics}
              hasPlainLyrics={metadata.hasPlainLyrics}
              isInstrumental={metadata.isInstrumental}
              currentTimeMs={isThisSong ? playerStore.currentTime * 1000 : null}
              onSeek={isThisSong ? (timeMs: number) => playerStore.seek(timeMs / 1000) : undefined}
              lrclibUrl={lrclibWebUrl(metadata.artist, metadata.title)}
            />
          </Tabs.Content>

          <Tabs.Content value="sources" class="mt-4 space-y-3">
            <p class="text-muted-foreground text-sm">Metadata enrichment sources</p>
            {#if metadata.matchedBy}
              <div class="bg-secondary/50 rounded-lg px-3 py-2">
                <p class="text-muted-foreground mb-1 text-xs">Matched via</p>
                <p class="text-sm font-medium">{metadata.matchedBy}</p>
              </div>
            {/if}
            <div class="space-y-2">
              <SourceRow
                name="AcoustID"
                connected={acoustIdSourceConnected(
                  metadata.sourceIds?.acoustIdTrackId,
                  metadata.matchedBy
                )}
                url={metadata.sourceIds?.acoustIdTrackId
                  ? `https://acoustid.org/track/${metadata.sourceIds.acoustIdTrackId}`
                  : 'https://acoustid.org'}
                label={metadata.sourceIds?.acoustIdTrackId
                  ? `acoustid.org/track/${metadata.sourceIds.acoustIdTrackId.slice(0, 8)}…`
                  : undefined}
              />
              <SourceRow
                name="MusicBrainz Recording"
                connected={Boolean(metadata.sourceIds?.musicBrainzId)}
                url={metadata.sourceIds?.musicBrainzId
                  ? `https://musicbrainz.org/recording/${metadata.sourceIds.musicBrainzId}`
                  : 'https://musicbrainz.org'}
                label={metadata.sourceIds?.musicBrainzId
                  ? `musicbrainz.org/recording/${metadata.sourceIds.musicBrainzId.slice(0, 8)}…`
                  : undefined}
              />
              {#if metadata.sourceIds?.musicBrainzReleaseId}
                <SourceRow
                  name="MusicBrainz Release"
                  connected={true}
                  url={`https://musicbrainz.org/release/${metadata.sourceIds.musicBrainzReleaseId}`}
                  label={`musicbrainz.org/release/${metadata.sourceIds.musicBrainzReleaseId.slice(0, 8)}…`}
                />
              {/if}
              <SourceRow
                name="Spotify"
                connected={Boolean(metadata.sourceIds?.spotifyId)}
                url={metadata.sourceIds?.spotifyId
                  ? `https://open.spotify.com/track/${metadata.sourceIds.spotifyId}`
                  : 'https://spotify.com'}
                label={metadata.sourceIds?.spotifyId
                  ? `open.spotify.com/track/${metadata.sourceIds.spotifyId.slice(0, 8)}…`
                  : undefined}
              />
              <SourceRow
                name="LRCLIB (Lyrics)"
                connected={lrclibSourceConnected({
                  lrclibId: metadata.sourceIds?.lrclibId,
                  lyricsStatus: metadata.lyricsStatus,
                  artist: metadata.artist,
                  title: metadata.title,
                  enrichmentStatus: metadata.enrichmentStatus
                })}
                url={lrclibWebUrl(metadata.artist, metadata.title)}
                label={lrclibWebSearchUrl(metadata.artist, metadata.title)
                  ? 'lrclib.net/search/…'
                  : undefined}
              />
            </div>
            <Separator class="my-3" />
            <Button
              variant="outline"
              class={cn(
                'w-full',
                resetState === 'success' && 'border-primary/50 text-primary',
                resetState === 'error' && 'border-destructive/50 text-destructive'
              )}
              size="sm"
              disabled={resetState === 'loading' || songId === null}
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
                Reset Failed
              {:else}
                <RotateCcw class="mr-1.5 size-3.5" />
                Re-enrich Metadata
              {/if}
            </Button>
            {#if resetError}
              <p class="text-destructive mt-1.5 text-xs">{resetError}</p>
            {/if}
          </Tabs.Content>
        </Tabs.Root>
      </div>
    </ScrollArea>

    <div class="border-border border-t p-3">
      <p class="text-muted-foreground truncate text-xs">
        <span class="font-medium">Path:</span>
        {file.path}
      </p>
    </div>
  </div>
{/if}

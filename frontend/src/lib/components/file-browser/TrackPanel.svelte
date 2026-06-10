<script lang="ts">
  import {
    AlertCircle,
    Check,
    CheckCircle2,
    Copy,
    FastForward,
    History,
    Loader2,
    Pause,
    Play,
    Rewind,
    RotateCcw,
    Sparkles,
    X
  } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as Tabs from '$lib/components/ui/tabs/index.js';
  import LyricsPanel from '$lib/components/file-browser/LyricsPanel.svelte';
  import SourceRow from '$lib/components/file-browser/SourceRow.svelte';
  import Waveform from '$lib/components/file-browser/Waveform.svelte';
  import {
    enrichSong,
    fetchEnrichmentDetail,
    toPlayerSong,
    mapEnrichmentStatus,
    resetSongEnrichment,
    fetchSongQualityGrade,
    gradeSong,
    copyQualitySongDossier,
    type ApiSong,
    type AlbumSummary,
    type EnrichmentDetail,
    type ProviderAttempt,
    type SongQualityGradeView,
    type QualityVerdict
  } from '$lib/api-client';
  import { fingerprintBars, fingerprintHash, providerAttemptRows } from '$lib/review-helpers';
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
    /**
     * Link to the standalone /track/[id] provenance timeline. When set, the
     * Enrichment tab shows a "View timeline" link.
     */
    timelineHref?: string;
  };
  const { album, song, trackIndex, onClose, onResetEnrichment, timelineHref }: Props = $props();

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
  let enrichState = $state<'idle' | 'loading' | 'success' | 'error'>('idle');
  let enrichOutcome = $state<string | null>(null);
  let enrichError = $state<string | null>(null);

  // Provider attempts (real candidate matches) are loaded lazily when the
  // Fingerprint tab is first viewed, and refetched when the song changes.
  let enrichmentDetail = $state<EnrichmentDetail | null>(null);
  let detailLoading = $state(false);
  let detailError = $state<string | null>(null);
  let loadedSongId = $state<number | null>(null);

  async function loadEnrichmentDetail(id: number) {
    detailLoading = true;
    detailError = null;
    try {
      enrichmentDetail = await fetchEnrichmentDetail(id);
      loadedSongId = id;
    } catch (err) {
      detailError = err instanceof Error ? err.message : 'Failed to load provider attempts';
    } finally {
      detailLoading = false;
    }
  }

  $effect(() => {
    if ((activeTab !== 'fingerprint' && activeTab !== 'enrichment') || detailLoading || loadedSongId === song.id)
      return;
    void loadEnrichmentDetail(song.id);
  });

  // AI quality grade for the Enrichment tab — loaded lazily, refetched per song.
  let quality = $state<SongQualityGradeView | null>(null);
  let qualityLoadedId = $state<number | null>(null);
  let gradeBusy = $state(false);
  let copied = $state(false);

  async function handleCopyDossier() {
    try {
      await copyQualitySongDossier(song.id);
      copied = true;
      setTimeout(() => (copied = false), 1500);
    } catch {
      // keep the panel quiet; failure leaves the icon unchanged
    }
  }

  $effect(() => {
    if (activeTab !== 'enrichment' || qualityLoadedId === song.id) return;
    const id = song.id;
    void (async () => {
      try {
        quality = await fetchSongQualityGrade(id);
        qualityLoadedId = id;
      } catch {
        // grade is optional UI; ignore load failures
      }
    })();
  });

  async function handleGradeNow() {
    gradeBusy = true;
    try {
      await gradeSong(song.id);
      quality = await fetchSongQualityGrade(song.id);
      qualityLoadedId = song.id;
    } catch {
      // surfaced via the unchanged grade card; keep the panel quiet
    } finally {
      gradeBusy = false;
    }
  }

  function verdictTint(v: QualityVerdict | undefined): string {
    switch (v) {
      case 'Excellent':
        return 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400 border-emerald-500/30';
      case 'Good':
        return 'bg-teal-500/15 text-teal-600 dark:text-teal-400 border-teal-500/30';
      case 'Questionable':
        return 'bg-amber-500/15 text-amber-600 dark:text-amber-400 border-amber-500/30';
      case 'Wrong':
        return 'bg-red-500/15 text-red-600 dark:text-red-400 border-red-500/30';
      default:
        return 'bg-muted text-muted-foreground border-border';
    }
  }

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
    const queue = album.songs.map((s) => toPlayerSong(s, album.artist));
    void playerStore.playSong(toPlayerSong(song, album.artist), queue, trackIndex);
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

  async function handleEnrichNow() {
    enrichState = 'loading';
    enrichError = null;
    enrichOutcome = null;
    try {
      // reset=true gives a clean re-run from scratch and returns the exact outcome —
      // works even when the automatic pipeline is disabled.
      const result = await enrichSong(song.id, true);
      enrichState = 'success';
      enrichOutcome = result.outcome;
      onResetEnrichment?.();
      setTimeout(() => {
        enrichState = 'idle';
        enrichOutcome = null;
      }, 4000);
    } catch (err) {
      enrichState = 'error';
      enrichError = err instanceof Error ? err.message : 'Failed to enrich song';
      setTimeout(() => {
        enrichState = 'idle';
        enrichError = null;
      }, 5000);
    }
  }

  const matchValue = $derived.by(() => {
    const v = song.matchConfidence ?? enrichmentDetail?.matchConfidence;
    return typeof v === 'number' ? Math.max(0, Math.min(1, v)) : null;
  });

  const enrichmentNormalized = $derived(mapEnrichmentStatus(song.enrichmentStatus));

  // The enrich action also builds the track into the library, so the label reflects the outcome:
  // "Add to library" for a track not yet built, "Update in library" once it has a destination.
  const inLibrary = $derived(!!song.destinationPath);

  // Real provider attempts → candidate rows, guarded so stale data from a
  // previously-viewed song isn't shown while the new one loads.
  const attemptRows = $derived(
    loadedSongId === song.id ? providerAttemptRows(enrichmentDetail) : []
  );

  // Provider attempts keyed by backend provider name, for the Enrichment tab's
  // connected dots. Empty until the detail loads for the current song.
  const attemptByProvider = $derived.by(() => {
    const map = new Map<string, ProviderAttempt>();
    if (loadedSongId !== song.id || !enrichmentDetail) return map;
    for (const a of enrichmentDetail.providerAttempts) map.set(a.provider, a);
    return map;
  });

  type EnrichmentSource = { key: string; name: string; connected: boolean; url?: string; label?: string };

  // The full catalogue of enrichment sources wired into the pipeline. AcoustID /
  // MusicBrainz / Spotify resolve their connected state from stored song ids;
  // Deezer / Apple Music / Tracker have no stored id, so they reflect whether the
  // provider produced a candidate on its last attempt. Tracker is opt-in and niche,
  // so it only appears once it has actually run for this song.
  const enrichmentSources = $derived.by<EnrichmentSource[]>(() => {
    const query = encodeURIComponent(`${trackArtist} ${trackTitle}`.trim());
    const matched = (provider: string) => attemptByProvider.get(provider)?.candidate != null;

    const sources: EnrichmentSource[] = [
      {
        key: 'acoustid',
        name: 'AcoustID',
        connected: acoustIdSourceConnected(song.acoustIdTrackId ?? undefined, song.matchedBy ?? undefined),
        url: song.acoustIdTrackId ? `https://acoustid.org/track/${song.acoustIdTrackId}` : 'https://acoustid.org',
        label: song.acoustIdTrackId ? `acoustid.org/track/${song.acoustIdTrackId.slice(0, 8)}…` : undefined
      },
      {
        key: 'musicbrainz-recording',
        name: 'MusicBrainz Recording',
        connected: Boolean(song.musicBrainzId),
        url: song.musicBrainzId
          ? `https://musicbrainz.org/recording/${song.musicBrainzId}`
          : 'https://musicbrainz.org',
        label: song.musicBrainzId ? `musicbrainz.org/recording/${song.musicBrainzId.slice(0, 8)}…` : undefined
      }
    ];

    if (song.musicBrainzReleaseId) {
      sources.push({
        key: 'musicbrainz-release',
        name: 'MusicBrainz Release',
        connected: true,
        url: `https://musicbrainz.org/release/${song.musicBrainzReleaseId}`,
        label: `musicbrainz.org/release/${song.musicBrainzReleaseId.slice(0, 8)}…`
      });
    }

    sources.push({
      key: 'spotify',
      name: 'Spotify',
      connected: Boolean(song.spotifyId),
      url: song.spotifyId ? `https://open.spotify.com/track/${song.spotifyId}` : 'https://spotify.com',
      label: song.spotifyId ? `open.spotify.com/track/${song.spotifyId.slice(0, 8)}…` : undefined
    });

    sources.push({
      key: 'deezer',
      name: 'Deezer',
      connected: matched('Deezer'),
      url: query ? `https://www.deezer.com/search/${query}` : 'https://www.deezer.com',
      label: query ? 'deezer.com/search/…' : undefined
    });

    sources.push({
      key: 'apple-music',
      name: 'Apple Music',
      connected: matched('AppleMusic'),
      url: query ? `https://music.apple.com/search?term=${query}` : 'https://music.apple.com',
      label: query ? 'music.apple.com/search/…' : undefined
    });

    sources.push({
      key: 'lrclib',
      name: 'LRCLIB (Lyrics)',
      connected: lrclibSourceConnected({
        lrclibId: song.lrclibId ?? undefined,
        lyricsStatus,
        artist: trackArtist,
        title: trackTitle,
        enrichmentStatus: enrichmentNormalized
      }),
      url: lrclibWebUrl(trackArtist, trackTitle),
      label: lrclibWebSearchUrl(trackArtist, trackTitle) ? 'lrclib.net/search/…' : undefined
    });

    if (attemptByProvider.has('Tracker')) {
      sources.push({
        key: 'tracker',
        name: 'Community Tracker',
        connected: matched('Tracker')
      });
    }

    return sources;
  });

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
    ['ISRC', song.isrc ?? '—'],
    ['Format', bitrateLabel()],
    ['Sample rate', song.sampleRate ? `${(song.sampleRate / 1000).toFixed(1)} kHz` : '—'],
    ['File size', formatFileSize(song.fileSizeBytes)],
    ['Status', enrichmentNormalized]
  ]);

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }
</script>

<aside
  class="flex h-full max-h-full min-h-0 flex-col overflow-hidden bg-transparent mh-track-panel-enter"
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
          class="text-muted-foreground hover:text-foreground data-[state=active]:border-b-primary data-[state=active]:text-foreground rounded-none border-0 border-b-2 border-transparent bg-transparent px-2.5 py-2 text-xs font-medium shadow-none transition-colors data-[state=active]:bg-transparent data-[state=active]:shadow-none"
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
      <div class="flex min-h-0 flex-1 flex-col px-5 py-3">
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
                {matchValue !== null ? matchValue.toFixed(2) : '—'}
              </div>
            </div>
          </div>

          <div class="bg-surface-sunken mt-4 flex h-16 items-end gap-[2px] rounded p-1.5">
            {#each fingerprintBars(song.fingerprint) as h, i (i)}
              <div
                class="from-primary flex-1 rounded-[1px] bg-gradient-to-t to-cyan-300/70"
                style="height: {h}%; min-height: 2px;"
              ></div>
            {/each}
          </div>

          <div class="bg-surface-sunken text-muted-foreground mt-2.5 rounded px-2.5 py-2 font-mono text-[10px] leading-relaxed break-all">
            {song.fingerprint ? fingerprintHash(song.fingerprint) : '— no fingerprint —'}
          </div>

          <div class="mt-5">
            <div class="text-muted-foreground text-[10px] font-semibold tracking-[0.08em] uppercase">
              {#if attemptRows.length}
                {attemptRows.length} provider {attemptRows.length === 1 ? 'attempt' : 'attempts'}
              {:else}
                Provider attempts
              {/if}
            </div>
            <div class="mt-2 flex flex-col gap-1.5">
              {#if detailLoading}
                <div class="text-muted-foreground flex items-center gap-2 px-1 py-3 text-[12px]">
                  <Loader2 class="size-3.5 animate-spin" /> Loading provider attempts…
                </div>
              {:else if detailError}
                <div class="text-destructive flex items-center gap-2 px-1 py-3 text-[12px]">
                  <AlertCircle class="size-3.5" /> {detailError}
                </div>
              {:else if !attemptRows.length}
                <div class="text-muted-foreground px-1 py-3 text-[12px]">No provider attempts yet.</div>
              {:else}
                {#each attemptRows as row (row.key)}
                  <div
                    class={cn(
                      'border-border flex items-center gap-3 rounded-md border p-2.5',
                      row.chosen && 'border-primary bg-primary/8',
                      !row.matched && 'opacity-60'
                    )}
                  >
                    <span class={cn('w-10 font-mono text-sm font-semibold', row.chosen ? 'text-primary' : 'text-muted-foreground')}>
                      {row.score !== null ? row.score.toFixed(2) : '—'}
                    </span>
                    <div class="min-w-0 flex-1">
                      <div class="truncate text-[12px] font-medium">
                        {#if row.matched}
                          {row.title || '(untitled)'}{row.artist ? ` — ${row.artist}` : ''}{row.album ? ` (${row.album}${row.year ? `, ${row.year}` : ''})` : ''}
                        {:else}
                          {row.error ?? (row.status === 'NoMatch' ? 'No match' : row.status)}
                        {/if}
                      </div>
                      <div class="text-muted-foreground mt-0.5 flex items-center gap-1.5 text-[11px]">
                        <span>{row.source}</span>
                        {#if !row.matched}
                          <span class="bg-muted text-muted-foreground rounded px-1 py-px font-mono text-[9px] tracking-wide uppercase">
                            {row.status}
                          </span>
                        {/if}
                      </div>
                    </div>
                    {#if row.chosen}
                      <span class="bg-primary/15 text-primary rounded px-1.5 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
                        CHOSEN
                      </span>
                    {/if}
                  </div>
                {/each}
              {/if}
            </div>
          </div>
        </div>
      </ScrollArea>
    </Tabs.Content>

    <Tabs.Content value="enrichment" class="flex min-h-0 flex-1 flex-col">
      <ScrollArea class="min-h-0 flex-1">
        <div class="space-y-3 px-5 py-4 text-xs">
          {#if timelineHref}
            <Button href={timelineHref} variant="outline" size="sm" class="w-full">
              <History class="mr-1.5 size-3.5" />
              View timeline
            </Button>
          {/if}

          {#if song.matchedBy}
            <div class="bg-muted/50 rounded-lg px-3 py-2">
              <p class="text-muted-foreground mb-0.5 text-[10px] tracking-wider uppercase">Matched via</p>
              <p class="text-[12.5px] font-medium">{song.matchedBy}</p>
            </div>
          {/if}

          <!-- AI quality grade -->
          <div class="border-border rounded-lg border px-3 py-2.5">
            <div class="mb-1.5 flex items-center justify-between gap-2">
              <p class="text-muted-foreground text-[10px] tracking-wider uppercase">AI quality</p>
              {#if quality?.graded}
                <span class={cn('rounded-md border px-1.5 py-0.5 text-[10px] font-semibold', verdictTint(quality.verdict))}>
                  {quality.verdict} · {quality.score}
                </span>
              {/if}
            </div>
            {#if quality?.graded}
              {#if quality.summary}
                <p class="text-muted-foreground mb-1.5 text-[11.5px] leading-snug">{quality.summary}</p>
              {/if}
              {#if quality.issues && quality.issues.length > 0}
                <div class="mb-1.5 flex flex-wrap gap-1">
                  {#each quality.issues as issue, i (i)}
                    <code class="bg-muted/60 rounded px-1 py-px font-mono text-[10px]">{issue.code}</code>
                  {/each}
                </div>
              {/if}
              {#if quality.model || quality.gradedAtUtc}
                <p class="text-muted-foreground/70 text-[10px]">
                  {quality.model ?? ''}{#if quality.model && quality.gradedAtUtc} · {/if}{#if quality.gradedAtUtc}{new Date(quality.gradedAtUtc).toLocaleString()}{/if}
                </p>
              {/if}
            {:else}
              <p class="text-muted-foreground/70 text-[11.5px]">Not graded yet.</p>
            {/if}
            <div class="mt-2 flex gap-1.5">
              <Button variant="outline" size="sm" class="h-7 flex-1 text-[11px]" disabled={gradeBusy} onclick={handleGradeNow}>
                {#if gradeBusy}
                  <Loader2 class="mr-1 size-3 animate-spin" />
                {:else}
                  <Sparkles class="mr-1 size-3" />
                {/if}
                {quality?.graded ? 'Re-grade' : 'Grade now'}
              </Button>
              <Button
                variant="outline"
                size="sm"
                class="h-7 text-[11px]"
                aria-label="Copy dossier"
                onclick={handleCopyDossier}
              >
                {#if copied}<Check class="size-3" />{:else}<Copy class="size-3" />{/if}
              </Button>
            </div>
          </div>

          <div class="space-y-2">
            {#each enrichmentSources as src (src.key)}
              <SourceRow name={src.name} connected={src.connected} url={src.url} label={src.label} />
            {/each}
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
              Metadata reset
            {:else if resetState === 'error'}
              <AlertCircle class="mr-1.5 size-3.5" />
              Reset failed
            {:else}
              <RotateCcw class="mr-1.5 size-3.5" />
              Reset metadata
            {/if}
          </Button>
          {#if resetError}
            <p class="text-destructive text-[11px]">{resetError}</p>
          {/if}

          <Button
            variant="secondary"
            class={cn(
              'mt-2 w-full',
              enrichState === 'success' && 'text-primary',
              enrichState === 'error' && 'text-destructive'
            )}
            size="sm"
            disabled={enrichState === 'loading'}
            onclick={handleEnrichNow}
          >
            {#if enrichState === 'loading'}
              <Loader2 class="mr-1.5 size-3.5 animate-spin" />
              {inLibrary ? 'Updating…' : 'Adding…'}
            {:else if enrichState === 'success'}
              <CheckCircle2 class="mr-1.5 size-3.5" />
              {enrichOutcome ?? 'Done'}
            {:else if enrichState === 'error'}
              <AlertCircle class="mr-1.5 size-3.5" />
              {inLibrary ? 'Update failed' : 'Add failed'}
            {:else}
              <Sparkles class="mr-1.5 size-3.5" />
              {inLibrary ? 'Update in library' : 'Add to library'}
            {/if}
          </Button>
          {#if enrichError}
            <p class="text-destructive text-[11px]">{enrichError}</p>
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

<script lang="ts">
  import type { LyricsStatus } from '$lib/types';
  import { fetchTrackLyrics } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import { Badge } from '$lib/components/ui/badge';
  import {
    AlertCircle,
    AlignLeft,
    CheckCircle2,
    ExternalLink,
    FileText,
    Loader2,
    Music,
    Timer
  } from '@lucide/svelte';
  import { cn } from '$lib/utils';
  import LyricsStatusBadge from './LyricsStatusBadge.svelte';

  type Props = {
    songId: number | null;
    syncedLyrics?: string;
    plainLyrics?: string;
    lyricsStatus?: LyricsStatus;
    hasSyncedLyrics?: boolean;
    hasPlainLyrics?: boolean;
    isInstrumental?: boolean;
    currentTimeMs?: number | null;
    onSeek?: (timeMs: number) => void;
    lrclibUrl?: string;
  };

  const {
    songId,
    syncedLyrics: syncedLyricsFromProps,
    plainLyrics: plainLyricsFromProps,
    lyricsStatus,
    hasSyncedLyrics: hasSyncedFromProps,
    hasPlainLyrics: hasPlainFromProps,
    isInstrumental,
    currentTimeMs,
    onSeek,
    lrclibUrl
  }: Props = $props();

  type LrcLine = { timeMs: number; text: string };

  function parseLrc(lrc: string): LrcLine[] {
    const lines: LrcLine[] = [];
    for (const raw of lrc.split('\n')) {
      const match = raw.match(/^\[(\d{2}):(\d{2})\.(\d{2,3})\]\s*(.*)$/);
      if (!match) continue;
      const mins = parseInt(match[1], 10);
      const secs = parseInt(match[2], 10);
      const cs = parseInt(match[3].padEnd(3, '0'), 10);
      const timeMs = mins * 60000 + secs * 1000 + cs;
      lines.push({ timeMs, text: match[4] ?? '' });
    }
    return lines;
  }

  function formatLrcTime(ms: number): string {
    const totalSecs = Math.floor(ms / 1000);
    const mins = Math.floor(totalSecs / 60);
    const secs = totalSecs % 60;
    return `${mins}:${secs.toString().padStart(2, '0')}`;
  }

  let showSynced = $state(true);
  let loadedSynced = $state<string | null | undefined>(undefined);
  let loadedPlain = $state<string | null | undefined>(undefined);
  let loadState = $state<'idle' | 'loading' | 'error'>('idle');

  // Re-sync from props whenever the parent passes new lyric content (e.g.
  // switching tracks). The initial-only $state(prop) pattern would capture
  // stale data after the first mount.
  let syncedFromPropsApplied = false;
  $effect(() => {
    if (syncedFromPropsApplied) return;
    loadedSynced = syncedLyricsFromProps;
    loadedPlain = plainLyricsFromProps;
    syncedFromPropsApplied = true;
  });

  let containerEl: HTMLDivElement | undefined = $state();
  let lineEls = $state<(HTMLDivElement | null)[]>([]);
  let prevActiveIndex = -1;

  const hasSynced = $derived(Boolean(loadedSynced) || Boolean(hasSyncedFromProps));
  const hasPlain = $derived(Boolean(loadedPlain) || Boolean(hasPlainFromProps));
  const hasAny = $derived(hasSynced || hasPlain);

  // Auto-fetch when we know lyrics exist but content isn't loaded yet.
  $effect(() => {
    const needsLoad =
      hasAny &&
      !loadedSynced &&
      !loadedPlain &&
      loadState === 'idle' &&
      songId !== null;
    if (!needsLoad || songId === null) return;
    loadState = 'loading';
    fetchTrackLyrics(songId)
      .then((data) => {
        loadedSynced = data.synced ?? undefined;
        loadedPlain = data.plain ?? undefined;
        loadState = 'idle';
      })
      .catch(() => {
        loadState = 'error';
      });
  });

  const parsedLines = $derived(
    Boolean(loadedSynced) && showSynced ? parseLrc(loadedSynced!) : null
  );

  const activeLineIndex = $derived.by(() => {
    if (!parsedLines || currentTimeMs == null || currentTimeMs < 0) return -1;
    let active = -1;
    for (let i = 0; i < parsedLines.length; i++) {
      if (parsedLines[i].timeMs <= currentTimeMs) active = i;
      else break;
    }
    return active;
  });

  const isTracking = $derived(parsedLines != null && currentTimeMs != null && currentTimeMs >= 0);

  $effect(() => {
    // Reset on new parsed lines (different song or toggle).
    void parsedLines;
    prevActiveIndex = -1;
    lineEls = [];
  });

  $effect(() => {
    if (activeLineIndex < 0 || activeLineIndex === prevActiveIndex) return;
    prevActiveIndex = activeLineIndex;
    const el = lineEls[activeLineIndex];
    const container = containerEl;
    if (!el || !container) return;

    const elRect = el.getBoundingClientRect();
    const containerRect = container.getBoundingClientRect();
    const offset = elRect.top - containerRect.top + container.scrollTop;
    const targetScroll = offset - container.clientHeight / 2 + el.clientHeight / 2;
    container.scrollTo({ top: Math.max(0, targetScroll), behavior: 'smooth' });
  });

  const showSyncedToggle = $derived(Boolean(loadedSynced) && Boolean(loadedPlain));
</script>

{#if isInstrumental}
  <div class="flex min-h-0 flex-1 flex-col items-center justify-center gap-2 py-8 text-center">
    <Music class="text-muted-foreground size-10 opacity-40" />
    <p class="text-muted-foreground text-sm">
      This track is instrumental — no lyrics expected.
    </p>
    <LyricsStatusBadge status="Instrumental" />
  </div>
{:else if loadState === 'loading'}
  <div class="flex min-h-0 flex-1 flex-col items-center justify-center gap-2 py-8 text-center">
    <Loader2 class="text-muted-foreground size-8 animate-spin" />
    <p class="text-muted-foreground text-sm">Loading lyrics…</p>
  </div>
{:else if loadState === 'error'}
  <div class="flex min-h-0 flex-1 flex-col items-center justify-center gap-2 py-8 text-center">
    <AlertCircle class="text-destructive size-8 opacity-70" />
    <p class="text-muted-foreground text-sm">Failed to load lyrics.</p>
    <Button variant="outline" size="sm" onclick={() => (loadState = 'idle')}>Retry</Button>
  </div>
{:else if !hasAny}
  <div class="flex min-h-0 flex-1 flex-col items-center justify-center gap-2 py-8 text-center">
    <FileText class="text-muted-foreground size-10 opacity-50" />
    <p class="text-muted-foreground text-sm">
      {#if lyricsStatus === 'NotFound'}
        No lyrics found in LRCLIB for this track.
      {:else if lyricsStatus === 'Failed'}
        Lyrics fetch encountered an error.
      {:else}
        Lyrics have not been fetched yet — they are enriched automatically after a successful
        metadata match.
      {/if}
    </p>
    <LyricsStatusBadge status={lyricsStatus} />
  </div>
{:else}
  <div class="flex min-h-0 flex-1 flex-col gap-3">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <div class="flex min-w-0 items-center gap-2">
        <LyricsStatusBadge status={lyricsStatus} />
        {#if hasSynced}
          <span
            title="Synced lyrics (LRC)"
            aria-label="Synced lyrics (LRC)"
            role="img"
            class="inline-flex"
          >
            <CheckCircle2
              class="size-4 shrink-0 text-green-600 dark:text-green-500"
              aria-hidden="true"
            />
          </span>
        {/if}
      </div>
      {#if showSyncedToggle}
        <div class="border-border flex overflow-hidden rounded-md border text-xs">
          <button
            class={cn(
              'flex items-center gap-1 px-2 py-1 transition-colors',
              showSynced
                ? 'bg-primary text-primary-foreground'
                : 'text-muted-foreground hover:text-foreground bg-transparent'
            )}
            onclick={() => (showSynced = true)}
          >
            <Timer class="size-3" />
            Synced
          </button>
          <button
            class={cn(
              'flex items-center gap-1 px-2 py-1 transition-colors',
              !showSynced
                ? 'bg-primary text-primary-foreground'
                : 'text-muted-foreground hover:text-foreground bg-transparent'
            )}
            onclick={() => (showSynced = false)}
          >
            <AlignLeft class="size-3" />
            Plain
          </button>
        </div>
      {:else if loadedSynced && !loadedPlain}
        <Badge variant="outline" class="text-muted-foreground gap-1 text-xs">
          <Timer class="size-3" />
          Synced LRC
        </Badge>
      {:else if !loadedSynced && loadedPlain}
        <Badge variant="outline" class="text-muted-foreground gap-1 text-xs">
          <AlignLeft class="size-3" />
          Plain text
        </Badge>
      {/if}
    </div>

    <div
      bind:this={containerEl}
      class="bg-secondary/50 min-h-0 flex-1 overflow-y-auto rounded-lg p-4 scroll-smooth"
    >
      {#if parsedLines}
        <div class="font-sans text-sm leading-relaxed">
          {#each parsedLines as line, i (i)}
            {@const isActive = isTracking && i === activeLineIndex}
            {@const isPast = isTracking && activeLineIndex >= 0 && i < activeLineIndex}
            {@const isFuture = isTracking && activeLineIndex >= 0 && i > activeLineIndex}
            <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
            <div
              bind:this={lineEls[i]}
              class={cn(
                '-mx-1 flex gap-2 rounded-sm px-1 py-0.5 transition-all duration-300',
                isActive && 'bg-primary/10 text-primary font-semibold',
                isPast && 'text-muted-foreground',
                isFuture && 'text-muted-foreground/50',
                !isTracking && 'text-foreground',
                onSeek && 'hover:bg-primary/5 cursor-pointer'
              )}
              role={onSeek ? 'button' : undefined}
              tabindex={onSeek ? 0 : undefined}
              onclick={onSeek ? () => onSeek(line.timeMs) : undefined}
              onkeydown={onSeek
                ? (e: KeyboardEvent) => {
                    if (e.key === 'Enter' || e.key === ' ') {
                      e.preventDefault();
                      onSeek(line.timeMs);
                    }
                  }
                : undefined}
            >
              <span
                class={cn(
                  'w-12 shrink-0 pt-0.5 font-mono text-xs',
                  isActive ? 'text-primary/70' : 'text-muted-foreground/60'
                )}
              >
                {formatLrcTime(line.timeMs)}
              </span>
              <span class={cn('flex-1', !line.text && 'select-none opacity-0')}>
                {line.text || '·'}
              </span>
            </div>
          {/each}
        </div>
      {:else}
        <pre class="font-sans text-sm leading-relaxed whitespace-pre-wrap">{showSynced
            ? loadedSynced
            : loadedPlain}</pre>
      {/if}
    </div>

    {#if lrclibUrl}
      <div class="text-muted-foreground flex items-center gap-1.5 text-xs">
        <span>Source:</span>
        <a
          href={lrclibUrl}
          target="_blank"
          rel="noopener noreferrer"
          class="text-primary hover:text-primary/80 inline-flex items-center gap-1 transition-colors"
        >
          LRCLIB
          <ExternalLink class="size-3" />
        </a>
      </div>
    {/if}
  </div>
{/if}

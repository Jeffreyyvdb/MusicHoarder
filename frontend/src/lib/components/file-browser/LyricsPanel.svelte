<script lang="ts">
  import type { LyricsProvenance, LyricsStatus } from '$lib/types';
  import { page } from '$app/state';
  import { Button } from '$lib/components/ui/button';
  import { Badge } from '$lib/components/ui/badge';
  import * as ToggleGroup from '$lib/components/ui/toggle-group/index.js';
  import { fade } from 'svelte/transition';
  import {
    AlertCircle,
    AlignLeft,
    AudioLines,
    CircleCheck,
    ExternalLink,
    FileText,
    Loader2,
    Music,
    RefreshCw,
    Timer
  } from '@lucide/svelte';
  import { cn } from '$lib/utils';
  import { parseLrc } from '$lib/lyrics/parse-lrc';
  import { createLyricsDoc, type LyricsDoc } from '$lib/lyrics/lyrics-doc.svelte';
  import { prefersReducedMotion } from '$lib/motion';
  import LyricsStatusBadge from './LyricsStatusBadge.svelte';
  import AiLyricsBadge from './AiLyricsBadge.svelte';
  import { isAdmin } from '$lib/auth/capabilities';

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
    /**
     * 'panel' is the compact boxed look (timestamp gutter, status chrome) of the compare view.
     * 'theater' is the Apple-Music full-screen look: big bold lines, no gutter, transparent over
     * the cover wash, minimal chrome.
     */
    variant?: 'panel' | 'theater';
    /**
     * Secondary lyrics document (pronunciation guide or translation) rendered stacked beneath each
     * original line. Generated server-side from the same lines with identical timestamps, so it
     * aligns 1:1 with the original by index (timeMs lookup as a fallback).
     */
    secondarySynced?: string;
    secondaryPlain?: string;
    /**
     * How much of these lyrics came from an AI. Passed in by callers that already hold the lyrics
     * (the share page fetches through its own anonymous endpoint); when the panel fetches for
     * itself it reads the value off that response instead.
     */
    provenance?: LyricsProvenance | null;
    /**
     * A lyrics document owned by the caller (Now Playing keeps one per song so its ⋯ menu can act
     * on it while no panel is mounted). Without one the panel makes its own from the props above.
     */
    doc?: LyricsDoc;
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
    lrclibUrl,
    variant = 'panel',
    secondarySynced,
    secondaryPlain,
    provenance: provenanceFromProps,
    doc: docFromProps
  }: Props = $props();

  const theater = $derived(variant === 'theater');

  function formatLrcTime(ms: number): string {
    const totalSecs = Math.floor(ms / 1000);
    const mins = Math.floor(totalSecs / 60);
    const secs = totalSecs % 60;
    return `${mins}:${secs.toString().padStart(2, '0')}`;
  }

  const ownDoc = createLyricsDoc({
    songId: () => songId,
    synced: () => syncedLyricsFromProps,
    plain: () => plainLyricsFromProps,
    hasSynced: () => hasSyncedFromProps,
    hasPlain: () => hasPlainFromProps,
    status: () => lyricsStatus,
    provenance: () => provenanceFromProps
  });
  const doc = $derived(docFromProps ?? ownDoc);

  // Re-sync from the inputs whenever the song changes, then fetch the text if only the flags came
  // in. Both are no-ops on repeat (see lyrics-doc), so an owner driving the same document from its
  // own effects is harmless.
  $effect(() => doc.syncToSong());
  $effect(() => doc.ensureLoaded());

  let containerEl: HTMLDivElement | undefined = $state();

  const loadedSynced = $derived(doc.synced);
  const loadedPlain = $derived(doc.plain);
  const showSynced = $derived(doc.showSynced);

  const parsedLines = $derived(
    Boolean(loadedSynced) && showSynced ? parseLrc(loadedSynced!) : null
  );
  // An empty array is truthy in JS, so guard on length: a non-empty synced string that
  // carries no parseable timestamps must NOT render an empty timed list — it falls back
  // to the raw/plain text below instead of a blank panel.
  const hasParsedLines = $derived(parsedLines != null && parsedLines.length > 0);

  // Text for the non-timed fallback view. When synced lyrics are present but un-timed
  // (no parseable timestamps), prefer real plain lyrics, then the raw synced string so
  // something is always visible.
  const fallbackText = $derived.by(() => {
    if (!showSynced) return loadedPlain ?? '';
    return loadedPlain ?? loadedSynced ?? '';
  });

  const activeLineIndex = $derived.by(() => {
    if (!parsedLines || currentTimeMs == null || currentTimeMs < 0) return -1;
    let active = -1;
    for (let i = 0; i < parsedLines.length; i++) {
      if (parsedLines[i].timeMs <= currentTimeMs) active = i;
      else break;
    }
    return active;
  });

  const isTracking = $derived(hasParsedLines && currentTimeMs != null && currentTimeMs >= 0);

  // Secondary (pronunciation/translation) lines, aligned to the primary parsed lines. Index
  // alignment is the primary path — the server copies the original timestamps verbatim, so both
  // documents expand identically through parseLrc; the timeMs map is a belt-and-braces fallback
  // for originals with timestamp-only lines the generator dropped.
  const parsedSecondary = $derived(
    secondarySynced && Boolean(loadedSynced) && showSynced ? parseLrc(secondarySynced) : null
  );
  const secondaryByIndex = $derived.by(() => {
    if (!parsedLines || parsedLines.length === 0 || !parsedSecondary || parsedSecondary.length === 0)
      return null;
    if (parsedSecondary.length === parsedLines.length) return parsedSecondary.map((l) => l.text);
    const byTime = new Map(parsedSecondary.map((l) => [l.timeMs, l.text]));
    return parsedLines.map((l) => byTime.get(l.timeMs) ?? '');
  });

  // Untimed fallback pairing: the generator dropped blank lines, so the j-th secondary line
  // belongs to the j-th non-blank primary line.
  const pairedFallbackLines = $derived.by(() => {
    if (!secondaryPlain || !fallbackText) return null;
    const primary = fallbackText.split('\n');
    const secondary = secondaryPlain.split('\n');
    let j = 0;
    return primary.map((text) => ({
      text,
      secondary: text.trim().length > 0 ? (secondary[j++] ?? '') : ''
    }));
  });

  // --- Follow mode ---
  //
  // Auto-scroll keeps the active line centred, but it must not fight the user: browsing
  // ahead through the lyrics disengages following, and the floating "Sync" pill re-engages
  // it (the Spotify / Apple Music contract). Disengaging keys off manual input events
  // (wheel / touch / scroll keys), not the scroll event, because a scroll listener cannot
  // tell our own smooth scroll from the user's.
  let followActive = $state(true);
  let followForSongId: number | null = null;
  $effect(() => {
    if (followForSongId === songId) return;
    followForSongId = songId;
    followActive = true;
  });

  function scrollActiveLineIntoView() {
    const idx = activeLineIndex;
    const container = containerEl;
    if (idx < 0 || !container) return;
    const el = container.querySelector<HTMLElement>(`[data-lyric-line="${idx}"]`);
    if (!el) return;

    const elRect = el.getBoundingClientRect();
    const containerRect = container.getBoundingClientRect();
    const offset = elRect.top - containerRect.top + container.scrollTop;
    const targetScroll = offset - container.clientHeight / 2 + el.clientHeight / 2;
    // JS smooth scrolling is not covered by the global Reduce Motion clamp, so ask each time: under
    // Reduce Motion the lines jump instead of gliding.
    container.scrollTo({
      top: Math.max(0, targetScroll),
      behavior: prefersReducedMotion() ? 'auto' : 'smooth'
    });
  }

  $effect(() => {
    void parsedLines; // re-center when the track / lyric set changes
    void activeLineIndex;
    if (!followActive) return;
    scrollActiveLineIntoView();
  });

  function disengageFollow() {
    if (followActive) followActive = false;
  }

  // Re-engaging is enough: the effect above reacts to `followActive` and re-centres.
  function resumeFollow() {
    followActive = true;
  }

  // Seeking from a lyric line is a "play from here" gesture, so it re-engages following.
  function seekToLine(timeMs: number) {
    onSeek?.(timeMs);
    followActive = true;
  }

  const SCROLL_KEYS = new Set(['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End', ' ']);

  // Only offered while there is a synced position to jump back to.
  const showSyncPill = $derived(hasParsedLines && isTracking && !followActive);
</script>

{#snippet recheckButton(label: string)}
  <!-- Re-checking LRCLIB is a server write on the owner's row — hidden for friend/demo views. -->
  {#if isAdmin(page.data.user)}
    <Button
      variant="subtle"
      size="sm"
      class="h-9 gap-1.5 px-3.5 pointer-coarse:h-11 pointer-coarse:px-4"
      disabled={doc.recheckState === 'checking' || songId === null}
      onclick={() => doc.recheck()}
    >
      {#if doc.recheckState === 'checking'}
        <Loader2 class="size-3.5 animate-spin" />
        Checking LRCLIB…
      {:else}
        <RefreshCw class="size-3.5" />
        {label}
      {/if}
    </Button>
    {#if doc.recheckState === 'unchanged'}
      <p class="text-footnote text-muted-foreground">
        LRCLIB still has nothing new for this track.
      </p>
    {/if}
  {/if}
{/snippet}

{#if isInstrumental}
  <div class="flex min-h-0 flex-1 flex-col items-center justify-center gap-2 py-8 text-center">
    <Music class="text-muted-foreground size-10" />
    <p class="text-subheadline text-muted-foreground">
      This track is instrumental — no lyrics expected.
    </p>
    <LyricsStatusBadge status="Instrumental" />
  </div>
{:else if doc.loadState === 'loading'}
  <div class="flex min-h-0 flex-1 flex-col items-center justify-center gap-2 py-8 text-center">
    <Loader2 class="text-muted-foreground size-8 animate-spin" />
    <p class="text-subheadline text-muted-foreground">Loading lyrics…</p>
  </div>
{:else if doc.loadState === 'error'}
  <div class="flex min-h-0 flex-1 flex-col items-center justify-center gap-2 py-8 text-center">
    <AlertCircle class="text-destructive-text size-8" />
    <p class="text-subheadline text-muted-foreground">Failed to load lyrics.</p>
    <Button variant="gray" class="h-9 rounded-full px-4 pointer-coarse:h-11" onclick={() => doc.retry()}>
      Retry
    </Button>
  </div>
{:else if !doc.hasAny}
  <div class="flex min-h-0 flex-1 flex-col items-center justify-center gap-3 px-4 py-8 text-center">
    <FileText class="text-muted-foreground size-10" />
    <p class="text-subheadline text-muted-foreground max-w-xs text-balance">
      {#if doc.status === 'NotFound'}
        No lyrics found in LRCLIB for this track.
      {:else if doc.status === 'Failed'}
        Lyrics fetch encountered an error.
      {:else}
        Lyrics have not been fetched yet — they are enriched automatically after a successful
        metadata match.
      {/if}
    </p>
    <LyricsStatusBadge status={doc.status} />
    {@render recheckButton('Check LRCLIB again')}
  </div>
{:else}
  <div class="flex min-h-0 flex-1 flex-col gap-3">
    {#if !theater}
      <div class="flex flex-wrap items-center justify-between gap-2">
        <div class="flex min-w-0 flex-wrap items-center gap-2">
          <LyricsStatusBadge status={doc.status} />
          <AiLyricsBadge provenance={doc.provenance} />
          {#if doc.hasSynced}
            <span
              title="Synced lyrics (LRC)"
              aria-label="Synced lyrics (LRC)"
              role="img"
              class="inline-flex"
            >
              <CircleCheck class="text-primary size-4 shrink-0" aria-hidden="true" />
            </span>
          {/if}
        </div>
        {#if doc.canToggleSynced}
          <ToggleGroup.Root
            type="single"
            size="sm"
            variant="segmented"
            value={showSynced ? 'synced' : 'plain'}
            onValueChange={(v) => {
              if (v) doc.setShowSynced(v === 'synced');
            }}
          >
            <ToggleGroup.Item
              value="synced"
              aria-label="Synced"
              class="relative gap-1 after:absolute after:inset-x-0 after:-inset-y-2"
            >
              <Timer class="size-3" />
              Synced
            </ToggleGroup.Item>
            <ToggleGroup.Item
              value="plain"
              aria-label="Plain"
              class="relative gap-1 after:absolute after:inset-x-0 after:-inset-y-2"
            >
              <AlignLeft class="size-3" />
              Plain
            </ToggleGroup.Item>
          </ToggleGroup.Root>
        {:else if loadedSynced && !loadedPlain}
          <Badge variant="secondary" class="text-muted-foreground">
            <Timer />
            Synced LRC
          </Badge>
        {:else if !loadedSynced && loadedPlain}
          <Badge variant="secondary" class="text-muted-foreground">
            <AlignLeft />
            Plain text
          </Badge>
        {/if}
      </div>
    {/if}

    <!--
      The theater view (Now Playing, share page) has no header row to hang the badge on, so the AI
      disclosure gets its own quiet strip above the lyrics. It must appear here too: this is the
      view people actually read lyrics in.
    -->
    {#if theater && doc.provenance && doc.provenance !== 'Human'}
      <div class="flex justify-center px-1 sm:px-6">
        <AiLyricsBadge provenance={doc.provenance} variant="theater" />
      </div>
    {/if}

    <!--
      Unsynced lyrics are not a dead end: LRCLIB is community-contributed, so someone may have
      added an LRC for this track since it was enriched. Offered in both variants — the theater
      view is where a missing LRC is actually noticed.
    -->
    {#if !doc.hasSynced}
      <div class={cn('flex flex-wrap items-center gap-2', theater && 'justify-center px-1 sm:px-6')}>
        {@render recheckButton('Look for synced lyrics')}
      </div>
    {/if}

    <div class="relative flex min-h-0 flex-1 flex-col">
      <!-- These listeners only observe scroll intent to disengage follow mode; the
           region itself stays non-interactive. -->
      <!-- svelte-ignore a11y_no_noninteractive_element_interactions -->
      <div
        bind:this={containerEl}
        role="region"
        aria-label="Lyrics"
        class={cn(
          'no-scrollbar min-h-0 flex-1 overflow-y-auto overscroll-contain pb-[calc(1rem_+_var(--mh-content-pad))]',
          theater ? 'px-1 sm:px-6' : 'bg-muted rounded-xl p-4'
        )}
        onwheel={disengageFollow}
        ontouchmove={disengageFollow}
        onkeydown={(e: KeyboardEvent) => {
          // defaultPrevented = a lyric line already consumed the key to seek.
          if (!e.defaultPrevented && SCROLL_KEYS.has(e.key)) disengageFollow();
        }}
      >
        {#if hasParsedLines && parsedLines}
          <div class={cn('font-sans', !theater && 'text-body md:text-sm md:leading-relaxed')}>
            {#each parsedLines as line, i (i)}
              {@const isActive = isTracking && i === activeLineIndex}
              {@const isPast = isTracking && i < activeLineIndex}
              <!-- Before the first timestamp (activeLineIndex = -1) every line is still to come, so
                   the whole document dims rather than sitting at full weight and reading as unsynced. -->
              {@const isFuture = isTracking && i > activeLineIndex}
              <!-- svelte-ignore a11y_no_noninteractive_tabindex -->
              <div
                data-lyric-line={i}
                class={cn(
                  '-mx-1 flex gap-2 rounded-md px-1 transition-colors duration-300',
                  theater ? 'text-title-1 py-1.5 font-bold sm:py-2' : 'py-0.5',
                  // panel highlight
                  !theater && isActive && 'bg-primary/10 text-primary font-semibold',
                  !theater && isPast && 'text-muted-foreground',
                  // Future lines are still real, readable lyrics (someone reads ahead), so the
                  // panel uses the tertiary token rather than an opacity.
                  !theater && isFuture && 'text-muted-foreground-dim',
                  !theater && !isTracking && 'text-foreground',
                  // Theater karaoke: the active line bright, the rest at 55% — the deliberate dim
                  // (inside Now Playing's media appearance that is its sanctioned white/55, ≥ 4.1:1
                  // on the dimmed cover; on the share page 3.97:1 light, both over the 3:1 large-bold
                  // floor). Static lyrics (no tracking) read at full contrast.
                  theater && isActive && 'text-foreground',
                  theater && (isPast || isFuture) && 'text-foreground/55',
                  theater && !isTracking && 'text-foreground',
                  onSeek &&
                    (theater
                      ? 'cursor-pointer pointer-fine:hover:text-foreground/80'
                      : 'hover:bg-primary/5 cursor-pointer')
                )}
                role={onSeek ? 'button' : undefined}
                tabindex={onSeek ? 0 : undefined}
                aria-label={onSeek && !line.text
                  ? `Instrumental, ${formatLrcTime(line.timeMs)}`
                  : undefined}
                onclick={onSeek ? () => seekToLine(line.timeMs) : undefined}
                onkeydown={onSeek
                  ? (e: KeyboardEvent) => {
                      if (e.key === 'Enter' || e.key === ' ') {
                        e.preventDefault();
                        seekToLine(line.timeMs);
                      }
                    }
                  : undefined}
              >
                {#if !theater}
                  <span
                    class={cn(
                      'text-footnote w-12 shrink-0 pt-0.5 tabular-nums md:text-xs',
                      isActive ? 'text-primary' : 'text-muted-foreground-dim'
                    )}
                  >
                    {formatLrcTime(line.timeMs)}
                  </span>
                {/if}
                <span class="flex min-w-0 flex-1 flex-col">
                  <!-- A blank line is an instrumental gap: it keeps its height (an invisible
                       dot) and stays a seek target, named "Instrumental, 1:23" above rather than
                       read out as "middle dot". -->
                  <span
                    class={cn(!line.text && 'select-none opacity-0')}
                    aria-hidden={!line.text || undefined}
                  >
                    {line.text || '·'}
                  </span>
                  {#if secondaryByIndex?.[i] && secondaryByIndex[i] !== line.text}
                    <!-- Its own colour rather than an opacity, so it never multiplies with the
                         line's dim. In theater: full white under the active line, 80% under the
                         rest. 20px semibold is not "large text", so it needs 4.5:1 — 50% white
                         measured 3.72 on the dimmed cover and 2.86 over the brightest wash; 80%
                         is 6.75 / 4.67. The lines stay apart by size (28 bold over 20). -->
                    <span
                      class={cn(
                        theater ? 'text-title-3 font-semibold' : 'text-footnote md:text-xs',
                        isActive || !isTracking
                          ? theater
                            ? 'text-foreground'
                            : 'text-muted-foreground'
                          : theater
                            ? 'text-foreground/80'
                            : 'text-muted-foreground-dim'
                      )}
                    >
                      {secondaryByIndex[i]}
                    </span>
                  {/if}
                </span>
              </div>
            {/each}
          </div>
        {:else if pairedFallbackLines}
          <div class={cn('font-sans', !theater && 'text-body md:text-sm md:leading-relaxed')}>
            {#each pairedFallbackLines as pair, i (i)}
              <div class={cn('flex flex-col', theater ? 'py-1.5' : 'py-0.5')}>
                <span
                  class={cn(
                    theater && 'text-title-1 text-foreground font-bold',
                    !pair.text && 'select-none'
                  )}>{pair.text || ' '}</span>
                {#if pair.secondary && pair.secondary !== pair.text}
                  <span
                    class={cn(
                      theater
                        ? 'text-title-3 text-foreground/80 font-semibold'
                        : 'text-muted-foreground text-footnote md:text-xs'
                    )}
                  >
                    {pair.secondary}
                  </span>
                {/if}
              </div>
            {/each}
          </div>
        {:else}
          <pre
            class={cn(
              'font-sans whitespace-pre-wrap',
              theater ? 'text-title-1 text-foreground font-bold' : 'text-body md:text-sm md:leading-relaxed'
            )}>{fallbackText}</pre>
        {/if}
      </div>

      {#if showSyncPill}
        <!-- Only as wide as the pill, so it never swallows clicks on the lyrics behind it. -->
        <div
          class="absolute bottom-3 left-1/2 -translate-x-1/2"
          transition:fade={{ duration: 120 }}
        >
          <button
            type="button"
            onclick={resumeFollow}
            class={cn(
              'bg-foreground text-background inline-flex items-center gap-1.5 rounded-full',
              'font-semibold shadow-lg transition-transform duration-150',
              'active:scale-[0.97] pointer-fine:hover:scale-[1.03]',
              theater ? 'text-subheadline h-11 px-4' : 'h-8 px-3 text-xs pointer-coarse:h-11 pointer-coarse:px-4'
            )}
          >
            <AudioLines class={theater ? 'size-4' : 'size-3.5'} />
            Sync
          </button>
        </div>
      {/if}
    </div>

    {#if lrclibUrl && !theater}
      <!-- The link's after: strip makes it a 44pt target on touch without growing the line. -->
      <div class="text-footnote text-muted-foreground flex items-center gap-1.5 md:text-xs">
        <span>Source:</span>
        <a
          href={lrclibUrl}
          target="_blank"
          rel="noopener noreferrer"
          class="text-primary relative inline-flex items-center gap-1 after:absolute after:-inset-x-2 after:-inset-y-3 pointer-fine:hover:underline"
        >
          LRCLIB
          <ExternalLink class="size-3" />
        </a>
      </div>
    {/if}
  </div>
{/if}

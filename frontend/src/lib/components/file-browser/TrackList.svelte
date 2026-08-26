<script lang="ts">
  import { ArrowDown, ArrowUp, Clock, Heart, ListMusic, Pause, Play } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { formatDuration, formatFamily, formatFileSize } from '$lib/formatters';
  import {
    albumKeyForSong,
    coverUrlForSong,
    isSpotifySourced,
    mapEnrichmentStatus,
    songOriginLabel,
    toPlayerSong,
    type ApiSong
  } from '$lib/api-client';
  import {
    artistOf,
    hasLyrics,
    matchValue,
    titleOf,
    type SortKey,
    type TrackListView
  } from '$lib/track-list-view.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { cn } from '$lib/utils';
  import SharedByBadge from '$lib/components/v2/SharedByBadge.svelte';

  type Props = {
    /**
     * Filter/sort state, owned by the page so its toolbar can render the chips
     * and the "X of Y" summary while this component renders the rows and the
     * sortable column headers. See $lib/track-list-view.svelte.ts.
     */
    view: TrackListView;
    isLoading: boolean;
    selectedId?: number | null;
    onSelect: (song: ApiSong) => void;
  };
  const { view, isLoading, selectedId = null, onSelect }: Props = $props();

  function artistHref(s: ApiSong): string {
    return `/library?artist=${encodeURIComponent(artistOf(s))}`;
  }
  function albumHref(s: ApiSong): string {
    return `/library?album=${encodeURIComponent(albumKeyForSong(s))}`;
  }

  const sorted = $derived(view.sorted);

  function playFrom(target: ApiSong) {
    const list = sorted;
    const queue = list.map((s) => toPlayerSong(s, artistOf(s)));
    const index = list.findIndex((s) => s.id === target.id);
    void playerStore.playSong(toPlayerSong(target, artistOf(target)), queue, index);
  }

  async function toggleLike(song: ApiSong) {
    try {
      await songsStore.toggleLike(song.id);
    } catch (err) {
      toast.error('Could not update liked songs', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }

  // ── Virtualization ────────────────────────────────────────────────────────
  // One DOM row per track is too heavy for large libraries (each row mounts a
  // Cover). Render only the rows in (or near) the viewport, absolutely
  // positioned inside a full-height spacer so the scrollbar still reflects the
  // whole list.
  const ROW_H = 56;
  const OVERSCAN = 8;
  let scrollEl = $state<HTMLDivElement>();
  let scrollTop = $state(0);
  let viewportH = $state(600);

  const startIndex = $derived(Math.max(0, Math.floor(scrollTop / ROW_H) - OVERSCAN));
  const endIndex = $derived(
    Math.min(sorted.length, Math.ceil((scrollTop + viewportH) / ROW_H) + OVERSCAN)
  );
  const visible = $derived(sorted.slice(startIndex, endIndex));

  function onScroll() {
    if (scrollEl) scrollTop = scrollEl.scrollTop;
  }

  $effect(() => {
    const el = scrollEl;
    if (!el) return;
    viewportH = el.clientHeight;
    // Coalesce resize ticks into a single rAF read so dragging the window edge
    // (or a resizable pane) doesn't force a synchronous reflow per event.
    let frame = 0;
    const ro = new ResizeObserver(() => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        viewportH = el.clientHeight;
      });
    });
    ro.observe(el);
    return () => {
      if (frame) cancelAnimationFrame(frame);
      ro.disconnect();
    };
  });

  // Jump back to the top whenever the visible set changes shape, so the user
  // isn't left scrolled past the end of a now-shorter list. Deliberately watches
  // the individual filter/sort keys and NOT `view.sorted` — that array
  // re-derives on every live songsStore refresh, which would yank the user back
  // to row 0 mid-browse.
  $effect(() => {
    // referenced for reactivity
    void view.searchQuery;
    void view.chips.join(',');
    void view.sortKey;
    void view.sortDir;
    if (scrollEl) scrollEl.scrollTop = 0;
    scrollTop = 0;
  });
</script>

{#snippet sortHead(k: SortKey, label: string)}
  <button
    type="button"
    onclick={() => view.toggleSort(k)}
    class={cn(
      'flex items-center gap-1 text-[11px] font-medium transition-colors',
      view.sortKey === k ? 'text-primary' : 'text-muted-foreground hover:text-foreground'
    )}
  >
    <span class="truncate">{label}</span>
    {#if view.sortKey === k}
      {#if view.sortDir === 'asc'}<ArrowUp class="size-3 shrink-0" />{:else}<ArrowDown class="size-3 shrink-0" />{/if}
    {/if}
  </button>
{/snippet}

<!--
  @container: column visibility is driven by the table's own width, not the
  viewport — the nav sidebar and the global song-detail panel can shrink this
  area well below viewport breakpoints. Tiers: @xl adds artist+format, @3xl
  adds album+year, @5xl adds size+match(+bitrate). Header and row grid
  templates below must stay identical per tier.
-->
<div class="@container flex min-h-0 flex-1 flex-col overflow-hidden">
  <!-- Column headers (sticky, outside the scroll area so columns stay aligned) -->
  <div
    class={cn(
      'border-border text-muted-foreground grid shrink-0 items-center gap-3 border-b px-5 py-2.5',
      'grid-cols-[40px_40px_minmax(0,1fr)_28px_52px]',
      '@xl:grid-cols-[40px_40px_minmax(0,1.5fr)_minmax(0,1fr)_56px_28px_52px]',
      '@3xl:grid-cols-[44px_44px_minmax(0,1.6fr)_minmax(0,1fr)_minmax(0,1fr)_44px_56px_84px_28px_52px]',
      '@5xl:grid-cols-[44px_44px_minmax(0,1.6fr)_minmax(0,1fr)_minmax(0,0.9fr)_52px_104px_72px_128px_84px_28px_52px]'
    )}
  >
    <span class="text-right text-[11px] font-medium">#</span>
    <span></span>
    {@render sortHead('title', 'Title')}
    <span class="hidden @xl:block">{@render sortHead('artist', 'Artist')}</span>
    <span class="hidden @3xl:block">{@render sortHead('album', 'Album')}</span>
    <span class="hidden @3xl:block">{@render sortHead('year', 'Year')}</span>
    <span class="text-muted-foreground hidden text-[11px] font-medium @xl:block">Format</span>
    <span class="hidden @5xl:block">{@render sortHead('size', 'Size')}</span>
    <span class="hidden @5xl:block">{@render sortHead('match', 'Match')}</span>
    <span class="text-muted-foreground hidden text-[11px] font-medium @3xl:block">Source</span>
    <span></span>
    <button
      type="button"
      onclick={() => view.toggleSort('dur')}
      class={cn(
        'flex items-center justify-end gap-1 transition-colors',
        view.sortKey === 'dur' ? 'text-primary' : 'text-muted-foreground hover:text-foreground'
      )}
      aria-label="Sort by duration"
    >
      {#if view.sortKey === 'dur'}
        {#if view.sortDir === 'asc'}<ArrowUp class="size-3" />{:else}<ArrowDown class="size-3" />{/if}
      {/if}
      <Clock class="size-3" />
    </button>
  </div>

  {#if isLoading && view.songs.length === 0}
    <div class="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
      Loading tracks…
    </div>
  {:else if sorted.length === 0}
    <div class="text-muted-foreground flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
      <ListMusic class="size-10 opacity-40" />
      <p class="text-sm">No tracks match</p>
      <p class="text-xs">Try clearing filters or a different search.</p>
    </div>
  {:else}
    <!-- Virtualized scroll viewport -->
    <div
      bind:this={scrollEl}
      onscroll={onScroll}
      class="min-h-0 flex-1 overflow-y-auto px-2 pb-[var(--mh-content-pad)] sm:px-3"
    >
      <div class="relative" style="height: {sorted.length * ROW_H}px;">
        {#each visible as song, vi (song.id)}
          {@const i = startIndex + vi}
          {@const family = formatFamily(song.extension)}
          {@const mv = matchValue(song)}
          {@const isLoaded = playerStore.currentSong?.id === song.id}
          {@const isCurrentlyPlaying = isLoaded && playerStore.isPlaying}
          {@const isSelected = selectedId === song.id}
          {@const isLiked = Boolean(song.likedAtUtc)}
          {@const origin = songOriginLabel(song)}
          {@const isFromSpotify = isSpotifySourced(song)}
          <div
            role="button"
            tabindex="0"
            onclick={() => onSelect(song)}
            onkeydown={(e) => (e.key === 'Enter' || e.key === ' ') && onSelect(song)}
            class={cn(
              'group border-border/40 absolute right-0 left-0 grid cursor-pointer items-center gap-3 border-b px-3',
              'grid-cols-[40px_40px_minmax(0,1fr)_28px_52px]',
              '@xl:grid-cols-[40px_40px_minmax(0,1.5fr)_minmax(0,1fr)_56px_28px_52px]',
              '@3xl:grid-cols-[44px_44px_minmax(0,1.6fr)_minmax(0,1fr)_minmax(0,1fr)_44px_56px_84px_28px_52px]',
              '@5xl:grid-cols-[44px_44px_minmax(0,1.6fr)_minmax(0,1fr)_minmax(0,0.9fr)_52px_104px_72px_128px_84px_28px_52px]',
              'hover:bg-accent/50 active:bg-accent/70',
              isSelected && 'bg-primary/10',
              isLoaded && 'text-primary'
            )}
            style="top: {i * ROW_H}px; height: {ROW_H}px;"
          >
            <!-- # / play -->
            <span class="text-muted-foreground relative grid h-full place-items-center text-right">
              <!-- Button first so the index/equalizer can hide off its `peer` focus state:
                   exactly one of the two is ever visible. The index/equalizer must stay
                   `pointer-events-none`: once hover fades it to `opacity-0` it becomes a
                   stacking context and paints *over* the absolutely positioned button, so
                   it would otherwise swallow the click — the press and release then resolve
                   to different nodes and the browser retargets `click` to this wrapper,
                   which reads as a row click and opens the detail panel instead of playing. -->
              <button
                type="button"
                onclick={(e) => {
                  e.stopPropagation();
                  playFrom(song);
                }}
                aria-label={isCurrentlyPlaying ? 'Pause track' : 'Play track'}
                class="peer text-primary absolute inset-0 grid place-items-center opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100"
              >
                {#if isCurrentlyPlaying}
                  <Pause class="size-3.5" fill="currentColor" />
                {:else}
                  <Play class="size-3.5" fill="currentColor" />
                {/if}
              </button>
              {#if isLoaded}
                <span
                  class={cn(
                    'mh-eq text-primary pointer-events-none group-hover:opacity-0 peer-focus-visible:opacity-0',
                    isCurrentlyPlaying && 'is-playing'
                  )}
                  aria-hidden="true"
                >
                  <i></i><i></i><i></i>
                </span>
              {:else}
                <span
                  class="pointer-events-none font-mono text-[11px] tabular-nums transition-opacity group-hover:opacity-0 peer-focus-visible:opacity-0"
                >
                  {String(i + 1).padStart(3, '0')}
                </span>
              {/if}
            </span>

            <!-- cover -->
            <Cover
              artist={artistOf(song)}
              title={song.album ?? titleOf(song)}
              coverUrl={coverUrlForSong(song)}
              size={36}
              corner={4}
              caption={false}
            />

            <!-- title + sub -->
            <div class="min-w-0">
              <div class={cn('truncate text-[13px] font-medium', isLoaded && 'text-primary')}>
                {titleOf(song)}
              </div>
              <div class="text-muted-foreground mt-0.5 flex items-center gap-2 text-[11px]">
                {#if mapEnrichmentStatus(song.enrichmentStatus) === 'needsreview'}
                  <span
                    title="Enrichment uncertain — needs review"
                    class="rounded bg-amber-500/15 px-1 py-0.5 font-mono text-[9px] font-semibold tracking-wider text-amber-600 dark:text-amber-500"
                  >
                    REVIEW
                  </span>
                {/if}
                {#if hasLyrics(song)}
                  <span class="bg-muted text-muted-foreground rounded px-1 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
                    LRC
                  </span>
                {/if}
                <!-- artist inline when narrow (its own column is hidden there) -->
                <a
                  href={artistHref(song)}
                  onclick={(e) => e.stopPropagation()}
                  class="truncate hover:underline @xl:hidden"
                >
                  {artistOf(song)}
                </a>
                <!-- Renders nothing for a track this account owns. -->
                <SharedByBadge {song} variant="icon" />
              </div>
            </div>

            <!-- artist (clickable) -->
            <a
              href={artistHref(song)}
              onclick={(e) => e.stopPropagation()}
              class="text-muted-foreground hover:text-foreground hidden truncate text-[12px] hover:underline @xl:block"
            >
              {artistOf(song)}
            </a>
            <!-- album (clickable) -->
            {#if song.album}
              <a
                href={albumHref(song)}
                onclick={(e) => e.stopPropagation()}
                class="text-muted-foreground hover:text-foreground hidden truncate text-[12px] hover:underline @3xl:block"
              >
                {song.album}
              </a>
            {:else}
              <span class="text-muted-foreground hidden truncate text-[12px] @3xl:block">—</span>
            {/if}
            <!-- year -->
            <span class="text-muted-foreground hidden font-mono text-[11px] @3xl:block">
              {song.year ?? '—'}
            </span>
            <!-- format -->
            <span class="text-muted-foreground hidden items-center gap-1.5 font-mono text-[10px] @xl:flex">
              {#if family === 'OTHER'}
                <span>{(song.extension ?? '').replace(/^\./, '').toUpperCase() || '—'}</span>
              {:else}
                <span class="text-foreground/70 font-medium">{family}</span>
              {/if}
              {#if song.bitRate && song.bitRate > 0}
                <span class="text-muted-foreground hidden font-mono text-[9.5px] @5xl:inline">{song.bitRate}kbps</span>
              {/if}
            </span>
            <!-- size -->
            <span class="text-muted-foreground hidden font-mono text-[11px] @5xl:block">
              {formatFileSize(song.fileSizeBytes)}
            </span>
            <!-- match -->
            <span class="hidden items-center gap-2 @5xl:flex">
              {#if mv != null}
                <span class="bg-foreground/10 h-1 flex-1 overflow-hidden rounded-full">
                  <span class="bg-foreground/35 block h-full rounded-full" style="width: {mv * 100}%;"></span>
                </span>
                <span class="text-muted-foreground min-w-[28px] text-right font-mono text-[10.5px]">{mv.toFixed(2)}</span>
              {:else}
                <span
                  class="text-muted-foreground flex-1 text-right font-mono text-[10.5px]"
                  title="No match confidence recorded for this track"
                >
                  —
                </span>
              {/if}
            </span>
            <!-- source (where this track came from) -->
            <span class="hidden min-w-0 @3xl:block">
              {#if origin}
                <span
                  title={origin.title}
                  class={cn(
                    'inline-block max-w-full truncate rounded px-1.5 py-0.5 text-[10px] font-medium',
                    isFromSpotify
                      ? 'bg-emerald-500/12 text-emerald-700 dark:text-emerald-400'
                      : 'bg-muted text-muted-foreground'
                  )}
                >
                  {origin.label}
                </span>
              {:else}
                <span class="text-muted-foreground text-[11px]">—</span>
              {/if}
            </span>
            <!-- like -->
            <button
              type="button"
              onclick={(e) => {
                e.stopPropagation();
                void toggleLike(song);
              }}
              aria-label={isLiked ? 'Remove from liked songs' : 'Add to liked songs'}
              aria-pressed={isLiked}
              class={cn(
                'grid place-items-center transition-all active:scale-90',
                isLiked
                  ? 'text-primary'
                  : 'text-muted-foreground opacity-0 group-hover:opacity-100 focus-visible:opacity-100 hover:text-foreground'
              )}
            >
              <Heart class="size-3.5" fill={isLiked ? 'currentColor' : 'none'} />
            </button>
            <!-- duration -->
            <span class="text-muted-foreground text-right font-mono text-[11px]">
              {formatDuration(song.durationSeconds)}
            </span>
          </div>
        {/each}
      </div>
    </div>
  {/if}
</div>

<style>
  /* Apple-style now-playing equalizer: three bars, animated only while playing. */
  .mh-eq {
    display: inline-flex;
    align-items: flex-end;
    justify-content: center;
    gap: 2px;
    height: 13px;
    transition: opacity 150ms;
  }
  .mh-eq > :global(i) {
    width: 2.5px;
    height: 35%;
    border-radius: 1px;
    background: currentColor;
  }
  .mh-eq.is-playing > :global(i) {
    animation: mh-eq 0.9s ease-in-out infinite;
  }
  .mh-eq > :global(i:nth-child(1)) {
    animation-delay: -0.5s;
  }
  .mh-eq > :global(i:nth-child(2)) {
    animation-delay: -0.2s;
  }
  .mh-eq > :global(i:nth-child(3)) {
    animation-delay: -0.7s;
  }
  @keyframes mh-eq {
    0%,
    100% {
      height: 30%;
    }
    50% {
      height: 100%;
    }
  }
</style>

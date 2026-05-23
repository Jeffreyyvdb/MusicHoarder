<script lang="ts">
  import { ArrowDown, ArrowUp, Clock, FileText, ListMusic, Pause, Play, Shuffle } from '@lucide/svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import {
    formatDuration,
    formatFileSize,
    formatTotalDuration,
    formatFamily,
    type FormatFamily
  } from '$lib/formatters';
  import { albumKeyForSong, songAddedTime, toPlayerSong, type ApiSong } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn, shuffle } from '$lib/utils';

  type Props = {
    songs: ApiSong[];
    searchQuery: string;
    isLoading: boolean;
    selectedId?: number | null;
    onSelect: (song: ApiSong) => void;
  };
  const { songs, searchQuery, isLoading, selectedId = null, onSelect }: Props = $props();

  type SortKey = 'added' | 'title' | 'artist' | 'album' | 'year' | 'size' | 'match' | 'dur';
  const STRING_KEYS: SortKey[] = ['title', 'artist', 'album'];

  let sortKey = $state<SortKey>('added');
  let sortDir = $state<'asc' | 'desc'>('desc');
  let fmt = $state<'all' | FormatFamily>('all');
  let lyricsOnly = $state(false);

  const UNKNOWN_ARTIST = 'Unknown Artist';

  // Album-artist-preferred, matching buildArtistGroups / the `?artist=` browse filter.
  function artistOf(s: ApiSong): string {
    return (s.albumArtist ?? s.artist ?? '').trim() || UNKNOWN_ARTIST;
  }
  function titleOf(s: ApiSong): string {
    return (s.title ?? s.fileName).trim() || s.fileName;
  }
  function hasLyrics(s: ApiSong): boolean {
    return Boolean(s.hasSyncedLyrics || s.hasPlainLyrics || s.lrclibId);
  }
  // Mirror AlbumPage's deterministic synthetic confidence for songs lacking a stored value.
  function matchValue(s: ApiSong): number {
    if (typeof s.matchConfidence === 'number') return Math.max(0, Math.min(1, s.matchConfidence));
    return 0.74 + ((s.id * 17) % 22) / 100;
  }
  function artistHref(s: ApiSong): string {
    return `/library?artist=${encodeURIComponent(artistOf(s))}`;
  }
  function albumHref(s: ApiSong): string {
    return `/library?album=${encodeURIComponent(albumKeyForSong(s))}`;
  }

  // Per-family hue (oklch) for the chip dot + row format pill. OTHER stays neutral.
  const FAMILY_HUE: Record<Exclude<FormatFamily, 'OTHER'>, number> = {
    FLAC: 150,
    MP3: 60,
    AAC: 280,
    WAV: 20,
    OGG: 200
  };
  function familyDot(f: 'all' | FormatFamily): string {
    if (f === 'all') return 'background: linear-gradient(135deg, oklch(0.65 0.12 230), oklch(0.65 0.12 30));';
    if (f === 'OTHER') return 'background: var(--muted-foreground);';
    return `background: oklch(0.62 0.14 ${FAMILY_HUE[f]});`;
  }
  function familyPill(f: FormatFamily): string {
    if (f === 'OTHER') return '';
    const h = FAMILY_HUE[f];
    return `color: oklch(0.62 0.15 ${h}); background: oklch(0.62 0.15 ${h} / 0.16);`;
  }

  const filtered = $derived.by(() => {
    let r = songs;
    const q = searchQuery.trim().toLowerCase();
    if (q) {
      r = r.filter(
        (s) =>
          titleOf(s).toLowerCase().includes(q) ||
          artistOf(s).toLowerCase().includes(q) ||
          (s.album ?? '').toLowerCase().includes(q)
      );
    }
    if (fmt !== 'all') r = r.filter((s) => formatFamily(s.extension) === fmt);
    if (lyricsOnly) r = r.filter(hasLyrics);
    return r;
  });

  const sorted = $derived.by(() => {
    const r = [...filtered];
    const pick = (s: ApiSong): string | number => {
      switch (sortKey) {
        case 'title':
          return titleOf(s).toLowerCase();
        case 'artist':
          return artistOf(s).toLowerCase();
        case 'album':
          return (s.album ?? '').toLowerCase();
        case 'year':
          return s.year ?? 0;
        case 'size':
          return s.fileSizeBytes ?? 0;
        case 'dur':
          return s.durationSeconds ?? 0;
        case 'match':
          return matchValue(s);
        case 'added':
        default:
          return songAddedTime(s);
      }
    };
    r.sort((a, b) => {
      const av = pick(a);
      const bv = pick(b);
      if (typeof av === 'string' && typeof bv === 'string') {
        const c = av.localeCompare(bv);
        return sortDir === 'asc' ? c : -c;
      }
      return sortDir === 'asc'
        ? (av as number) - (bv as number)
        : (bv as number) - (av as number);
    });
    return r;
  });

  function toggleSort(k: SortKey) {
    if (sortKey === k) {
      sortDir = sortDir === 'asc' ? 'desc' : 'asc';
    } else {
      sortKey = k;
      sortDir = STRING_KEYS.includes(k) ? 'asc' : 'desc';
    }
  }

  // Format chips: "All formats" + one per distinct family present, in a stable order.
  const FAMILY_ORDER: FormatFamily[] = ['FLAC', 'MP3', 'AAC', 'WAV', 'OGG', 'OTHER'];
  const familyCounts = $derived.by(() => {
    const m = new Map<FormatFamily, number>();
    for (const s of songs) {
      const f = formatFamily(s.extension);
      m.set(f, (m.get(f) ?? 0) + 1);
    }
    return m;
  });
  const formatChips = $derived.by(() => {
    const chips: { id: 'all' | FormatFamily; label: string; count: number }[] = [
      { id: 'all', label: 'All formats', count: songs.length }
    ];
    for (const f of FAMILY_ORDER) {
      const count = familyCounts.get(f);
      if (count) chips.push({ id: f, label: f === 'OTHER' ? 'Other' : f, count });
    }
    return chips;
  });

  const stats = $derived.by(() => ({
    count: filtered.length,
    totalSec: filtered.reduce((n, s) => n + (s.durationSeconds ?? 0), 0),
    totalBytes: filtered.reduce((n, s) => n + (s.fileSizeBytes ?? 0), 0)
  }));

  const albumCount = $derived(new Set(songs.map((s) => albumKeyForSong(s))).size);
  const hasFilters = $derived(fmt !== 'all' || lyricsOnly);

  function playFrom(target: ApiSong) {
    const list = sorted;
    const queue = list.map((s) => toPlayerSong(s, artistOf(s)));
    const index = list.findIndex((s) => s.id === target.id);
    void playerStore.playSong(toPlayerSong(target, artistOf(target)), queue, index);
  }

  function shuffleTracks() {
    if (sorted.length === 0) return;
    const queue = shuffle(sorted).map((s) => toPlayerSong(s, artistOf(s)));
    void playerStore.playSong(queue[0], queue, 0);
  }

  // ── Virtualization ────────────────────────────────────────────────────────
  // One DOM row per track is too heavy for large libraries (each row mounts a
  // Cover). Render only the rows in (or near) the viewport, absolutely
  // positioned inside a full-height spacer so the scrollbar still reflects the
  // whole list.
  const ROW_H = 52;
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
  // isn't left scrolled past the end of a now-shorter list.
  $effect(() => {
    // referenced for reactivity
    void searchQuery;
    void fmt;
    void lyricsOnly;
    void sortKey;
    void sortDir;
    if (scrollEl) scrollEl.scrollTop = 0;
    scrollTop = 0;
  });
</script>

{#snippet sortHead(k: SortKey, label: string)}
  <button
    type="button"
    onclick={() => toggleSort(k)}
    class={cn(
      'flex items-center gap-1 text-[10px] font-semibold tracking-wider uppercase transition-colors',
      sortKey === k ? 'text-primary' : 'text-muted-foreground hover:text-foreground'
    )}
  >
    <span class="truncate">{label}</span>
    {#if sortKey === k}
      {#if sortDir === 'asc'}<ArrowUp class="size-3 shrink-0" />{:else}<ArrowDown class="size-3 shrink-0" />{/if}
    {/if}
  </button>
{/snippet}

<div class="flex min-h-0 flex-1 flex-col overflow-hidden">
  <!-- Header band -->
  <div class="border-border bg-card/30 flex items-start justify-between gap-4 border-b px-4 py-5 md:px-6">
    <div class="min-w-0">
      <h1 class="text-2xl font-bold tracking-[-0.02em]">All Tracks</h1>
      <p class="text-muted-foreground mt-1 text-sm">
        {stats.count.toLocaleString()} track{stats.count === 1 ? '' : 's'}
        {#if searchQuery.trim()}
          · matching <span class="font-mono">"{searchQuery.trim()}"</span>
        {:else}
          · across {albumCount.toLocaleString()} album{albumCount === 1 ? '' : 's'}
        {/if}
        · <span class="font-mono">{formatTotalDuration(stats.totalSec)}</span>
        · <span class="font-mono">{formatFileSize(stats.totalBytes)}</span>
      </p>
    </div>
    <div class="mt-1 flex shrink-0 items-center gap-2">
      {#if hasFilters}
        <button
          type="button"
          onclick={() => {
            fmt = 'all';
            lyricsOnly = false;
          }}
          class="text-muted-foreground hover:border-primary hover:text-primary rounded-md border px-2 py-1 text-[11px] transition-colors"
        >
          Clear filters
        </button>
      {/if}
      {#if sorted.length > 0}
        <button
          type="button"
          onclick={shuffleTracks}
          class="border-primary/40 text-primary hover:bg-primary/10 inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-[11px] font-medium transition-colors"
        >
          <Shuffle class="size-3.5" />
          Shuffle
        </button>
      {/if}
    </div>
  </div>

  <!-- Filter chips -->
  <div class="border-border flex flex-wrap items-center gap-2 border-b px-4 py-3 md:px-6">
    {#each formatChips as chip (chip.id)}
      <button
        type="button"
        onclick={() => (fmt = chip.id)}
        class={cn(
          'flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] transition-colors',
          fmt === chip.id
            ? 'border-primary bg-primary/10 text-primary'
            : 'border-border text-muted-foreground hover:text-foreground'
        )}
      >
        <span class="inline-block size-2 rounded-full" style={familyDot(chip.id)}></span>
        {chip.label}
        <span class="font-mono opacity-70">{chip.count.toLocaleString()}</span>
      </button>
    {/each}

    <span class="bg-border mx-1 h-4 w-px"></span>

    <button
      type="button"
      onclick={() => (lyricsOnly = !lyricsOnly)}
      class={cn(
        'flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] transition-colors',
        lyricsOnly
          ? 'border-primary bg-primary/10 text-primary'
          : 'border-border text-muted-foreground hover:text-foreground'
      )}
    >
      <FileText class="size-3" />
      With lyrics
    </button>

    <span class="text-muted-foreground ml-auto font-mono text-[10.5px]">
      {sorted.length.toLocaleString()} shown · sorted by {sortKey}
      {sortDir === 'asc' ? '↑' : '↓'}
    </span>
  </div>

  <!-- Column headers (sticky, outside the scroll area so columns stay aligned) -->
  <div
    class={cn(
      'border-border text-muted-foreground grid shrink-0 items-center gap-3 border-b px-5 py-2.5',
      'grid-cols-[40px_40px_minmax(0,1fr)_52px] sm:grid-cols-[44px_44px_minmax(0,1.6fr)_minmax(0,1fr)_minmax(0,0.9fr)_52px_104px_72px_128px_52px]'
    )}
  >
    <span class="text-right text-[10px] font-semibold tracking-wider uppercase">#</span>
    <span></span>
    {@render sortHead('title', 'Title')}
    <span class="hidden sm:block">{@render sortHead('artist', 'Artist')}</span>
    <span class="hidden sm:block">{@render sortHead('album', 'Album')}</span>
    <span class="hidden sm:block">{@render sortHead('year', 'Year')}</span>
    <span class="hidden text-[10px] font-semibold tracking-wider uppercase sm:block">Format</span>
    <span class="hidden sm:block">{@render sortHead('size', 'Size')}</span>
    <span class="hidden sm:block">{@render sortHead('match', 'Match')}</span>
    <button
      type="button"
      onclick={() => toggleSort('dur')}
      class={cn(
        'flex items-center justify-end gap-1 transition-colors',
        sortKey === 'dur' ? 'text-primary' : 'text-muted-foreground hover:text-foreground'
      )}
      aria-label="Sort by duration"
    >
      {#if sortKey === 'dur'}
        {#if sortDir === 'asc'}<ArrowUp class="size-3" />{:else}<ArrowDown class="size-3" />{/if}
      {/if}
      <Clock class="size-3" />
    </button>
  </div>

  {#if isLoading && songs.length === 0}
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
    <div bind:this={scrollEl} onscroll={onScroll} class="min-h-0 flex-1 overflow-y-auto px-2 sm:px-3">
      <div class="relative" style="height: {sorted.length * ROW_H}px;">
        {#each visible as song, vi (song.id)}
          {@const i = startIndex + vi}
          {@const family = formatFamily(song.extension)}
          {@const mv = matchValue(song)}
          {@const isLoaded = playerStore.currentSong?.id === song.id}
          {@const isCurrentlyPlaying = isLoaded && playerStore.isPlaying}
          {@const isSelected = selectedId === song.id}
          <div
            role="button"
            tabindex="0"
            onclick={() => onSelect(song)}
            onkeydown={(e) => (e.key === 'Enter' || e.key === ' ') && onSelect(song)}
            class={cn(
              'group absolute right-0 left-0 grid cursor-pointer items-center gap-3 rounded-md px-3',
              'grid-cols-[40px_40px_minmax(0,1fr)_52px] sm:grid-cols-[44px_44px_minmax(0,1.6fr)_minmax(0,1fr)_minmax(0,0.9fr)_52px_104px_72px_128px_52px]',
              'hover:bg-accent/50',
              isSelected && 'bg-primary/10',
              isLoaded && 'text-primary'
            )}
            style="top: {i * ROW_H}px; height: {ROW_H}px;"
          >
            <!-- # / play -->
            <span class="text-muted-foreground relative grid place-items-center text-right">
              <span
                class={cn(
                  'font-mono text-[11px] tabular-nums transition-opacity group-hover:opacity-0',
                  isCurrentlyPlaying && 'opacity-0'
                )}
              >
                {String(i + 1).padStart(3, '0')}
              </span>
              <button
                type="button"
                onclick={(e) => {
                  e.stopPropagation();
                  playFrom(song);
                }}
                aria-label={isCurrentlyPlaying ? 'Pause track' : 'Play track'}
                class={cn(
                  'text-primary absolute inset-0 grid place-items-center opacity-0 transition-opacity group-hover:opacity-100',
                  isCurrentlyPlaying && 'opacity-100'
                )}
              >
                {#if isCurrentlyPlaying}
                  <Pause class="size-3.5" />
                {:else}
                  <Play class="size-3.5" />
                {/if}
              </button>
            </span>

            <!-- cover -->
            <Cover
              artist={artistOf(song)}
              title={song.album ?? titleOf(song)}
              coverUrl={song.albumArt}
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
                {#if hasLyrics(song)}
                  <span class="bg-primary/15 text-primary rounded px-1 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
                    LRC
                  </span>
                {/if}
                <!-- artist inline on mobile (its own column is hidden there) -->
                <a
                  href={artistHref(song)}
                  onclick={(e) => e.stopPropagation()}
                  class="truncate hover:underline sm:hidden"
                >
                  {artistOf(song)}
                </a>
                {#if song.fingerprint}
                  <span class="hidden truncate font-mono text-[9.5px] opacity-65 sm:inline">
                    {song.fingerprint.slice(0, 12)}…
                  </span>
                {/if}
              </div>
            </div>

            <!-- artist (clickable) -->
            <a
              href={artistHref(song)}
              onclick={(e) => e.stopPropagation()}
              class="text-muted-foreground hover:text-foreground hidden truncate text-[12px] hover:underline sm:block"
            >
              {artistOf(song)}
            </a>
            <!-- album (clickable) -->
            {#if song.album}
              <a
                href={albumHref(song)}
                onclick={(e) => e.stopPropagation()}
                class="text-muted-foreground hover:text-foreground hidden truncate text-[12px] hover:underline sm:block"
              >
                {song.album}
              </a>
            {:else}
              <span class="text-muted-foreground hidden truncate text-[12px] sm:block">—</span>
            {/if}
            <!-- year -->
            <span class="text-muted-foreground hidden font-mono text-[11px] sm:block">
              {song.year ?? '—'}
            </span>
            <!-- format -->
            <span class="hidden items-center gap-1.5 sm:flex">
              {#if family === 'OTHER'}
                <span class="text-muted-foreground font-mono text-[10px]">{(song.extension ?? '').replace(/^\./, '').toUpperCase() || '—'}</span>
              {:else}
                <span class="rounded px-1.5 py-0.5 font-mono text-[10px] font-semibold" style={familyPill(family)}>
                  {family}
                </span>
              {/if}
              {#if song.bitRate && song.bitRate > 0}
                <span class="text-muted-foreground hidden font-mono text-[9.5px] lg:inline">{song.bitRate}kbps</span>
              {/if}
            </span>
            <!-- size -->
            <span class="text-muted-foreground hidden font-mono text-[11px] sm:block">
              {formatFileSize(song.fileSizeBytes)}
            </span>
            <!-- match -->
            <span class="hidden items-center gap-2 sm:flex">
              <span class="bg-border h-1 flex-1 overflow-hidden rounded-full">
                <span class="bg-primary block h-full rounded-full" style="width: {mv * 100}%;"></span>
              </span>
              <span class="text-muted-foreground min-w-[28px] text-right font-mono text-[10.5px]">{mv.toFixed(2)}</span>
            </span>
            <!-- duration -->
            <span class="text-muted-foreground text-right font-mono text-[11px]">
              {formatDuration(song.durationSeconds)}
            </span>
          </div>
        {/each}
      </div>
    </div>

    <!-- Footer totals (outside the scroll area) -->
    <div class="border-border text-muted-foreground flex shrink-0 items-center gap-2 border-t px-5 py-2.5 font-mono text-[11px]">
      <span>Showing {sorted.length.toLocaleString()} of {songs.length.toLocaleString()}</span>
      <span class="flex-1"></span>
      <span>{formatFileSize(stats.totalBytes)}</span>
      <span class="opacity-50">·</span>
      <span>{formatTotalDuration(stats.totalSec)}</span>
    </div>
  {/if}
</div>

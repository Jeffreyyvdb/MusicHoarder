<script lang="ts">
  import type { Snippet } from 'svelte';
  import { ArrowDown, ArrowUp, Clock, Heart, ListMusic, Pause, Play } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { Badge } from '$lib/components/ui/badge';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import SharedByBadge from '$lib/components/v2/SharedByBadge.svelte';
  import TrackRowMenu from '$lib/components/v2/TrackRowMenu.svelte';
  import TrackRowText from '$lib/components/v2/TrackRowText.svelte';
  import { longpress, type LongPressPoint } from '$lib/actions/long-press';
  import { DYNAMIC_TYPE_CHANGE, dynamicTypeScale } from '$lib/dynamic-type';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
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

  type Props = {
    /**
     * Filter/sort state, owned by the page so its nav bar can render the search, the filters and
     * the "X of Y" subtitle while this component renders the rows. See track-list-view.svelte.ts.
     */
    view: TrackListView;
    isLoading: boolean;
    selectedId?: number | null;
    /** A row click on desktop: the page toggles the song-detail overlay. */
    onSelect: (song: ApiSong) => void;
    /**
     * A row tap on a phone (the tap rule, track-list-view.svelte.ts). When set, compact rows call this instead of
     * `onSelect`; the page passes it only below md.
     */
    onActivate?: (song: ApiSong) => void;
    /**
     * Rendered INSIDE the scroller, before the rows: the page's nav bar (PageToolbarV2, which must
     * be the scroller's first child for its sticky bar and large title to work) and anything that
     * scrolls with it. No wrapper element — sticky only sticks within its parent's box.
     */
    header?: Snippet;
    /** Replaces the default "No tracks match" state, e.g. with a filter dead-end explanation. */
    empty?: Snippet;
  };
  const {
    view,
    isLoading,
    selectedId = null,
    onSelect,
    onActivate,
    header,
    empty
  }: Props = $props();

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  function artistHref(s: ApiSong): string {
    return `/library?artist=${encodeURIComponent(artistOf(s))}`;
  }
  function albumHref(s: ApiSong): string {
    return `/library?album=${encodeURIComponent(albumKeyForSong(s))}`;
  }

  const sorted = $derived(view.sorted);

  /** A row menu's Play: the visible list from `target`, never pausing it. */
  function playFrom(target: ApiSong) {
    const list = sorted;
    void playerStore.startQueue(
      list.map((s) => toPlayerSong(s, artistOf(s))),
      list.findIndex((s) => s.id === target.id)
    );
  }

  /** The desktop row's glyph shows Play/Pause for its own song, so it is the one that toggles. */
  function togglePlayFrom(target: ApiSong) {
    const list = sorted;
    const queue = list.map((s) => toPlayerSong(s, artistOf(s)));
    const index = list.findIndex((s) => s.id === target.id);
    void playerStore.playSong(toPlayerSong(target, artistOf(target)), queue, index);
  }

  async function toggleLike(song: ApiSong) {
    try {
      await songsStore.toggleLike(song.id);
    } catch (err) {
      toast.error('Could not update favourites', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }

  // One menu per rendered row; the row's touch-and-hold and right-click open it at the finger or
  // pointer. Only the rows in the virtual window exist, so only their menus do.
  type RowMenu = { openAt: (point: LongPressPoint) => void };
  const menus: Record<number, RowMenu | undefined> = {};
  function openMenu(id: number, point: LongPressPoint) {
    menus[id]?.openAt(point);
  }
  function onRowContextMenu(e: MouseEvent, id: number) {
    e.preventDefault();
    openMenu(id, { x: e.clientX, y: e.clientY });
  }
  /** Desktop rows are role=button; a key press on a control inside them is that control's. */
  function onRowKeydown(e: KeyboardEvent, song: ApiSong) {
    if (e.target !== e.currentTarget) return;
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onSelect(song);
    }
  }

  // ── Virtualization ────────────────────────────────────────────────────────
  // One DOM row per track is too heavy for large libraries (each row mounts a Cover). Render only
  // the rows in (or near) the viewport, absolutely positioned inside a full-height spacer so the
  // scrollbar still reflects the whole list. Every row is one fixed height, which is what makes
  // the window arithmetic exact.
  //
  // The row height holds two lines of text, so on a phone it follows the Text Size setting
  // (Dynamic Type): 64pt at the default size, never less. Desktop keeps its dense 56px.
  let typeScale = $state(dynamicTypeScale());
  $effect(() => {
    const onChange = () => (typeScale = dynamicTypeScale());
    window.addEventListener(DYNAMIC_TYPE_CHANGE, onChange);
    return () => window.removeEventListener(DYNAMIC_TYPE_CHANGE, onChange);
  });
  const rowH = $derived(compact ? Math.max(64, Math.round(64 * typeScale)) : 56);
  const OVERSCAN = 8;

  let scrollEl = $state<HTMLDivElement>();
  let rowsEl = $state<HTMLDivElement>();
  let scrollTop = $state(0);
  let viewportH = $state(600);
  // Everything above the rows inside the scroller — the nav bar, large title, search, tokens,
  // Play/Shuffle and (desktop) the column header. The window is measured from the rows' own top.
  let headerH = $state(0);

  const startIndex = $derived(Math.max(0, Math.floor((scrollTop - headerH) / rowH) - OVERSCAN));
  const endIndex = $derived(
    Math.min(
      sorted.length,
      Math.max(0, Math.ceil((scrollTop - headerH + viewportH) / rowH)) + OVERSCAN
    )
  );
  const visible = $derived(sorted.slice(startIndex, endIndex));

  function onScroll() {
    if (scrollEl) scrollTop = scrollEl.scrollTop;
  }

  // The header is measured as the rows' offset (no wrapper to measure — it would break sticky).
  // Anything above the rows can change height (the filter tokens appear, the search field, a
  // Dynamic Type change), so every direct child of the scroller is observed, and re-observed when
  // the children change. Resize ticks are coalesced into one rAF read.
  $effect(() => {
    const el = scrollEl;
    if (!el) return;
    const measure = () => {
      viewportH = el.clientHeight;
      headerH = rowsEl ? rowsEl.offsetTop : 0;
    };
    let frame = 0;
    const schedule = () => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        measure();
      });
    };
    const ro = new ResizeObserver(schedule);
    const observeAll = () => {
      ro.disconnect();
      ro.observe(el);
      for (const child of el.children) ro.observe(child);
    };
    const mo = new MutationObserver(() => {
      observeAll();
      schedule();
    });
    measure();
    observeAll();
    mo.observe(el, { childList: true });
    return () => {
      if (frame) cancelAnimationFrame(frame);
      ro.disconnect();
      mo.disconnect();
    };
  });

  // Jump back to the top whenever the visible set changes shape, so the user isn't left scrolled
  // past the end of a now-shorter list. Deliberately watches the individual filter/sort keys and
  // NOT `view.sorted` — that array re-derives on every live songsStore refresh, which would yank
  // the user back to row 0 mid-browse. Going to the top also brings the large title back.
  $effect(() => {
    // referenced for reactivity
    void view.searchQuery;
    void view.chips.join(',');
    void view.sortKey;
    void view.sortDir;
    if (scrollEl) scrollEl.scrollTop = 0;
    scrollTop = 0;
  });

  // Desktop table columns. The header and row templates must stay identical per tier; the last
  // column is the row's ⋯ menu.
  const GRID = [
    'grid-cols-[40px_40px_minmax(0,1fr)_28px_52px_32px]',
    '@xl:grid-cols-[40px_40px_minmax(0,1.5fr)_minmax(0,1fr)_56px_28px_52px_32px]',
    '@3xl:grid-cols-[44px_44px_minmax(0,1.6fr)_minmax(0,1fr)_minmax(0,1fr)_44px_56px_84px_28px_52px_32px]',
    '@5xl:grid-cols-[44px_44px_minmax(0,1.6fr)_minmax(0,1fr)_minmax(0,0.9fr)_52px_104px_72px_128px_84px_28px_52px_32px]'
  ].join(' ');
</script>

{#snippet sortHead(k: SortKey, label: string)}
  <button
    type="button"
    onclick={() => view.toggleSort(k)}
    class={cn(
      'relative flex min-h-5 items-center gap-1 text-[11px] font-medium transition-colors',
      // Text-hugging buttons in a short header row: grow the tap height invisibly on touch
      // (an iPad at md+) rather than the visible row.
      "pointer-coarse:after:absolute pointer-coarse:after:inset-x-0 pointer-coarse:after:-inset-y-[14px] pointer-coarse:after:content-['']",
      view.sortKey === k ? 'text-primary' : 'text-muted-foreground hover:text-foreground'
    )}
  >
    <span class="truncate">{label}</span>
    {#if view.sortKey === k}
      {#if view.sortDir === 'asc'}<ArrowUp class="size-3 shrink-0" />{:else}<ArrowDown
          class="size-3 shrink-0"
        />{/if}
    {/if}
  </button>
{/snippet}

{#snippet equalizer(playing: boolean, className: string)}
  <span
    class={cn('mh-eq pointer-events-none', playing && 'is-playing', className)}
    aria-hidden="true"
  >
    <i></i><i></i><i></i>
  </span>
{/snippet}

<!--
  @container: column visibility is driven by the table's own width, not the viewport — the nav
  sidebar can shrink this area well below viewport breakpoints. Tiers: @xl adds artist+format,
  @3xl adds album+year+source, @5xl adds size+match(+bitrate).
-->
<div class="@container flex min-h-0 flex-1 flex-col overflow-hidden">
  <!-- The one scroller: nav bar, header bands, (desktop) column header, rows. `relative` makes it
       the rows' offsetParent, so their offsetTop is the header height. -->
  <div
    bind:this={scrollEl}
    onscroll={onScroll}
    class="relative min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)"
  >
    {@render header?.()}

    {#if !compact}
      <!-- Column headers: sticky under the bar (--mh-navbar-h is the bar plus its chip band). -->
      <div
        class={cn(
          'border-border text-muted-foreground bg-background sticky top-[var(--mh-navbar-h,0px)] z-10 grid items-center gap-3 border-b px-7 py-2.5',
          GRID
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
            'relative flex min-h-5 items-center justify-end gap-1 transition-colors',
            "pointer-coarse:after:absolute pointer-coarse:after:inset-x-0 pointer-coarse:after:-inset-y-[14px] pointer-coarse:after:content-['']",
            view.sortKey === 'dur' ? 'text-primary' : 'text-muted-foreground hover:text-foreground'
          )}
          aria-label="Sort by duration"
        >
          {#if view.sortKey === 'dur'}
            {#if view.sortDir === 'asc'}<ArrowUp class="size-3" />{:else}<ArrowDown
                class="size-3"
              />{/if}
          {/if}
          <Clock class="size-3" />
        </button>
        <span></span>
      </div>
    {/if}

    {#if isLoading && view.songs.length === 0}
      <div
        class="text-muted-foreground text-subheadline flex items-center justify-center p-8 md:text-sm"
      >
        Loading tracks…
      </div>
    {:else if sorted.length === 0}
      {#if empty}
        {@render empty()}
      {:else}
        <EmptyState
          icon={ListMusic}
          title="No tracks match"
          action={view.hasFilters
            ? { label: 'Clear filters', onclick: () => view.clearFilters() }
            : undefined}
        />
      {/if}
    {:else}
      <!-- On a phone the rows are a list of two-control items; on desktop each row is itself a
           button (the table's row selection). -->
      <div
        bind:this={rowsEl}
        role={compact ? 'list' : undefined}
        aria-label={compact ? 'Tracks' : undefined}
        class="relative"
        style="height: {sorted.length * rowH}px;"
      >
        {#each visible as song, vi (song.id)}
          {@const i = startIndex + vi}
          {@const isLoaded = playerStore.currentSong?.id === song.id}
          {@const isCurrentlyPlaying = isLoaded && playerStore.isPlaying}
          {@const isLiked = Boolean(song.likedAtUtc)}
          {@const needsReview = mapEnrichmentStatus(song.enrichmentStatus) === 'needsreview'}
          {#if compact}
            <!-- Phone row: 48pt art, title over "artist · album", a trailing ⋯. The row is
                 two siblings, never nested controls: the main button (tap rule) and the menu. -->
            <div
              role="listitem"
              aria-setsize={sorted.length}
              aria-posinset={i + 1}
              use:longpress={{ onlongpress: (p) => openMenu(song.id, p) }}
              oncontextmenu={(e) => onRowContextMenu(e, song.id)}
              class={cn(
                'has-[[data-row-main]:active]:bg-accent absolute inset-x-0 flex items-center pr-1 transition-colors duration-100',
                // Inset separator from the text column (16 margin + 48 art + 12 gap), none after the last.
                i < sorted.length - 1 &&
                  "after:bg-separator after:absolute after:right-0 after:bottom-0 after:left-[76px] after:h-(--hairline) after:content-['']"
              )}
              style="top: {i * rowH}px; height: {rowH}px;"
            >
              <!-- The loaded row is announced as such, and — since a tap on it opens Now Playing
                   rather than playing it — says so, as Android's "Show player" click label does. -->
              <button
                type="button"
                data-row-main=""
                onclick={() => (onActivate ?? onSelect)(song)}
                aria-current={isLoaded ? 'true' : undefined}
                class="focus-visible:ring-ring flex h-full min-w-0 flex-1 items-center gap-3 pl-4 text-left outline-none focus-visible:ring-2 focus-visible:ring-inset"
              >
                <span class="relative shrink-0">
                  <Cover
                    artist={artistOf(song)}
                    title={song.album ?? titleOf(song)}
                    coverUrl={coverUrlForSong(song)}
                    size={48}
                    corner={6}
                    caption={false}
                    dprCap={3}
                  />
                  {#if isLoaded}
                    <span
                      class="absolute inset-0 grid place-items-center rounded-[6px] bg-black/45 text-white"
                    >
                      {@render equalizer(isCurrentlyPlaying, '')}
                    </span>
                  {/if}
                </span>
                <TrackRowText {song} loaded={isLoaded} opensPlayer={isLoaded && !!onActivate} />
              </button>
              <TrackRowMenu
                bind:this={() => menus[song.id], (m) => (menus[song.id] = m)}
                {song}
                onplay={() => playFrom(song)}
              />
            </div>
          {:else}
            {@const family = formatFamily(song.extension)}
            {@const mv = matchValue(song)}
            {@const isSelected = selectedId === song.id}
            {@const origin = songOriginLabel(song)}
            {@const isFromSpotify = isSpotifySourced(song)}
            <div
              role="button"
              tabindex="0"
              aria-current={isLoaded ? 'true' : undefined}
              onclick={() => onSelect(song)}
              onkeydown={(e) => onRowKeydown(e, song)}
              use:longpress={{ onlongpress: (p) => openMenu(song.id, p) }}
              oncontextmenu={(e) => onRowContextMenu(e, song.id)}
              class={cn(
                'group border-separator absolute right-0 left-0 grid cursor-pointer items-center gap-3 border-b px-3 transition-colors duration-100 md:inset-x-4',
                GRID,
                'hover:bg-accent',
                isSelected && 'bg-primary/12 hover:bg-primary/12',
                isLoaded && 'text-primary'
              )}
              style="top: {i * rowH}px; height: {rowH}px;"
            >
              <!-- # / play -->
              <span
                class="text-muted-foreground relative grid h-full place-items-center text-right"
              >
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
                    togglePlayFrom(song);
                  }}
                  aria-label={isCurrentlyPlaying ? 'Pause track' : 'Play track'}
                  class="peer text-primary absolute inset-0 grid place-items-center opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100 pointer-coarse:opacity-100"
                >
                  {#if isCurrentlyPlaying}
                    <Pause class="size-3.5" fill="currentColor" />
                  {:else}
                    <Play class="size-3.5" fill="currentColor" />
                  {/if}
                </button>
                {#if isLoaded}
                  {@render equalizer(
                    isCurrentlyPlaying,
                    'text-primary group-hover:opacity-0 peer-focus-visible:opacity-0 pointer-coarse:opacity-0'
                  )}
                {:else}
                  <span
                    class="pointer-events-none text-[11px] tabular-nums transition-opacity group-hover:opacity-0 peer-focus-visible:opacity-0 pointer-coarse:opacity-0"
                  >
                    {i + 1}
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
                  {#if isLoaded}<span class="sr-only">Now playing, </span>{/if}{titleOf(song)}
                </div>
                <div class="text-muted-foreground mt-0.5 flex items-center gap-2 text-[11px]">
                  {#if needsReview}
                    <Badge
                      variant="warning"
                      class="min-h-0 px-1.5 py-px"
                      title="Enrichment uncertain — needs review">Review</Badge
                    >
                  {/if}
                  <!-- On hover or selection only: nearly every row has lyrics, so at rest the
                       badge was a column of "LRC" that said nothing (the With lyrics filter is
                       the way to find the ones that don't). -->
                  {#if hasLyrics(song)}
                    <span
                      class={cn(
                        'bg-muted text-foreground rounded px-1 py-px text-[11px] font-semibold opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100',
                        isSelected && 'opacity-100'
                      )}
                    >
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
                  <!-- Only when the list mixes libraries (see TrackRowText); renders nothing for a
                       track this account owns. -->
                  {#if songsStore.hasMixedSources}
                    <SharedByBadge {song} variant="icon" />
                  {/if}
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
              <span class="text-muted-foreground hidden text-[11px] tabular-nums @3xl:block">
                {song.year ?? '—'}
              </span>
              <!-- format -->
              <span
                class="text-muted-foreground hidden items-center gap-1.5 text-[11px] tabular-nums @xl:flex"
              >
                {#if family === 'OTHER'}
                  <span>{(song.extension ?? '').replace(/^\./, '').toUpperCase() || '—'}</span>
                {:else}
                  <span class="text-foreground font-medium">{family}</span>
                {/if}
                {#if song.bitRate && song.bitRate > 0}
                  <span class="text-muted-foreground hidden text-[11px] @5xl:inline"
                    >{song.bitRate}kbps</span
                  >
                {/if}
              </span>
              <!-- size -->
              <span class="text-muted-foreground hidden text-[11px] tabular-nums @5xl:block">
                {formatFileSize(song.fileSizeBytes)}
              </span>
              <!-- match -->
              <span class="hidden items-center gap-2 @5xl:flex">
                {#if mv != null}
                  <span class="bg-secondary h-1 flex-1 overflow-hidden rounded-full">
                    <span
                      class="bg-muted-foreground block h-full rounded-full"
                      style="width: {mv * 100}%;"
                    ></span>
                  </span>
                  <span
                    class="text-muted-foreground min-w-[28px] text-right text-[11px] tabular-nums"
                    >{mv.toFixed(2)}</span
                  >
                {:else}
                  <span
                    class="text-muted-foreground flex-1 text-right text-[11px]"
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
                      'inline-block max-w-full truncate rounded px-1.5 py-0.5 text-[11px] font-medium',
                      isFromSpotify
                        ? 'bg-primary/12 text-primary'
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
                aria-label="Favourite"
                aria-pressed={isLiked}
                title={isLiked ? 'Remove from favourites' : 'Add to favourites'}
                class={cn(
                  'relative grid h-7 place-items-center transition-all active:scale-90',
                  // The column is only 28px wide and the icon 14px; on touch (an iPad) grow the hit
                  // region with an invisible pseudo-element rather than the visible column.
                  "pointer-coarse:after:absolute pointer-coarse:after:-inset-3 pointer-coarse:after:content-['']",
                  isLiked
                    ? 'text-primary'
                    : 'text-muted-foreground hover:text-foreground opacity-0 group-hover:opacity-100 focus-visible:opacity-100 pointer-coarse:opacity-100'
                )}
              >
                <Heart
                  class="size-3.5 pointer-coarse:size-5"
                  fill={isLiked ? 'currentColor' : 'none'}
                />
              </button>
              <!-- duration -->
              <span class="text-muted-foreground text-right text-[11px] tabular-nums">
                {formatDuration(song.durationSeconds)}
              </span>
              <!-- ⋯: hover-revealed, resting on touch, and kept up while its menu is open -->
              <TrackRowMenu
                bind:this={() => menus[song.id], (m) => (menus[song.id] = m)}
                {song}
                onplay={() => playFrom(song)}
                class="size-8 opacity-0 group-hover:opacity-100 focus-visible:opacity-100 aria-expanded:opacity-100 pointer-coarse:opacity-100"
              />
            </div>
          {/if}
        {/each}
      </div>
    {/if}
  </div>
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
  /* Belt-and-braces alongside the global reduced-motion clamp: a static three-bar pose. */
  @media (prefers-reduced-motion: reduce) {
    .mh-eq.is-playing > :global(i) {
      animation: none;
    }
  }
</style>

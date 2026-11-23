<script lang="ts">
  import { untrack, type Snippet } from 'svelte';
  import { flip, type AnimationConfig } from 'svelte/animate';
  import type { TransitionConfig } from 'svelte/transition';
  import { MediaQuery } from 'svelte/reactivity';
  import { goto } from '$app/navigation';
  import { Disc3, Mic2, Play, Shuffle } from '@lucide/svelte';
  import { EmptyState } from '#lib/components/ui/empty-state/index.ts';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import * as DropdownMenu from '#lib/components/ui/dropdown-menu/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import { longpress, type LongPressPoint } from '#lib/actions/long-press.ts';
  import { easeInOut, easeOut, prefersReducedMotion } from '#lib/motion.ts';
  import { toPlayerSong, type AlbumStatusInfo, type AlbumSummary } from '#lib/api-client.ts';
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { cn } from '#lib/utils.ts';
  import { carryBuiltTiles, watchNearViewport } from '#lib/viewport-fill.ts';

  // Safe to server-render: everything interactive is opt-in and browser-only — the menu is off
  // unless asked for, and the long-press action and the menu's DOM only exist in the browser.
  type Props = {
    albums: AlbumSummary[];
    /** href builder for an album card (keeps deep-linkable `?album=` URLs). */
    hrefFor: (album: AlbumSummary) => string;
    /** Whether the underlying songs are still loading (controls the empty/skeleton copy). */
    isLoading?: boolean;
    /** Per-album provider-link status (keyed by `artistLower::titleLower`) for the corner badge. */
    statuses?: Map<string, AlbumStatusInfo>;
    /** `shelf`: one sideways-scrolling row (the Overview's shelves) instead of the wrapping grid. */
    layout?: 'grid' | 'shelf';
    /**
     * A touch-and-hold (and right-click) menu on each tile: Play, Shuffle, Go to artist. Its
     * visible twin is the album page's own Play / Shuffle. Off by default.
     */
    menu?: boolean;
    /** Off on an artist's own view, where "Go to artist" would go nowhere. */
    showArtistLink?: boolean;
    /**
     * What an empty list shows — the page knows why it is empty (a search miss, a filter) and
     * what would undo it. Defaults to a plain "No albums".
     */
    empty?: Snippet;
  };
  const {
    albums,
    hrefFor,
    isLoading = false,
    statuses,
    layout = 'grid',
    menu = false,
    showArtistLink = true,
    empty
  }: Props = $props();

  // Two columns on a phone (the Apple Music grid), three to six from md as the window widens.
  const GRID_CLASS =
    'grid grid-cols-2 gap-x-4 gap-y-5 md:grid-cols-4 md:gap-x-5 md:gap-y-6 lg:grid-cols-5 xl:grid-cols-6';

  /**
   * Corner mark for an album whose link status needs attention, or null for none. Only the
   * exceptions are marked — a disputed match, an album on no provider — so the normal state
   * (linked, or still being checked) carries no dot: a dot on every cover said nothing. Kept as a
   * `kind` rather than a colour class so the template can tell the two apart by shape too, not
   * just hue — a `title` alone never reaches a touch tap, and colour alone doesn't survive
   * Increase Contrast or colour-blindness. The album page's status row still shows the full
   * link state, "Linked · Spotify, MusicBrainz" included.
   */
  type Badge = { kind: 'wrong' | 'localOnly'; label: string };
  function badgeFor(album: AlbumSummary): Badge | null {
    // Canonical link-status is keyed by album name (artist+title), not the folder-based album.key —
    // cards split across releases share the same name-based status badge.
    const info = statuses?.get(`${album.artist.toLowerCase()}::${album.title.toLowerCase()}`);
    if (!info) return null;
    // A confirmed mis-match dominates the badge regardless of link state.
    if (info.verdict === 'Wrong') {
      return { kind: 'wrong', label: 'Likely wrong album — AI flagged the match' };
    }
    if (info.status === 'localOnly') {
      return { kind: 'localOnly', label: 'Local only — not on any provider' };
    }
    return null;
  }

  // Play and Shuffle never pause — not even when the album's first track is the one playing.
  function playAlbum(album: AlbumSummary, shuffled = false) {
    if (album.songs.length === 0) return;
    const ordered = album.songs.map((s) => toPlayerSong(s, album.artist));
    void playerStore.startQueue(ordered, 0, {
      shuffle: shuffled,
      source: { label: album.title, href: `/library?album=${encodeURIComponent(album.key)}` }
    });
  }

  function playFirst(album: AlbumSummary, e: MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    playAlbum(album);
  }

  // ── tile menu ─────────────────────────────────────────────────────────────
  // One menu for the whole grid, anchored at the finger (or pointer) of whichever tile opened it,
  // the way an iOS context menu grows out of the thing held.
  let menuAlbum = $state<AlbumSummary | null>(null);
  let menuOpen = $state(false);
  let menuPoint = $state<LongPressPoint>({ x: 0, y: 0 });
  const menuAnchor = $derived.by(() => {
    const { x, y } = menuPoint;
    return { getBoundingClientRect: () => new DOMRect(x, y, 0, 0) };
  });

  // The tile that opened the menu, so focus goes back to it on close. The menu's own trigger is a
  // hidden stand-in (it anchors nowhere), and returning focus there left a keyboard or Switch
  // Control user on <body>.
  let menuTile: HTMLElement | null = null;

  function openMenu(album: AlbumSummary, point: LongPressPoint, tile: HTMLElement | null) {
    menuAlbum = album;
    menuPoint = point;
    menuTile = tile;
    menuOpen = true;
  }
  /** The tile link under a long-press point (the hold is on its inner box). */
  function tileOf(point: LongPressPoint): HTMLElement | null {
    return document.elementFromPoint(point.x, point.y)?.closest<HTMLElement>('a[href]') ?? null;
  }
  function onTileContextMenu(e: MouseEvent, album: AlbumSummary) {
    e.preventDefault();
    const tile = e.currentTarget as HTMLElement;
    // The keyboard's Menu key (or Shift+F10) sends a contextmenu with no pointer position: open it
    // under the tile's art instead of at the corner of the window.
    if (e.clientX === 0 && e.clientY === 0) {
      const r = tile.getBoundingClientRect();
      openMenu(album, { x: r.left + 8, y: r.top + Math.min(r.height, r.width) }, tile);
      return;
    }
    openMenu(album, { x: e.clientX, y: e.clientY }, tile);
  }

  // A library's grid is hundreds or thousands of tiles, and building every cover, badge, button
  // and gesture handler in one task — or eagerly in idle slices — held the main thread for seconds
  // on big grids and big artist pages. Every album gets its link at once, so the grid has its full
  // height and scroll position from the first frame; the heavy inside (Cover, images, long-press,
  // hover Play) is built for the first screenful and then only as each tile comes within two
  // screens of the viewport or takes focus (`#lib/viewport-fill.ts`).
  //
  // With a mouse or trackpad at md and wider, find-in-page needs the album titles in the DOM, so
  // unbuilt tiles there still carry their lightweight title/artist text (zero Cover/img/listeners)
  // while waiting for the viewport to build their artwork.
  const FIRST_FILL = 36;
  const searchablePlaceholders = new MediaQuery('(min-width: 768px) and (pointer: fine)');

  // The albums whose tile has been built: the first screenful, and each one that came within two
  // screens of the viewport or took focus (see `#lib/viewport-fill.ts`). By key, and never taken
  // back while the album stays in the list, so a re-sort moves built tiles instead of tearing them
  // down. `$state.raw`, replaced per batch: one set read by every tile is cheaper than a signal
  // per album.
  //
  // It starts as what the first frame builds by position, so on a fresh grid the effect below finds
  // nothing to add: a write there, right after mount, would make every one of the hundreds of
  // tiles just created check again whether it is built (tens of milliseconds on a phone).
  let gridEl = $state<HTMLElement | null>(null);
  let builtKeys = $state.raw<ReadonlySet<string>>(
    carryBuiltTiles(
      new Set(),
      untrack(() => albums),
      FIRST_FILL
    )
  );
  $effect(() => {
    const grid = gridEl;
    const list = albums;
    // A shelf is a dozen tiles, all built.
    if (layout === 'shelf') return;
    // Before the grid check: a list that emptied has no grid, and must still be forgotten.
    untrack(() => (builtKeys = carryBuiltTiles(builtKeys, list, FIRST_FILL)));
    if (!grid) return;
    // A new watcher per list: a changed list can hold links that did not exist a moment ago.
    const watcher = watchNearViewport(grid, {
      attribute: 'data-album-key',
      onnear: (keys) =>
        untrack(() => {
          const fresh = keys.filter((key) => !builtKeys.has(key));
          if (fresh.length > 0) builtKeys = new Set([...builtKeys, ...fresh]);
        })
    });
    return () => watcher.destroy();
  });

  const SHELF_FLIP_MS = 280;

  function shelfFlip(node: Element, positions: { from: DOMRect; to: DOMRect }): AnimationConfig {
    return flip(node, positions, {
      duration: prefersReducedMotion() ? 0 : SHELF_FLIP_MS,
      easing: easeInOut
    });
  }

  function shelfIn(_node: Element): TransitionConfig {
    const reduced = prefersReducedMotion();
    return {
      duration: 260,
      easing: easeOut,
      css: (t) =>
        reduced ? `opacity: ${t}` : `opacity: ${t}; transform: scale(${0.94 + 0.06 * t})`
    };
  }

  // Chromium and WebKit re-snap a scroller to the tile it was snapped to after a layout change, so
  // an album arriving at the front scrolled the row to follow the old first tile and hid the new
  // one off the left edge.
  let shelfViewport = $state<HTMLElement | null>(null);
  let shelfHeld = false;
  $effect.pre(() => {
    void albums;
    const viewport = untrack(() => shelfViewport);
    shelfHeld = layout === 'shelf' && viewport !== null && viewport.scrollLeft < 2;
    if (shelfHeld) viewport!.style.scrollSnapType = 'none';
  });
  $effect(() => {
    void albums;
    const viewport = untrack(() => shelfViewport);
    if (!shelfHeld || !viewport) return;
    viewport.scrollLeft = 0;
    const release = setTimeout(() => (viewport.style.scrollSnapType = ''), SHELF_FLIP_MS + 40);
    return () => {
      clearTimeout(release);
      viewport.style.scrollSnapType = '';
    };
  });

  function shelfOut(_node: Element): TransitionConfig {
    return { duration: 150, easing: easeOut, css: (t) => `opacity: ${t}` };
  }
</script>

{#snippet tileMeta(album: AlbumSummary)}
  <div class="min-w-0 px-0.5">
    <p
      class="text-subheadline truncate font-semibold md:text-[12.5px] md:leading-snug md:font-medium"
    >
      {album.title}
    </p>
    <p class="text-footnote text-muted-foreground truncate md:text-[11.5px]">
      {album.artist}{album.year ? ` · ${album.year}` : ''}
    </p>
    {#if album.folderKeys.length > 1}
      <!-- The card folds together several destination folders — say so rather than silently
           hiding that this album is split on disk. -->
      <p
        class="text-footnote text-muted-foreground-dim md:text-[11px]"
        title={album.folderKeys.join('\n')}
      >
        {album.folderKeys.length} editions
      </p>
    {/if}
  </div>
{/snippet}

{#snippet tile(album: AlbumSummary, shelf: boolean, filled: boolean, deferred: boolean)}
  {@const badge = filled ? badgeFor(album) : null}
  <a
    href={hrefFor(album)}
    data-album-key={shelf ? undefined : album.key}
    oncontextmenu={menu ? (e) => onTileContextMenu(e, album) : undefined}
    class={cn(
      'group focus-visible:ring-ring flex flex-col rounded-lg focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:outline-hidden',
      shelf
        ? 'w-[150px] shrink-0 snap-start md:w-[168px]'
        : 'p-1 transition-transform hover:-translate-y-0.5',
      !shelf && deferred && 'mh-deferred-tile'
    )}
    aria-label={`Open album ${album.title} by ${album.artist}`}
  >
    {#if !filled}
      <!-- Not built yet (see `builtKeys`): the link is already a working tile of the right size. -->
      <div class="flex flex-col gap-2">
        <div class="bg-muted aspect-square w-full rounded-lg" aria-hidden="true"></div>
        {#if searchablePlaceholders.current}
          {@render tileMeta(album)}
        {:else}
          <div class="h-9" aria-hidden="true"></div>
        {/if}
      </div>
    {:else}
      <!-- The hold (and its 97% press feedback) lives on this inner box, not on the link: on a shelf
         the link is the snap target, and scaling it made Chromium re-snap the row, which read as
         a scroll and cancelled the hold on the first tile. -->
      <div
        class="flex flex-col gap-2"
        use:longpress={{
          onlongpress: (p) => openMenu(album, p, tileOf(p)),
          disabled: !menu
        }}
      >
        <div class="relative">
          <Cover
            artist={album.artist}
            title={album.title}
            coverUrl={album.coverUrl}
            size={176}
            corner={8}
            interactive
            class="aspect-square !h-auto !w-full shadow-[0_2px_10px_rgba(0,0,0,0.12)] hover:shadow-[0_8px_24px_rgba(0,0,0,0.18)] dark:shadow-[0_4px_14px_rgba(0,0,0,0.5)] dark:hover:shadow-[0_10px_28px_rgba(0,0,0,0.6)]"
          />
          {#if badge}
            <span
              role="img"
              aria-label={badge.label}
              title={badge.label}
              class={cn(
                'absolute top-1.5 left-1.5 grid size-2.5 place-items-center rounded-full ring-2 ring-black/35',
                badge.kind === 'wrong' && 'border-destructive bg-destructive/30 border-2',
                badge.kind === 'localOnly' && 'border-2 border-white/80 bg-transparent'
              )}
            ></span>
          {/if}
          <!-- Desktop only: a hover play button. A phone has no hover, so there the tile's single
           action is opening the album (Play lives on the album page and in the hold menu). -->
          <button
            type="button"
            aria-label={`Play ${album.title}`}
            onclick={(e) => playFirst(album, e)}
            class="bg-primary text-primary-foreground absolute right-2 bottom-2 grid size-9 translate-y-1 place-items-center rounded-full opacity-0 shadow-md transition-all duration-150 group-focus-within:opacity-100 group-hover:translate-y-0 group-hover:opacity-100 focus-visible:translate-y-0 focus-visible:opacity-100 pointer-coarse:hidden"
          >
            <Play class="size-4" fill="currentColor" />
          </button>
        </div>
        {@render tileMeta(album)}
      </div>
    {/if}
  </a>
{/snippet}

{#if isLoading && albums.length === 0}
  <!-- Skeleton tiles in the real grid, not a spinner or a sentence — a first-run visitor should see
       an incoming grid, not what reads as an empty page. -->
  <div class={GRID_CLASS}>
    {#each Array(12) as _, i (i)}
      <div class="flex flex-col gap-2 p-1">
        <Skeleton class="aspect-square w-full rounded-md" />
        <div class="min-w-0 space-y-1.5 px-0.5">
          <Skeleton class="h-3 w-4/5" />
          <Skeleton class="h-3 w-3/5" />
        </div>
      </div>
    {/each}
  </div>
{:else if albums.length === 0}
  {#if empty}
    {@render empty()}
  {:else}
    <EmptyState icon={Disc3} title="No albums" />
  {/if}
{:else if layout === 'shelf'}
  <!-- A horizontal ScrollArea rather than a bare overflow-x row: the native bar sat permanently
       under every shelf, while this one overlays the row and shows only on hover. Snapping goes on
       the viewport (the element that scrolls); data-scroll-x keeps the installed app's edge swipe
       out of it. -->
  <ScrollArea
    bind:viewportRef={shelfViewport}
    orientation="horizontal"
    data-scroll-x=""
    class="[&>[data-slot=scroll-area-viewport]]:snap-x [&>[data-slot=scroll-area-viewport]]:scroll-px-4 md:[&>[data-slot=scroll-area-viewport]]:scroll-px-7"
  >
    <div class="flex gap-3 px-4 pt-2 pb-3 md:gap-4 md:px-7">
      {#each albums as album (album.key)}
        <div class="shrink-0" animate:shelfFlip in:shelfIn out:shelfOut>
          {@render tile(album, true, true, false)}
        </div>
      {/each}
    </div>
  </ScrollArea>
{:else}
  <div class={GRID_CLASS} bind:this={gridEl}>
    {#each albums as album, i (album.key)}
      {@render tile(album, false, i < FIRST_FILL || builtKeys.has(album.key), i >= FIRST_FILL)}
    {/each}
  </div>
{/if}

{#if menu}
  <DropdownMenu.Root bind:open={menuOpen}>
    <!-- The menu is anchored at the finger (customAnchor); this trigger only exists because a
         menu needs one to return focus to. Never a tap target. -->
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <span {...props} tabindex="-1" aria-hidden="true" class="sr-only"></span>
      {/snippet}
    </DropdownMenu.Trigger>
    <DropdownMenu.Content
      customAnchor={menuAnchor}
      side="bottom"
      align="start"
      class="w-60 pointer-coarse:w-72"
      onCloseAutoFocus={(e) => {
        if (!menuTile?.isConnected) return;
        e.preventDefault();
        menuTile.focus({ preventScroll: true });
      }}
    >
      {#if menuAlbum}
        {@const album = menuAlbum}
        <DropdownMenu.Label class="truncate">{album.title}</DropdownMenu.Label>
        <DropdownMenu.Group>
          <DropdownMenu.Item onSelect={() => playAlbum(album)}>
            <Play /> Play
          </DropdownMenu.Item>
          <DropdownMenu.Item onSelect={() => playAlbum(album, true)}>
            <Shuffle /> Shuffle
          </DropdownMenu.Item>
        </DropdownMenu.Group>
        {#if showArtistLink}
          <DropdownMenu.Separator />
          <DropdownMenu.Item
            onSelect={() => void goto(`/library?artist=${encodeURIComponent(album.artist)}`)}
          >
            <Mic2 /> Go to artist
          </DropdownMenu.Item>
        {/if}
      {/if}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/if}

<style>
  /* Below-the-fold album tiles skip off-screen layout/paint; older browsers keep containment. */
  .mh-deferred-tile {
    content-visibility: auto;
    contain-intrinsic-size: auto none auto 13rem;
  }
  @supports not (content-visibility: auto) {
    .mh-deferred-tile {
      contain: layout style paint;
    }
  }
</style>

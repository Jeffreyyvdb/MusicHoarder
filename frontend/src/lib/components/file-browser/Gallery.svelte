<script lang="ts">
  import { onMount } from 'svelte';
  import { ArrowLeft, Disc3, Music, Play } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import ProcessingStrip from '$lib/components/file-browser/ProcessingStrip.svelte';
  import { buildAlbumsFromSongs, toPlayerSong, sortAlbumsByRecency, type ApiSong } from '$lib/api-client';
  import { SECTION_LABELS, type SectionId } from '$lib/album-sections';
  import { formatFileSize, formatDuration } from '$lib/formatters';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  type BrowseFilter = { label: string; clearHref: string; kind: 'artist' | 'year' };
  type Props = {
    songs: ApiSong[];
    section: SectionId;
    searchQuery: string;
    isLoading: boolean;
    /** When set, the gallery shows an "Organize by" drill-down (artist/year) header + back link. */
    browseFilter?: BrowseFilter | null;
  };
  const { songs, section, searchQuery, isLoading, browseFilter = null }: Props = $props();

  type Layout = 'grid' | 'list' | 'col';

  function getStoredLayout(): Layout {
    if (typeof window === 'undefined') return 'grid';
    const v = localStorage.getItem('musichoarder-library-view');
    // 'col' is a "coming soon" option the header never activates; treat a stale
    // stored 'col' as grid so the rendered view and the header stay in sync.
    if (v === 'list' || v === 'grid') return v;
    return 'grid';
  }

  let layout = $state<Layout>('grid');

  onMount(() => {
    layout = getStoredLayout();
    const handler = (e: Event) => {
      const next = (e as CustomEvent).detail as Layout | undefined;
      if (next === 'list' || next === 'grid') {
        layout = next;
        // The previous layout's row height must not leak into the new stride;
        // reset synchronously so the first paint uses the correct estimate.
        measuredRow = 0;
        if (viewport) viewport.scrollTop = 0;
        scrollTop = 0;
      }
    };
    window.addEventListener('mh:layout-change', handler);
    return () => window.removeEventListener('mh:layout-change', handler);
  });

  const albums = $derived(
    section === 'recent'
      ? sortAlbumsByRecency(buildAlbumsFromSongs(songs))
      : buildAlbumsFromSongs(songs)
  );
  const filtered = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return albums;
    return albums.filter(
      (a) =>
        a.title.toLowerCase().includes(q) ||
        a.artist.toLowerCase().includes(q) ||
        (a.genre?.toLowerCase().includes(q) ?? false)
    );
  });

  const meta = $derived(SECTION_LABELS[section]);
  const showProcessing = $derived(section === 'lib' || section === 'queue');
  const isQueue = $derived(section === 'queue');

  // ── Row windowing ─────────────────────────────────────────────────────────
  // Rendering every album at once produced a ~31k-node DOM and a multi-hundred-MB
  // heap (one mounted Cover per album), which hitched the main thread / audio
  // during playback. We render only the on-screen rows (+ overscan) instead.
  const GAP_X = 20; // gap-x-5
  const GAP_Y = 24; // gap-y-6
  const OVERSCAN = 3;
  const LIST_ROW_BORDER = 1;

  let viewport = $state<HTMLElement | null>(null);
  let listEl = $state<HTMLDivElement | null>(null);
  let scrollTop = $state(0);
  let viewportH = $state(0);
  let containerW = $state(0);
  let listTop = $state(0); // offset of the list from the scroll-content top
  let measuredRow = $state(0);
  let measured = $state(false); // true once the viewport has been measured at least once
  let contentEl = $state<HTMLDivElement | null>(null);

  const columns = $derived.by(() => {
    if (layout !== 'grid') return 1;
    const w = containerW || 1200;
    if (w < 480) return 2;
    if (w < 720) return 3;
    if (w < 960) return 4;
    if (w < 1200) return 5;
    return 6;
  });

  const rows = $derived.by(() => {
    const out: (typeof filtered)[] = [];
    for (let i = 0; i < filtered.length; i += columns) out.push(filtered.slice(i, i + columns));
    return out;
  });

  // Estimate used until a real row has been measured; self-corrects after paint.
  const estRowHeight = $derived.by(() => {
    if (layout !== 'grid') return 49;
    const pad = (containerW || 1200) >= 768 ? 48 : 32; // md:p-6 (24*2) vs p-4 (16*2)
    const colW = (Math.max(0, (containerW || 1200) - pad) - (columns - 1) * GAP_X) / columns;
    return colW + 56; // square cover + two text lines
  });
  const rowStride = $derived(
    Math.max(1, (measuredRow || estRowHeight) + (layout === 'grid' ? GAP_Y : LIST_ROW_BORDER))
  );

  const effectiveScroll = $derived(Math.max(0, scrollTop - listTop));
  // Clamp to the last row so a stale-large scrollTop (e.g. results shrank while
  // scrolled deep) can never slice past the end and blank the list; the shorter
  // topPad then lets the browser clamp scrollTop and the window self-corrects.
  const startRow = $derived(
    Math.min(
      Math.max(0, Math.floor(effectiveScroll / rowStride) - OVERSCAN),
      Math.max(0, rows.length - 1)
    )
  );
  const endRow = $derived(
    Math.min(
      rows.length,
      Math.ceil((effectiveScroll + (measured ? viewportH : 800)) / rowStride) + OVERSCAN
    )
  );
  const visibleRows = $derived(rows.slice(startRow, endRow));
  const topPad = $derived(startRow * rowStride);
  const bottomPad = $derived(Math.max(0, (rows.length - endRow) * rowStride));

  // Offset of the list within the scroll content. Scroll-independent: as you
  // scroll, listEl.top falls while vp.scrollTop rises by the same amount.
  function measureListTop() {
    const vp = viewport;
    const el = listEl;
    if (!vp || !el) return;
    listTop = el.getBoundingClientRect().top - vp.getBoundingClientRect().top + vp.scrollTop;
  }

  // Wire up scroll + resize on the ScrollArea viewport and its content.
  $effect(() => {
    const vp = viewport;
    const content = contentEl;
    if (!vp) return;
    const sync = () => {
      containerW = vp.clientWidth;
      viewportH = vp.clientHeight;
      measured = true;
      measureListTop();
    };
    const onScroll = () => {
      scrollTop = vp.scrollTop;
      measureListTop();
    };
    vp.addEventListener('scroll', onScroll, { passive: true });
    // Observe the viewport (its size) AND the content (height changes above the
    // list — e.g. the ProcessingStrip growing/shrinking during a job) so listTop
    // never goes stale and mis-windows the rows.
    const ro = new ResizeObserver(sync);
    ro.observe(vp);
    if (content) ro.observe(content);
    scrollTop = vp.scrollTop;
    sync();
    // Sync to scroll restoration applied after this effect runs (back-nav, ssr=false).
    const raf = requestAnimationFrame(() => {
      if (!viewport) return;
      scrollTop = viewport.scrollTop;
      measureListTop();
    });
    return () => {
      vp.removeEventListener('scroll', onScroll);
      ro.disconnect();
      cancelAnimationFrame(raf);
    };
  });

  // Re-measure listTop when content above the list changes for non-size reasons.
  $effect(() => {
    void filtered.length;
    void layout;
    void showProcessing;
    void containerW;
    measureListTop();
  });

  // Reset to the top when the user changes what's shown (search / section /
  // drill-down) so a stale deep scrollTop can't window past the new, shorter set.
  // Keyed on user inputs only — a background `songs` refresh won't yank scroll.
  let prevViewKey = '';
  $effect(() => {
    const key = `${section} ${searchQuery.trim()} ${browseFilter?.label ?? ''}`;
    if (key === prevViewKey) return;
    prevViewKey = key;
    if (viewport) viewport.scrollTop = 0;
    scrollTop = 0;
  });

  // Measure an actual rendered row height; corrects the estimate. Intentionally
  // does NOT depend on scroll position — measuring per scroll-tick would force a
  // layout flush on every frame, the very cost this windowing removes.
  $effect(() => {
    void columns;
    void layout;
    void containerW;
    void filtered.length;
    const el = listEl;
    if (!el) return;
    const row = el.querySelector<HTMLElement>('[data-vrow]');
    if (row) {
      const h = row.clientHeight;
      if (h > 0 && Math.abs(h - measuredRow) > 1) measuredRow = h;
    }
  });

  function playFirst(albumKey: string, e: MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    const album = filtered.find((a) => a.key === albumKey);
    if (!album || album.songs.length === 0) return;
    const queue = album.songs.map((s) => toPlayerSong(s, album.artist));
    void playerStore.playSong(queue[0], queue, 0);
  }

  function albumHref(key: string) {
    return `/library?album=${encodeURIComponent(key)}`;
  }
</script>

{#if isLoading && albums.length === 0}
  <div class="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
    Loading albums…
  </div>
{:else}
  <ScrollArea bind:viewportRef={viewport} class="min-h-0 flex-1">
    <div bind:this={contentEl} class="p-4 pb-20 md:p-6">
      <div class="mb-4 flex items-end justify-between gap-4">
        <div class="min-w-0">
          {#if browseFilter}
            <a
              href={browseFilter.clearHref}
              class="text-muted-foreground hover:text-foreground mb-1 inline-flex items-center gap-1 text-xs"
            >
              <ArrowLeft class="size-3.5" />
              All {browseFilter.kind === 'artist' ? 'artists' : 'years'}
            </a>
            <h2 class="truncate text-2xl font-semibold tracking-[-0.02em]">{browseFilter.label}</h2>
            <p class="text-muted-foreground mt-1 text-xs">
              {filtered.length.toLocaleString()} album{filtered.length === 1 ? '' : 's'}
              {#if searchQuery.trim()}
                <span class="ml-1">· matching "{searchQuery.trim()}"</span>
              {/if}
            </p>
          {:else}
            <h2 class="truncate text-2xl font-semibold tracking-[-0.02em]">{meta.title}</h2>
            <p class="text-muted-foreground mt-1 text-xs">
              {meta.subtitle(filtered.length)}
              {#if searchQuery.trim()}
                <span class="ml-1">· matching "{searchQuery.trim()}"</span>
              {/if}
            </p>
          {/if}
        </div>
        <div class="text-muted-foreground hidden text-xs sm:block">
          Sort by <span class="text-foreground/80 ml-1 cursor-pointer">Recently added ▾</span>
        </div>
      </div>

      {#if showProcessing}
        <ProcessingStrip />
      {/if}

      {#if isQueue}
        <div class="text-muted-foreground px-2 py-12 text-center">
          <div class="text-foreground text-base font-medium">Queue tails live above</div>
          <div class="mt-1 text-xs">
            Items appear in the library once they finish writing to destination.
          </div>
        </div>
      {:else if filtered.length === 0}
        <div
          class="text-muted-foreground flex flex-col items-center justify-center gap-3 py-16 text-center"
        >
          <Disc3 class="size-10 opacity-40" />
          <p class="text-sm">
            {searchQuery.trim()
              ? 'No albums match your search.'
              : 'Nothing in this section yet.'}
          </p>
        </div>
      {:else}
        <div class="text-muted-foreground mt-1 mb-3 text-sm font-medium">
          {layout === 'grid' ? 'All albums' : 'Details'}
        </div>

        {#if layout === 'grid'}
          <div bind:this={listEl}>
            {#if topPad > 0}<div style="height: {topPad}px;" aria-hidden="true"></div>{/if}
            {#each visibleRows as row, ri (startRow + ri)}
              <div
                data-vrow
                class="grid gap-x-5"
                style="grid-template-columns: repeat({columns}, minmax(0, 1fr)); margin-bottom: {GAP_Y}px;"
              >
                {#each row as album (album.key)}
                  <a
                    href={albumHref(album.key)}
                    class="group focus-visible:ring-ring outline-hidden flex flex-col gap-2 rounded-lg p-1 transition-transform hover:-translate-y-0.5 focus-visible:ring-2 focus-visible:ring-offset-2"
                    aria-label={`Open album ${album.title} by ${album.artist}`}
                  >
                    <div class="relative">
                      <Cover
                        artist={album.artist}
                        title={album.title}
                        coverUrl={album.coverUrl}
                        size={176}
                        interactive
                        class="!w-full !h-auto aspect-square"
                      />
                      <button
                        type="button"
                        aria-label={`Play ${album.title}`}
                        onclick={(e) => playFirst(album.key, e)}
                        class="bg-primary text-primary-foreground absolute right-2 bottom-2 grid size-9 translate-y-1 place-items-center rounded-full opacity-0 shadow-md transition-all duration-150 group-hover:translate-y-0 group-hover:opacity-100"
                      >
                        <Play class="size-4" />
                      </button>
                    </div>
                    <div class="min-w-0 px-0.5">
                      <p class="truncate text-[12.5px] font-medium">{album.title}</p>
                      <p class="text-muted-foreground truncate text-[11.5px]">
                        {album.artist}{album.year ? ` · ${album.year}` : ''}
                      </p>
                    </div>
                  </a>
                {/each}
              </div>
            {/each}
            {#if bottomPad > 0}<div style="height: {bottomPad}px;" aria-hidden="true"></div>{/if}
          </div>
        {:else}
          <!-- list view -->
          <div class="border-border bg-card overflow-hidden rounded-lg border">
            <div
              class={cn(
                'bg-surface-sunken border-border text-muted-foreground grid items-center gap-4 border-b px-3.5 py-2 text-[10px] font-semibold tracking-wider uppercase',
                'grid-cols-[44px_2.2fr_1.4fr_1fr_0.8fr_0.8fr_0.9fr]'
              )}
            >
              <span></span>
              <span>Album</span>
              <span>Artist</span>
              <span class="hidden md:inline">Genre</span>
              <span class="hidden md:inline">Year</span>
              <span>Tracks</span>
              <span class="text-right">Size</span>
            </div>
            <div bind:this={listEl}>
              {#if topPad > 0}<div style="height: {topPad}px;" aria-hidden="true"></div>{/if}
              {#each visibleRows as row, ri (startRow + ri)}
                {#each row as album (album.key)}
                  <a
                    data-vrow
                    href={albumHref(album.key)}
                    class={cn(
                      'border-border hover:bg-accent/50 grid items-center gap-4 border-b px-3.5 py-2 text-xs last:border-b-0',
                      'grid-cols-[44px_2.2fr_1.4fr_1fr_0.8fr_0.8fr_0.9fr]'
                    )}
                  >
                    <Cover
                      artist={album.artist}
                      title={album.title}
                      coverUrl={album.coverUrl}
                      size={32}
                      corner={3}
                      caption={false}
                    />
                    <span class="truncate text-[12.5px] font-medium">{album.title}</span>
                    <span class="text-muted-foreground truncate">{album.artist}</span>
                    <span class="text-muted-foreground hidden truncate md:inline">{album.genre ?? '—'}</span>
                    <span class="text-muted-foreground hidden font-mono md:inline">{album.year ?? '—'}</span>
                    <span class="text-muted-foreground font-mono">
                      <Music class="-mt-0.5 mr-1 inline size-3" />{album.trackCount}
                    </span>
                    <span class="text-muted-foreground text-right font-mono">
                      {formatFileSize(album.byteSize)}
                    </span>
                  </a>
                {/each}
              {/each}
              {#if bottomPad > 0}<div style="height: {bottomPad}px;" aria-hidden="true"></div>{/if}
            </div>
          </div>
        {/if}

        {#if filtered.length > 0 && layout === 'grid'}
          <div class="text-muted-foreground mt-6 text-center text-[11px]">
            {filtered.length.toLocaleString()} album{filtered.length === 1 ? '' : 's'}
            {#if filtered.reduce((sum, a) => sum + a.durationSeconds, 0) > 0}
              · total {formatDuration(filtered.reduce((sum, a) => sum + a.durationSeconds, 0))}
            {/if}
          </div>
        {/if}
      {/if}
    </div>
  </ScrollArea>
{/if}

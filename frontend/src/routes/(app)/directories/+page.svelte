<script lang="ts">
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import FilterChip from '$lib/components/v2/FilterChip.svelte';
  import {
    fetchDirectoryMatchTree,
    setDirectoryExpectedLow,
    openProgressStream,
    type DirectoryMatchNode,
    type ProgressSnapshot
  } from '$lib/api-client';
  import DirectoryTreeRow from '$lib/components/directories/DirectoryTreeRow.svelte';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { Button } from '$lib/components/ui/button';
  import { SearchField } from '$lib/components/ui/search-field';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { cn } from '$lib/utils';
  import { Loader2, AlertTriangle, ChevronRight, ArrowUpDown, FolderSearch, FolderOpen } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { SvelteSet } from 'svelte/reactivity';

  // Match by folder. One scroller at every width: the nav bar (title, the match figure as its
  // subtitle, search, sort), then the status bar with a legend that names every colour, then the
  // folder tree. The filter chips stay reachable while it scrolls: on a phone in a strip pinned
  // under the nav bar, on a desktop in the bar's own chip band (one material, one hairline), with
  // the column header pinned under it like a list view's.

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  // Whether the phone's chip strip is pinned under the nav bar (see the markup): its sentinel has
  // scrolled out of the top of the page's scroller.
  let pageScroller = $state<HTMLElement | null>(null);
  let stripSentinel = $state<HTMLElement | null>(null);
  let stripStuck = $state(false);
  $effect(() => {
    const el = stripSentinel;
    const root = pageScroller;
    if (!el || !root) return;
    const io = new IntersectionObserver(([entry]) => {
      stripStuck =
        !entry.isIntersecting &&
        entry.rootBounds != null &&
        entry.boundingClientRect.top < entry.rootBounds.top;
    }, { root });
    io.observe(el);
    return () => {
      io.disconnect();
      stripStuck = false;
    };
  });

  let tree = $state<DirectoryMatchNode | null>(null);
  let isLoading = $state(true);
  let error = $state<string | null>(null);

  type FilterId = 'all' | 'review' | 'failed' | 'expected' | 'done';
  type SortId = 'match' | 'name' | 'size';
  let filter = $state<FilterId>('all');
  let sort = $state<SortId>('match');
  let query = $state('');

  // ── Live enrichment ─────────────────────────────────────────────────────────
  // Folders the user just kicked off enrichment for (drives a persistent "Enriching…"
  // state on the row). While the enrich step is running we re-fetch the tree on a
  // debounced cadence so folder match-bars move; `refreshToken` cascades to expanded
  // rows so their per-file pills update too.
  const enrichingPaths = new SvelteSet<string>();
  let refreshToken = $state(0);
  let liveCleanup: (() => void) | null = null;
  // Mirrors liveCleanup for the template: the "Live" badge shows only while the page really is
  // following an enrich run, rather than claiming to be live all the time.
  let liveActive = $state(false);
  let refreshTimer: ReturnType<typeof setTimeout> | null = null;
  let sawRunning = false;

  function lastSegment(path: string): string {
    const t = path.replace(/\\/g, '/').replace(/\/+$/, '');
    const i = t.lastIndexOf('/');
    const name = i >= 0 ? t.slice(i + 1) : t;
    return name || path;
  }

  async function refreshTree() {
    try {
      tree = await fetchDirectoryMatchTree();
      refreshToken += 1;
    } catch {
      // keep last good tree on transient failures
    }
  }

  function scheduleRefresh() {
    if (refreshTimer) return; // debounce: at most one refresh per window
    refreshTimer = setTimeout(() => {
      refreshTimer = null;
      void refreshTree();
    }, 3000);
  }

  function stopLive() {
    if (refreshTimer) {
      clearTimeout(refreshTimer);
      refreshTimer = null;
    }
    if (liveCleanup) {
      liveCleanup();
      liveCleanup = null;
    }
    liveActive = false;
  }

  async function finishLive() {
    stopLive();
    enrichingPaths.clear();
    sawRunning = false;
    await refreshTree();
  }

  function startLive() {
    if (liveCleanup) return;
    sawRunning = false;
    liveActive = true;
    liveCleanup = openProgressStream(
      (snap: ProgressSnapshot) => {
        if (snap.enrich?.status === 'Running') {
          sawRunning = true;
          scheduleRefresh();
        } else if (sawRunning || enrichingPaths.size > 0) {
          // Enrich finished (or was already done before we connected) — finalize once.
          void finishLive();
        }
      },
      () => {
        // Server closes the stream when the job completes; finalize rather than reconnect.
        liveCleanup = null;
        void finishLive();
      }
    );
  }

  function handleEnriched(path: string, count: number) {
    enrichingPaths.add(path);
    toast.success('Adding to library', {
      description: `${count.toLocaleString()} ${count === 1 ? 'track' : 'tracks'} under ${lastSegment(path)} queued`
    });
    startLive();
    scheduleRefresh();
  }

  // Optimistically flip the flag on the matching node so the row re-styles (and the filter
  // buckets recompute) instantly, then persist. No re-fetch needed — tagging changes no song
  // statuses, so the match rollups don't move; on failure we revert the optimistic flip.
  async function handleToggleExpected(path: string, next: boolean) {
    const node = findNode(tree, path);
    if (node) node.expectedLow = next;
    try {
      await setDirectoryExpectedLow(path, next);
      toast.success(next ? 'Marked as expected low' : 'Cleared expected-low tag', {
        description: lastSegment(path)
      });
    } catch (e) {
      if (node) node.expectedLow = !next; // revert on failure
      toast.error(e instanceof Error ? e.message : 'Failed to update folder');
    }
  }

  function findNode(node: DirectoryMatchNode | null, path: string): DirectoryMatchNode | null {
    if (!node) return null;
    if (node.path === path) return node;
    for (const child of node.children) {
      const found = findNode(child, path);
      if (found) return found;
    }
    return null;
  }

  $effect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const loaded = await fetchDirectoryMatchTree();
        if (!cancelled) tree = loaded;
      } catch (e) {
        if (!cancelled) error = e instanceof Error ? e.message : 'Failed to load directory tree';
      } finally {
        if (!cancelled) isLoading = false;
      }
    })();
    return () => {
      cancelled = true;
      stopLive();
    };
  });

  const total = $derived(tree?.total ?? 0);
  const written = $derived(tree?.done ?? 0);
  const matchedNotWritten = $derived(tree ? Math.max(0, tree.matched - tree.done) : 0);
  const enriched = $derived(tree?.matched ?? 0);
  const review = $derived(tree?.needsReview ?? 0);
  const failed = $derived(tree?.failed ?? 0);
  const queued = $derived(tree?.pending ?? 0);
  const matchedPct = $derived(tree && tree.total > 0 ? Math.round(tree.matchedPct) : 0);

  // Apple-Settings-style storage bar: in-library and matched share the accent family, the two
  // attention states get their status tokens, queued stays neutral. The legend under it names
  // every colour with its count (colour is never the only carrier), and the two attention
  // entries are the filter nudges.
  const heroSegs = $derived([
    { key: 'written', n: written, cls: 'bg-primary', label: 'In library', filter: null },
    { key: 'matched', n: matchedNotWritten, cls: 'bg-primary/50', label: 'Matched', filter: null },
    { key: 'review', n: review, cls: 'bg-warning', label: 'Needs review', filter: 'review' as const },
    { key: 'failed', n: failed, cls: 'bg-destructive', label: 'No match', filter: 'failed' as const },
    { key: 'queued', n: queued, cls: 'bg-muted-foreground-dim', label: 'Queued', filter: null }
  ]);

  const children = $derived(tree?.children ?? []);

  // A folder still needs work if it has review/failed rows or its match rate is below 90%.
  function hasReview(n: DirectoryMatchNode): boolean {
    return n.needsReview > 0;
  }
  function hasFailed(n: DirectoryMatchNode): boolean {
    return n.failed > 0;
  }
  function isDone(n: DirectoryMatchNode): boolean {
    return n.needsReview === 0 && n.failed === 0 && n.matchedPct >= 90;
  }

  const buckets = $derived.by(() => {
    const b = { all: 0, review: 0, failed: 0, expected: 0, done: 0 };
    for (const c of children) {
      b.all += 1;
      if (c.expectedLow) {
        b.expected += 1;
        continue;
      }
      if (hasReview(c)) b.review += 1;
      if (hasFailed(c)) b.failed += 1;
      if (isDone(c)) b.done += 1;
    }
    return b;
  });

  const visibleChildren = $derived.by(() => {
    let rows = children;
    const q = query.trim().toLowerCase();
    if (q) rows = rows.filter((c) => c.name.toLowerCase().includes(q) || c.path.toLowerCase().includes(q));

    if (filter === 'expected') rows = rows.filter((c) => c.expectedLow);
    else if (filter === 'review') rows = rows.filter((c) => !c.expectedLow && hasReview(c));
    else if (filter === 'failed') rows = rows.filter((c) => !c.expectedLow && hasFailed(c));
    else if (filter === 'done') rows = rows.filter((c) => !c.expectedLow && isDone(c));

    const sorted = [...rows];
    if (sort === 'match') sorted.sort((a, b) => a.matchedPct - b.matchedPct); // worst first
    else if (sort === 'name') sorted.sort((a, b) => a.name.localeCompare(b.name));
    else if (sort === 'size') sorted.sort((a, b) => b.sizeBytes - a.sizeBytes);
    return sorted;
  });

  const FILTERS = $derived(
    [
      { id: 'all' as const, label: 'All', n: buckets.all },
      { id: 'review' as const, label: 'Has reviews', n: buckets.review },
      { id: 'failed' as const, label: 'Has failures', n: buckets.failed, hideIfZero: true },
      { id: 'expected' as const, label: 'Expected low', n: buckets.expected },
      { id: 'done' as const, label: 'Done', n: buckets.done }
    ].filter((p) => !p.hideIfZero || p.n > 0)
  );

  const SORTS = [
    { id: 'match' as const, label: 'Worst match first' },
    { id: 'name' as const, label: 'Name' },
    { id: 'size' as const, label: 'Size' }
  ];
  const sortLabel = $derived(SORTS.find((s) => s.id === sort)?.label ?? 'Sort');

  // The subtitle says what the list is showing: the library figure normally, "N of M folders"
  // once a search or filter narrows it (the search field scrolls away on a phone, so this line
  // is where the narrowing stays visible).
  const narrowed = $derived(query.trim() !== '' || filter !== 'all');
  const headerMeta = $derived(
    isLoading || !tree
      ? undefined
      : narrowed
        ? `${visibleChildren.length.toLocaleString()} of ${children.length.toLocaleString()} folders · ${matchedPct}% enriched`
        : `${matchedPct}% enriched · ${enriched.toLocaleString()} of ${total.toLocaleString()} files`
  );
</script>

<!-- The chip set, in whichever band holds it (the phone's pinned strip, the desktop bar's band;
     both scroll sideways). -->
{#snippet filterChips()}
  <div role="group" aria-label="Show folders" class="flex shrink-0 items-center gap-2">
    {#each FILTERS as p (p.id)}
      <FilterChip pressed={filter === p.id} onclick={() => (filter = p.id)} count={p.n}>
        {p.label}
      </FilterChip>
    {/each}
  </div>
{/snippet}

<!-- One scroller at every width, the nav bar as its first child. -->
<div class="flex min-h-0 flex-1 flex-col">
  <ScrollArea bind:viewportRef={pageScroller} class="min-h-0 flex-1" viewportClass="overscroll-contain">
    <PageToolbarV2
      title="By folder"
      meta={headerMeta}
      metaFrom="lg"
      filters={!compact && !isLoading && tree ? filterChips : undefined}
    >
      {#snippet actions()}
        {#if liveActive}
          <span class="text-muted-foreground text-footnote md:text-nav-xs inline-flex shrink-0 items-center gap-1.5">
            <span class="bg-primary mh-v2-pulse size-1.5 rounded-full" aria-hidden="true"></span>
            Live
          </span>
        {/if}
        <DropdownMenu.Root>
          <DropdownMenu.Trigger>
            {#snippet child({ props })}
              <Button
                {...props}
                variant="gray"
                class="rounded-full"
                title="Sort folders"
                aria-label={`Sort folders: ${sortLabel}`}
              >
                <ArrowUpDown aria-hidden="true" />
                <span class="hidden md:inline">{sortLabel}</span>
              </Button>
            {/snippet}
          </DropdownMenu.Trigger>
          <DropdownMenu.Content align="end" class="min-w-52">
            <DropdownMenu.Label>Sort by</DropdownMenu.Label>
            <DropdownMenu.RadioGroup bind:value={sort}>
              {#each SORTS as s (s.id)}
                <DropdownMenu.RadioItem value={s.id}>{s.label}</DropdownMenu.RadioItem>
              {/each}
            </DropdownMenu.RadioGroup>
          </DropdownMenu.Content>
        </DropdownMenu.Root>
      {/snippet}
      {#snippet search()}
        <SearchField bind:value={query} label="Filter folders" />
      {/snippet}
    </PageToolbarV2>

    {#if !isLoading && tree}
      <!-- Slim segmented status bar; the headline number lives in the subtitle. -->
      <div class="px-4 pt-2 pb-3 md:px-7 md:pt-4">
        <div class="bg-muted flex h-1.5 w-full gap-px overflow-hidden rounded-full" aria-hidden="true">
          {#each heroSegs as s (s.key)}
            {#if s.n > 0}
              <span class={cn('h-full', s.cls)} style="width: {(s.n / Math.max(total, 1)) * 100}%"></span>
            {/if}
          {/each}
        </div>

        <!-- The legend: every colour with its word and count. The two attention states set the
             filter below (the old "N files need your review" nudges). -->
        <ul class="text-footnote md:text-nav-xs mt-2.5 flex flex-wrap items-center gap-x-4 gap-y-2">
          {#each heroSegs as s (s.key)}
            {#if s.n > 0}
              <li>
                {#if s.filter}
                  {@const target = s.filter}
                  <!-- Named by its visible words ("Needs review 10"), so Voice Control's "tap
                       Needs review" finds it; the sr-only tail says what the tap does. The
                       pseudo-element takes the 18px line to a 44pt target. -->
                  <button
                    type="button"
                    onclick={() => (filter = target)}
                    class="text-foreground focus-visible:ring-ring/50 relative inline-flex items-center gap-1.5 rounded-sm font-medium outline-none hover:underline focus-visible:ring-3 pointer-coarse:after:absolute pointer-coarse:after:-inset-x-1.5 pointer-coarse:after:-inset-y-[13px]"
                  >
                    <span class={cn('size-2 shrink-0 rounded-full', s.cls)} aria-hidden="true"></span>
                    {s.label}
                    <span class="tabular-nums">{s.n.toLocaleString()}</span>
                    <span class="sr-only">
                      {s.key === 'review' ? ' — show folders with reviews' : ' — show folders with failures'}
                    </span>
                    <ChevronRight class="text-muted-foreground size-3.5" aria-hidden="true" />
                  </button>
                {:else}
                  <span class="text-muted-foreground inline-flex items-center gap-1.5">
                    <span class={cn('size-2 shrink-0 rounded-full', s.cls)} aria-hidden="true"></span>
                    {s.label}
                    <span class="text-foreground tabular-nums">{s.n.toLocaleString()}</span>
                  </span>
                {/if}
              </li>
            {/if}
          {/each}
        </ul>
      </div>

      {#if compact}
        <!-- Phone: the chips in a strip pinned under the nav bar (--mh-navbar-h, which the bar
             publishes on this scroller) while the tree scrolls beneath it. The bar has no fill
             of its own here — only the scroll edge, which fades out over its lower part — so the
             strip carries an opaque band up under that fade, or the folder scrolling past would
             show through between the bar's title and the chips. Only while it is pinned: at
             rest the band would sit over the legend's last line and clip it. -->
        <!-- Where the strip would be if it did not stick, raised by the bar's height: once this
             leaves the top of the scroller, the strip is pinned. -->
        <div
          bind:this={stripSentinel}
          aria-hidden="true"
          class="pointer-events-none relative -mb-px h-px"
          style="top: calc(-1 * var(--mh-navbar-h, 0px))"
        ></div>
        <div
          data-stuck={stripStuck || undefined}
          class="bg-background border-separator before:bg-background sticky top-(--mh-navbar-h,0px) z-10 border-b before:pointer-events-none before:absolute before:inset-x-0 before:bottom-full before:hidden before:h-4 data-stuck:before:block"
        >
          <div data-scroll-x="" class="no-scrollbar flex items-center overflow-x-auto px-4 py-1.5">
            {@render filterChips()}
          </div>
        </div>
      {:else if children.length > 0}
        <!-- Desktop: the chips ride in the bar's band; the column header pins under the bar like
             a list view's, with the hairline that separates it from the rows. -->
        <div
          class="bg-background border-separator text-muted-foreground sticky top-(--mh-navbar-h,0px) z-10 flex items-center gap-2 border-b px-6 pt-1 pb-1.5 text-[11px] font-medium"
          aria-hidden="true"
        >
          <span class="w-3.5 shrink-0"></span>
          <span class="flex-1">Folder</span>
          <span class="w-28 shrink-0 text-center">Status</span>
          <span class="w-16 shrink-0 text-right">Count</span>
          <span class="w-10 shrink-0 text-right">Match</span>
          <span class="w-[104px] shrink-0"></span>
        </div>
      {/if}
    {/if}

    <div class="px-2 py-2 md:px-4">
      {#if isLoading}
        <div class="text-muted-foreground text-body flex min-h-[40vh] items-center justify-center gap-2 md:text-sm">
          <Loader2 class="size-4 animate-spin" aria-hidden="true" />
          Loading directory tree…
        </div>
      {:else if error}
        <div class="text-muted-foreground text-body flex min-h-[40vh] flex-col items-center justify-center gap-2 px-6 text-center md:text-sm">
          <AlertTriangle class="text-warning-text size-5" aria-hidden="true" />
          {error}
        </div>
      {:else if tree && children.length > 0}
        {#if visibleChildren.length > 0}
          {#each visibleChildren as child (child.path)}
            <DirectoryTreeRow
              node={child}
              depth={0}
              {enrichingPaths}
              {refreshToken}
              onEnriched={handleEnriched}
              onToggleExpected={handleToggleExpected}
            />
          {/each}
        {:else}
          <!-- A dead end always offers the way back out: clearing whichever narrowed it. -->
          <EmptyState
            icon={FolderSearch}
            title={query.trim() ? 'No matching folders' : 'No folders in this filter'}
            hint={query.trim()
              ? `Nothing here matches “${query.trim()}”.`
              : 'Pick another filter to see the rest.'}
            action={query.trim()
              ? { label: 'Clear search', onclick: () => (query = '') }
              : { label: 'Show all', onclick: () => (filter = 'all') }}
          />
        {/if}
      {:else}
        <EmptyState
          icon={FolderOpen}
          title="No tracks indexed yet"
          hint="Folders appear here once a scan has read the source library."
        />
      {/if}
    </div>
  </ScrollArea>
</div>

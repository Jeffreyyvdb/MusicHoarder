<script lang="ts">
  import {
    fetchDirectoryMatchTree,
    setDirectoryExpectedLow,
    openProgressStream,
    type DirectoryMatchNode,
    type ProgressSnapshot
  } from '$lib/api-client';
  import DirectoryTreeRow from '$lib/components/directories/DirectoryTreeRow.svelte';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { cn } from '$lib/utils';
  import { Loader2, AlertTriangle, Search, ChevronRight, ArrowUpDown, Check } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { SvelteSet } from 'svelte/reactivity';

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

  // Apple-Settings-style storage bar: in-library and matched share the accent family,
  // attention states get their semantic hue, queued stays neutral track gray.
  const heroSegs = $derived([
    { key: 'written', n: written, cls: 'bg-primary', label: 'in library' },
    { key: 'matched', n: matchedNotWritten, cls: 'bg-primary/50', label: 'matched' },
    { key: 'review', n: review, cls: 'bg-amber-500', label: 'needs review' },
    { key: 'failed', n: failed, cls: 'bg-red-500', label: 'no match' },
    { key: 'queued', n: queued, cls: 'bg-muted-foreground/25', label: 'queued' }
  ]);
  const barTitle = $derived(
    heroSegs
      .filter((s) => s.n > 0)
      .map((s) => `${s.n.toLocaleString()} ${s.label}`)
      .join(' · ')
  );

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
</script>

<!-- On mobile the whole thing scrolls (header + hero scroll away, filters stay
     pinned); on desktop it's a flex column with only the folder list scrolling. -->
<div class="flex min-h-0 flex-1 flex-col overflow-y-auto pb-[var(--mh-content-pad)] sm:overflow-hidden sm:pb-0">
  <!-- Header -->
  <header class="flex shrink-0 flex-col gap-1 px-4 pt-5 pb-1 sm:px-6">
    <div class="flex flex-wrap items-baseline gap-2">
      <h1 class="text-xl font-semibold tracking-tight">Match by folder</h1>
      <span class="text-muted-foreground ml-auto inline-flex items-center gap-1.5 text-[11px]">
        <span class="bg-primary mh-v2-pulse size-1.5 rounded-full"></span>
        Live
      </span>
    </div>
    <p class="text-muted-foreground hidden max-w-[760px] text-[13px] leading-relaxed sm:block">
      Everything in
      {#if tree}
        <span class="text-foreground/80 font-medium" title={tree.name}>{lastSegment(tree.name)}</span>,
      {:else}
        your source directory,
      {/if}
      grouped by folder. Drill in for per-file state and destination, or tag a folder as
      <span class="text-foreground/80 font-medium">expected low</span> to keep leaks and unreleased recordings out
      of your work queue.
    </p>
  </header>

  {#if !isLoading && tree}
    <!-- Unboxed hero: neutral headline number + slim segmented storage bar -->
    <div class="shrink-0 px-4 pt-4 pb-5 sm:px-6">
      <div class="flex flex-wrap items-baseline gap-x-2.5 gap-y-1">
        <span class="text-[30px] leading-none font-semibold tracking-tight tabular-nums sm:text-[36px]"
          >{matchedPct}<span class="text-muted-foreground ml-0.5 text-lg font-medium">%</span></span
        >
        <span class="text-muted-foreground text-[13px]">
          enriched · <span class="text-foreground/80 font-medium tabular-nums">{enriched.toLocaleString()}</span> of
          <span class="text-foreground/80 font-medium tabular-nums">{total.toLocaleString()}</span> files
        </span>
      </div>

      <div class="bg-muted mt-3.5 flex h-1.5 w-full gap-px overflow-hidden rounded-full" title={barTitle}>
        {#each heroSegs as s (s.key)}
          {#if s.n > 0}
            <span class={cn('h-full', s.cls)} style="width: {(s.n / Math.max(total, 1)) * 100}%"></span>
          {/if}
        {/each}
      </div>

      <!-- Attention states only — quiet inline nudges instead of a tinted banner -->
      {#if review > 0 || failed > 0}
        <div class="mt-3 flex flex-wrap items-center gap-x-5 gap-y-1.5 text-[12.5px]">
          {#if review > 0}
            <button
              type="button"
              onclick={() => (filter = 'review')}
              class="text-muted-foreground hover:text-foreground group inline-flex items-center gap-1.5 transition-colors"
            >
              <span class="size-1.5 shrink-0 rounded-full bg-amber-500"></span>
              <span
                ><span class="text-foreground font-medium tabular-nums">{review.toLocaleString()}</span>
                {review === 1 ? 'file needs' : 'files need'} your review</span
              >
              <ChevronRight
                class="size-3 opacity-50 transition-transform group-hover:translate-x-px motion-reduce:transition-none"
              />
            </button>
          {/if}
          {#if failed > 0}
            <button
              type="button"
              onclick={() => (filter = 'failed')}
              class="text-muted-foreground hover:text-foreground group inline-flex items-center gap-1.5 transition-colors"
            >
              <span class="size-1.5 shrink-0 rounded-full bg-red-500"></span>
              <span
                ><span class="text-foreground font-medium tabular-nums">{failed.toLocaleString()}</span> matched nothing</span
              >
              <ChevronRight
                class="size-3 opacity-50 transition-transform group-hover:translate-x-px motion-reduce:transition-none"
              />
            </button>
          {/if}
        </div>
      {/if}
    </div>

    <!-- One control cluster: segmented filter + search + sort menu.
         Pinned on mobile while the header/hero scroll away. -->
    <div
      class="border-border/60 bg-background sticky top-0 z-10 flex shrink-0 flex-wrap items-center justify-between gap-x-3 gap-y-2 border-b px-4 py-2 sm:static sm:z-auto sm:px-6 sm:py-2.5"
    >
      <div class="no-scrollbar bg-muted/70 flex max-w-full items-center gap-0.5 overflow-x-auto rounded-full p-0.5">
        {#each FILTERS as p (p.id)}
          <button
            type="button"
            onclick={() => (filter = p.id)}
            class={cn(
              'inline-flex shrink-0 items-center gap-1.5 rounded-full px-3 py-1 text-[12.5px] whitespace-nowrap transition-colors',
              filter === p.id
                ? 'bg-card text-foreground shadow-sm'
                : 'text-muted-foreground hover:text-foreground'
            )}
            aria-pressed={filter === p.id}
          >
            {p.label}
            <span class="text-muted-foreground text-[11.5px] tabular-nums">{p.n.toLocaleString()}</span>
          </button>
        {/each}
      </div>
      <div class="flex items-center gap-1">
        <div
          class="bg-muted/60 focus-within:bg-muted flex h-7 items-center gap-1.5 rounded-full px-2.5 transition-colors"
        >
          <Search class="text-muted-foreground size-3.5 shrink-0" />
          <input
            placeholder="Filter folders"
            bind:value={query}
            class="placeholder:text-muted-foreground w-28 bg-transparent text-xs outline-none transition-[width] duration-200 focus:w-44 motion-reduce:transition-none sm:w-32"
          />
        </div>
        <DropdownMenu.Root>
          <DropdownMenu.Trigger>
            {#snippet child({ props })}
              <button
                {...props}
                type="button"
                class="text-muted-foreground hover:bg-muted/60 hover:text-foreground inline-flex h-7 items-center gap-1.5 rounded-full px-2.5 text-xs transition-colors"
                title="Sort folders"
              >
                <ArrowUpDown class="size-3.5" />
                <span class="hidden md:inline">{sortLabel}</span>
              </button>
            {/snippet}
          </DropdownMenu.Trigger>
          <DropdownMenu.Content align="end" class="min-w-44">
            {#each SORTS as s (s.id)}
              <DropdownMenu.Item onSelect={() => (sort = s.id)} class="justify-between">
                {s.label}
                {#if sort === s.id}
                  <Check class="text-muted-foreground size-4" />
                {/if}
              </DropdownMenu.Item>
            {/each}
          </DropdownMenu.Content>
        </DropdownMenu.Root>
      </div>
    </div>
  {/if}

  <!-- Table — natural height on mobile (outer scrolls), inner scroller on desktop -->
  <div class="px-3 py-2 sm:min-h-0 sm:flex-1 sm:overflow-y-auto sm:px-4">
    {#if isLoading}
      <div class="text-muted-foreground flex min-h-[40vh] items-center justify-center gap-2 text-sm sm:h-full sm:min-h-0">
        <Loader2 class="size-4 animate-spin" />
        Loading directory tree…
      </div>
    {:else if error}
      <div class="text-muted-foreground flex min-h-[40vh] flex-col items-center justify-center gap-2 text-sm sm:h-full sm:min-h-0">
        <AlertTriangle class="size-5 text-amber-500" />
        {error}
      </div>
    {:else if tree && children.length > 0}
      <div class="text-muted-foreground mt-1 mb-1.5 flex items-center gap-2 px-2 text-[11px] font-medium">
        <span class="w-3.5 shrink-0"></span>
        <span class="flex-1">Folder</span>
        <span class="hidden w-28 shrink-0 text-center sm:block">Status</span>
        <span class="hidden w-16 shrink-0 text-right sm:block">Count</span>
        <span class="w-10 shrink-0 text-right">Match</span>
        <span class="w-[104px] shrink-0"></span>
      </div>
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
        <div class="text-muted-foreground flex h-32 items-center justify-center text-sm">
          No folders in this filter.
        </div>
      {/if}
    {:else}
      <div class="text-muted-foreground flex min-h-[40vh] items-center justify-center text-sm sm:h-full sm:min-h-0">
        No songs indexed yet.
      </div>
    {/if}
  </div>
</div>

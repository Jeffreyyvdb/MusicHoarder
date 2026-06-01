<script lang="ts">
  import {
    fetchDirectoryMatchTree,
    setDirectoryExpectedLow,
    openProgressStream,
    type DirectoryMatchNode,
    type ProgressSnapshot
  } from '$lib/api-client';
  import DirectoryTreeRow from '$lib/components/directories/DirectoryTreeRow.svelte';
  import { cn } from '$lib/utils';
  import { Folder, Loader2, AlertTriangle, Search, Sparkles, ChevronRight } from '@lucide/svelte';
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
  const review = $derived(tree?.needsReview ?? 0);
  const failed = $derived(tree?.failed ?? 0);
  const queued = $derived(tree?.pending ?? 0);
  const matchedPct = $derived(tree && tree.total > 0 ? Math.round(tree.matchedPct) : 0);

  const heroSegs = $derived([
    { key: 'written', n: written, cls: 'bg-emerald-600' },
    { key: 'matched', n: matchedNotWritten, cls: 'bg-emerald-500' },
    { key: 'review', n: review, cls: 'bg-amber-500' },
    { key: 'failed', n: failed, cls: 'bg-red-500' },
    { key: 'queued', n: queued, cls: 'bg-slate-400/60' }
  ]);
  const legend = $derived([
    { key: 'lib', label: 'in library', n: written, dot: 'bg-emerald-600' },
    { key: 'matched', label: 'matched', n: matchedNotWritten, dot: 'bg-emerald-500' },
    { key: 'review', label: 'review', n: review, dot: 'bg-amber-500' },
    { key: 'failed', label: 'failed', n: failed, dot: 'bg-red-500' },
    { key: 'queued', label: 'queued', n: queued, dot: 'bg-slate-400/60 ring-border ring-1' }
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

  const pctClass = (p: number) =>
    p >= 90
      ? 'text-emerald-600 dark:text-emerald-400'
      : p >= 60
        ? 'text-amber-600 dark:text-amber-400'
        : 'text-red-600 dark:text-red-400';

  const FILTERS = $derived(
    [
      { id: 'all' as const, label: 'All folders', n: buckets.all, tone: '' },
      { id: 'review' as const, label: 'Has reviews', n: buckets.review, tone: 'review' },
      { id: 'failed' as const, label: 'Has failures', n: buckets.failed, tone: 'fail', hideIfZero: true },
      { id: 'expected' as const, label: 'Expected low', n: buckets.expected, tone: 'muted' },
      { id: 'done' as const, label: 'Done', n: buckets.done, tone: 'ok' }
    ].filter((p) => !p.hideIfZero || p.n > 0)
  );

  function pillCountClass(active: boolean, tone: string): string {
    if (!active) return 'bg-muted text-muted-foreground';
    switch (tone) {
      case 'review':
        return 'bg-amber-500/15 text-amber-600 dark:text-amber-400';
      case 'fail':
        return 'bg-red-500/10 text-red-600 dark:text-red-400';
      case 'ok':
        return 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400';
      case 'muted':
        return 'bg-muted text-muted-foreground';
      default:
        return 'bg-primary/10 text-primary';
    }
  }
</script>

<div class="flex h-full min-h-0 flex-col">
  <!-- Header -->
  <header class="border-border flex shrink-0 flex-col gap-1.5 border-b px-4 py-4 sm:px-5">
    <div class="flex flex-wrap items-center gap-2">
      <Folder class="text-primary size-4 shrink-0" />
      <h1 class="text-[15px] font-semibold">Match by folder</h1>
      {#if tree}
        <span
          class="bg-muted text-muted-foreground border-border min-w-0 max-w-[40vw] truncate rounded border px-1.5 py-0.5 font-mono text-[11px]"
          title={tree.name}
        >
          {tree.name}
        </span>
      {/if}
      <span class="text-muted-foreground ml-auto inline-flex items-center gap-1.5 font-mono text-[10.5px]">
        <span class="bg-primary mh-pulse-dot size-1.5 rounded-full"></span>
        live
      </span>
    </div>
    <p class="text-muted-foreground max-w-[880px] text-[12.5px] leading-relaxed">
      Files in your source directory, grouped by folder. Drill into a folder to see its sub-folders and the
      per-file enrichment state and destination. Tag a folder as <b class="text-foreground/80 font-medium"
        >expected low</b
      > for leaks, unreleased, or recordings that aren't in public databases — they'll stop showing up in your work queue.
    </p>
  </header>

  {#if !isLoading && tree}
    <!-- Hero segmented bar -->
    <div class="border-border shrink-0 border-b px-4 py-3.5 sm:px-5">
      <div class="bg-card border-border flex flex-col gap-4 rounded-xl border px-4 py-3.5 sm:flex-row sm:items-center sm:gap-7">
        <div class="flex shrink-0 items-end gap-3 sm:flex-col sm:items-start sm:gap-0.5">
          <div class="flex items-baseline">
            <span class={cn('text-[34px] font-semibold leading-none tracking-tight tabular-nums', pctClass(matchedPct))}>{matchedPct}</span>
            <span class={cn('ml-0.5 text-base font-medium', pctClass(matchedPct))}>%</span>
          </div>
          <div class="text-muted-foreground pb-1 font-mono text-[11px] whitespace-nowrap sm:pb-0">
            enriched · <b class="text-foreground/80 font-medium">{total.toLocaleString()}</b> files total
          </div>
        </div>
        <div class="min-w-0 flex-1 space-y-2">
          <div class="bg-muted flex h-2.5 w-full overflow-hidden rounded-full">
            {#each heroSegs as s (s.key)}
              {#if s.n > 0}
                <span class={cn('h-full', s.cls)} style="width: {(s.n / Math.max(total, 1)) * 100}%"></span>
              {/if}
            {/each}
          </div>
          <div class="flex flex-wrap gap-x-4 gap-y-1 font-mono text-[11px]">
            {#each legend as l (l.key)}
              {#if l.n > 0}
                <span class="inline-flex items-center gap-1.5">
                  <span class={cn('size-2 rounded-[2px]', l.dot)}></span>
                  <span class="text-foreground font-medium">{l.n.toLocaleString()}</span>
                  <span class="text-muted-foreground">{l.label}</span>
                </span>
              {/if}
            {/each}
          </div>
        </div>
      </div>

      <!-- Review banner — primary action -->
      {#if review > 0}
        <button
          type="button"
          onclick={() => (filter = 'review')}
          class="mt-3 flex w-full items-center gap-3 rounded-lg border border-amber-500/30 bg-amber-500/10 px-4 py-2.5 text-left transition-colors hover:bg-amber-500/15"
        >
          <span class="grid size-7 shrink-0 place-items-center rounded-md bg-amber-500 text-white">
            <Sparkles class="size-3.5" />
          </span>
          <div class="min-w-0 flex-1">
            <div class="text-[13px]">
              <b class="font-semibold text-amber-700 dark:text-amber-400">{review.toLocaleString()}</b> files need your review
            </div>
            <div class="text-muted-foreground hidden font-mono text-[11px] sm:block">
              Borderline confidence matches — confirm or pick the right release before they land.
            </div>
          </div>
          <span class="inline-flex shrink-0 items-center gap-1 rounded-md bg-amber-600 px-2.5 py-1.5 text-[12px] font-medium text-white">
            Review <ChevronRight class="size-3" />
          </span>
        </button>
      {/if}
    </div>

    <!-- Filter pills + search + sort -->
    <div class="border-border flex shrink-0 flex-wrap items-center justify-between gap-3 border-b px-4 py-2 sm:px-5">
      <div class="flex flex-wrap items-center gap-1">
        {#each FILTERS as p (p.id)}
          <button
            type="button"
            onclick={() => (filter = p.id)}
            class={cn(
              'inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-[12px] transition-colors',
              filter === p.id
                ? 'bg-card border-border text-foreground shadow-sm'
                : 'text-muted-foreground hover:bg-muted/60 border-transparent'
            )}
          >
            <span>{p.label}</span>
            <span class={cn('rounded px-1.5 py-px font-mono text-[10.5px] tabular-nums', pillCountClass(filter === p.id, p.tone))}>{p.n}</span>
          </button>
        {/each}
      </div>
      <div class="flex items-center gap-3">
        <div
          class="bg-muted/60 border-border focus-within:border-primary flex items-center gap-1.5 rounded-md border px-2 py-1 transition-colors"
        >
          <Search class="text-muted-foreground size-3" />
          <input
            placeholder="filter folders…"
            bind:value={query}
            class="placeholder:text-muted-foreground w-28 bg-transparent font-mono text-[11.5px] outline-none sm:w-32"
          />
        </div>
        <div class="text-muted-foreground hidden items-center gap-1 text-[10.5px] sm:flex">
          <span>sort:</span>
          {#each [{ id: 'match' as const, label: 'match %' }, { id: 'name' as const, label: 'name' }, { id: 'size' as const, label: 'size' }] as s (s.id)}
            <button
              type="button"
              onclick={() => (sort = s.id)}
              class={cn(
                'rounded px-1.5 py-0.5 transition-colors',
                sort === s.id ? 'bg-card text-foreground border-border border' : 'hover:text-foreground'
              )}>{s.label}</button
            >
          {/each}
        </div>
      </div>
    </div>
  {/if}

  <!-- Table -->
  <div class="min-h-0 flex-1 overflow-y-auto px-3 py-2 sm:px-4">
    {#if isLoading}
      <div class="text-muted-foreground flex h-full items-center justify-center gap-2 text-sm">
        <Loader2 class="size-4 animate-spin" />
        Loading directory tree…
      </div>
    {:else if error}
      <div class="text-muted-foreground flex h-full flex-col items-center justify-center gap-2 text-sm">
        <AlertTriangle class="size-5 text-amber-500" />
        {error}
      </div>
    {:else if tree && children.length > 0}
      <div
        class="text-muted-foreground mb-1 flex items-center gap-2 px-2 text-[10px] font-medium tracking-wider uppercase"
      >
        <span class="w-3.5 shrink-0"></span>
        <span class="flex-1">Folder</span>
        <span class="hidden w-28 shrink-0 text-center sm:block">Status</span>
        <span class="hidden w-16 shrink-0 text-right sm:block">Count</span>
        <span class="w-10 shrink-0 text-right">Match</span>
        <span class="w-[88px] shrink-0"></span>
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
      <div class="text-muted-foreground flex h-full items-center justify-center text-sm">
        No songs indexed yet.
      </div>
    {/if}
  </div>
</div>

<style>
  :global(.mh-pulse-dot) {
    box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    animation: mh-pulse-dot 2s infinite;
  }
  @keyframes mh-pulse-dot {
    0% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    }
    70% {
      box-shadow: 0 0 0 5px oklch(0.5 0.17 145 / 0);
    }
    100% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0);
    }
  }
</style>

<script lang="ts">
  import {
    fetchDirectoryMatchTree,
    openProgressStream,
    type DirectoryMatchNode,
    type ProgressSnapshot
  } from '$lib/api-client';
  import DirectoryTreeRow from '$lib/components/directories/DirectoryTreeRow.svelte';
  import { cn } from '$lib/utils';
  import { FolderTree, Loader2, AlertTriangle } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { SvelteSet } from 'svelte/reactivity';

  let tree = $state<DirectoryMatchNode | null>(null);
  let isLoading = $state(true);
  let error = $state<string | null>(null);

  type FilterId = 'all' | 'needs-action' | 'done';
  type SortId = 'match' | 'name' | 'size';
  let filter = $state<FilterId>('all');
  let sort = $state<SortId>('match');

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
    toast.success('Enrichment started', {
      description: `${count.toLocaleString()} ${count === 1 ? 'track' : 'tracks'} under ${lastSegment(path)} queued`
    });
    startLive();
    scheduleRefresh();
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
  const inLibrary = $derived(tree?.done ?? 0);
  const queued = $derived(tree?.pending ?? 0);
  const review = $derived(tree?.needsReview ?? 0);
  const failed = $derived(tree?.failed ?? 0);
  const matchedPct = $derived(tree && tree.total > 0 ? Math.round(tree.matchedPct) : 0);

  // A folder still needs attention if it has review/failed rows or its match rate is below 90%.
  function needsAction(node: DirectoryMatchNode): boolean {
    return node.needsReview > 0 || node.failed > 0 || node.matchedPct < 90;
  }

  const children = $derived(tree?.children ?? []);
  const needsActionCount = $derived(children.filter(needsAction).length);
  const doneCount = $derived(children.length - needsActionCount);

  const visibleChildren = $derived.by(() => {
    let rows = children;
    if (filter === 'needs-action') rows = rows.filter(needsAction);
    else if (filter === 'done') rows = rows.filter((c) => !needsAction(c));

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
</script>

<div class="flex h-full min-h-0 flex-col">
  <header class="border-border flex shrink-0 flex-col gap-1 border-b px-5 py-4">
    <div class="flex items-center gap-2">
      <FolderTree class="text-muted-foreground size-4" />
      <h1 class="text-[15px] font-semibold">Match by folder</h1>
      {#if tree}
        <span
          class="bg-muted text-muted-foreground border-border ml-1 truncate rounded border px-1.5 py-0.5 font-mono text-[11px]"
          title={tree.name}
        >
          {tree.name}
        </span>
      {/if}
    </div>
    <p class="text-muted-foreground text-[12.5px]">
      Files in your source directory, grouped by folder. Drill into a folder to see its sub-folders
      and the per-file enrichment state and destination. Well-tagged folders that simply aren't in
      the public databases (leaks, unreleased) will show low match rates that are expected.
    </p>
  </header>

  {#if !isLoading && tree}
    <!-- Inline stats row -->
    <div
      class="border-border flex shrink-0 flex-wrap items-baseline gap-x-5 gap-y-2 border-b px-5 py-2.5 text-[12px]"
    >
      <span class="flex items-baseline gap-1.5 whitespace-nowrap">
        <b class={cn('font-mono text-lg font-semibold tabular-nums', pctClass(matchedPct))}>{matchedPct}%</b>
        <span class="text-muted-foreground">enriched overall</span>
      </span>
      <span class="text-muted-foreground whitespace-nowrap">
        <b class="text-foreground font-mono font-medium tabular-nums">{inLibrary.toLocaleString()}</b> in library
      </span>
      <span class="text-muted-foreground whitespace-nowrap">
        <b class="text-foreground font-mono font-medium tabular-nums">{queued.toLocaleString()}</b> queued
      </span>
      <span class="whitespace-nowrap text-amber-600 dark:text-amber-400">
        <b class="font-mono font-medium tabular-nums">{review.toLocaleString()}</b> review
      </span>
      <span class="whitespace-nowrap text-red-600 dark:text-red-400">
        <b class="font-mono font-medium tabular-nums">{failed.toLocaleString()}</b> failed
      </span>
      <span class="text-muted-foreground/70 whitespace-nowrap">·</span>
      <span class="text-muted-foreground/70 whitespace-nowrap">
        <b class="text-muted-foreground font-mono font-medium tabular-nums">{total.toLocaleString()}</b> total
      </span>
    </div>

    <!-- Filter pills + sort -->
    <div class="border-border flex shrink-0 flex-wrap items-center justify-between gap-3 border-b px-5 py-2">
      <div class="flex gap-1">
        {#each [{ id: 'all', label: 'All folders', n: children.length }, { id: 'needs-action', label: 'Needs action', n: needsActionCount }, { id: 'done', label: 'Done', n: doneCount }] as p (p.id)}
          <button
            type="button"
            onclick={() => (filter = p.id as FilterId)}
            class={cn(
              'inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-[12px] transition-colors',
              filter === p.id
                ? 'bg-background border-border text-foreground'
                : 'text-muted-foreground hover:bg-muted/60 border-transparent'
            )}
          >
            <span>{p.label}</span>
            <span
              class={cn(
                'rounded px-1.5 py-px font-mono text-[10.5px] tabular-nums',
                filter === p.id ? 'bg-primary/10 text-primary' : 'bg-muted text-muted-foreground'
              )}>{p.n}</span
            >
          </button>
        {/each}
      </div>
      <div class="text-muted-foreground flex items-center gap-1 text-[10.5px]">
        <span>sort:</span>
        {#each [{ id: 'match', label: 'match %' }, { id: 'name', label: 'name' }, { id: 'size', label: 'size' }] as s (s.id)}
          <button
            type="button"
            onclick={() => (sort = s.id as SortId)}
            class={cn(
              'rounded px-1.5 py-0.5 transition-colors',
              sort === s.id ? 'bg-muted text-foreground' : 'hover:text-foreground'
            )}>{s.label}</button
          >
        {/each}
      </div>
    </div>
  {/if}

  <div class="min-h-0 flex-1 overflow-y-auto px-3 py-2">
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
        class="text-muted-foreground mb-1 flex items-center gap-2 px-2 text-[10.5px] font-medium tracking-wide uppercase"
      >
        <span class="flex-1">Folder</span>
        <span class="hidden w-28 text-center sm:block">Status</span>
        <span class="w-10 text-right">Match</span>
        <span class="w-20 text-right">Unmatched</span>
      </div>
      {#if visibleChildren.length > 0}
        {#each visibleChildren as child (child.path)}
          <DirectoryTreeRow
            node={child}
            depth={0}
            {enrichingPaths}
            {refreshToken}
            onEnriched={handleEnriched}
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

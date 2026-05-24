<script lang="ts">
  import {
    enrichFolder,
    fetchFolderFiles,
    type DirectoryMatchNode,
    type SourceFile
  } from '$lib/api-client';
  import { AlertCircle, ChevronRight, Loader2, Sparkles } from '@lucide/svelte';
  import { cleanDisplayName } from '$lib/formatters';
  import { cn } from '$lib/utils';
  import { Button } from '$lib/components/ui/button';
  import Self from './DirectoryTreeRow.svelte';
  import SourceFileRow from './SourceFileRow.svelte';

  let {
    node,
    depth = 0,
    enrichingPaths,
    refreshToken = 0,
    onEnriched
  }: {
    node: DirectoryMatchNode;
    depth?: number;
    enrichingPaths?: Set<string>;
    refreshToken?: number;
    onEnriched?: (path: string, count: number) => void;
  } = $props();

  let expanded = $state(false);
  const hasChildren = $derived(node.children.length > 0);
  const hasFiles = $derived(node.directFileCount > 0);
  const expandable = $derived(hasChildren || hasFiles);

  // Lazily-loaded files that live directly in this folder.
  let files = $state<SourceFile[] | null>(null);
  let filesState = $state<'idle' | 'loading' | 'error'>('idle');

  let enrichState = $state<'idle' | 'loading' | 'error'>('idle');

  // Persistent "Enriching…" while the request is in flight OR this folder's enrich job is live.
  const isEnriching = $derived(enrichState === 'loading' || (enrichingPaths?.has(node.path) ?? false));

  // While enrichment runs the parent bumps `refreshToken`; silently refresh this folder's loaded
  // files so per-file state pills track the live progress (no loading-spinner flicker).
  $effect(() => {
    void refreshToken;
    if (expanded && files !== null && filesState !== 'loading') {
      void silentReloadFiles();
    }
  });

  // Segment widths for the stacked status bar, as a share of this folder's total.
  function pct(n: number): number {
    return node.total > 0 ? (n / node.total) * 100 : 0;
  }

  // `matched` from the API includes rows already written to the library (`done`); split them so the
  // bar shows "in library" distinctly from "matched, not yet written" — matching the design.
  const written = $derived(node.done);
  const matchedNotWritten = $derived(Math.max(0, node.matched - node.done));

  const matchedPctLabel = $derived(node.total > 0 ? Math.round(node.matchedPct) : 0);

  function toggle() {
    if (!expandable) return;
    expanded = !expanded;
    if (expanded && hasFiles && files === null && filesState !== 'loading') {
      void loadFiles();
    }
  }

  async function loadFiles() {
    filesState = 'loading';
    try {
      files = await fetchFolderFiles(node.path);
      filesState = 'idle';
    } catch {
      filesState = 'error';
    }
  }

  async function silentReloadFiles() {
    try {
      files = await fetchFolderFiles(node.path);
    } catch {
      // keep the last good list during live polling
    }
  }

  async function handleEnrichFolder() {
    enrichState = 'loading';
    try {
      const result = await enrichFolder(node.path);
      enrichState = 'idle';
      onEnriched?.(node.path, result.enqueued);
    } catch {
      enrichState = 'error';
      setTimeout(() => (enrichState = 'idle'), 5000);
    }
  }
</script>

<div class="select-none">
  <div class="group relative flex items-center">
  <button
    type="button"
    onclick={toggle}
    class={cn(
      'flex w-full items-center gap-2 rounded-md py-1.5 pr-2 text-left text-[13px] transition-colors',
      expandable ? 'hover:bg-muted/60 cursor-pointer' : 'cursor-default'
    )}
    style="padding-left: {depth * 18 + 6}px"
    aria-expanded={expandable ? expanded : undefined}
  >
    <ChevronRight
      class={cn(
        'size-3.5 shrink-0 transition-transform',
        expandable ? 'text-muted-foreground' : 'opacity-0',
        expanded && 'rotate-90'
      )}
    />

    <span class="min-w-0 flex-1 truncate font-medium" title={node.path || node.name}>
      {cleanDisplayName(node.name)}
    </span>

    <!-- Stacked status bar: written / matched / review / failed / queued -->
    <div
      class="bg-muted hidden h-[6px] w-28 shrink-0 overflow-hidden rounded-full sm:flex"
      title={`in library ${written} · matched ${matchedNotWritten} · review ${node.needsReview} · failed ${node.failed} · queued ${node.pending}`}
    >
      <span class="h-full bg-emerald-600" style="width: {pct(written)}%"></span>
      <span class="h-full bg-emerald-500" style="width: {pct(matchedNotWritten)}%"></span>
      <span class="h-full bg-amber-500" style="width: {pct(node.needsReview)}%"></span>
      <span class="h-full bg-red-500" style="width: {pct(node.failed)}%"></span>
      <span class="h-full bg-slate-400/60" style="width: {pct(node.pending)}%"></span>
    </div>

    <span
      class={cn(
        'w-10 shrink-0 text-right font-mono text-[11px] tabular-nums',
        matchedPctLabel >= 90
          ? 'text-emerald-600 dark:text-emerald-400'
          : matchedPctLabel >= 60
            ? 'text-amber-600 dark:text-amber-400'
            : 'text-red-600 dark:text-red-400'
      )}
    >
      {matchedPctLabel}%
    </span>

    <span class="text-muted-foreground w-20 shrink-0 text-right font-mono text-[11px] tabular-nums">
      {#if node.notMatched > 0}
        <span class="text-foreground">{node.notMatched.toLocaleString()}</span> / {node.total.toLocaleString()}
      {:else}
        {node.total.toLocaleString()}
      {/if}
    </span>
  </button>

    <Button
      variant="ghost"
      size="sm"
      class={cn(
        'mr-1 h-6 shrink-0 px-2 text-[11px] opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100',
        (isEnriching || enrichState === 'error') && 'opacity-100',
        isEnriching && 'text-primary',
        enrichState === 'error' && 'text-destructive'
      )}
      disabled={isEnriching}
      title="Enqueue every song under this folder for enrichment"
      onclick={handleEnrichFolder}
    >
      {#if isEnriching}
        <Loader2 class="mr-1 size-3 animate-spin" />
        Enriching…
      {:else if enrichState === 'error'}
        <AlertCircle class="mr-1 size-3" />
        Failed
      {:else}
        <Sparkles class="mr-1 size-3" />
        Enrich
      {/if}
    </Button>
  </div>

  {#if expanded}
    <div>
      {#each node.children as child (child.path)}
        <Self node={child} depth={depth + 1} {enrichingPaths} {refreshToken} {onEnriched} />
      {/each}

      {#if hasFiles}
        {#if filesState === 'loading'}
          <div
            class="text-muted-foreground flex items-center gap-2 py-1.5 text-[11px]"
            style="padding-left: {depth * 18 + 30}px"
          >
            <Loader2 class="size-3 animate-spin" />
            Loading files…
          </div>
        {:else if filesState === 'error'}
          <button
            type="button"
            onclick={loadFiles}
            class="text-muted-foreground hover:text-foreground flex items-center gap-2 py-1.5 text-[11px]"
            style="padding-left: {depth * 18 + 30}px"
          >
            <AlertCircle class="size-3 text-amber-500" />
            Couldn't load files — retry
          </button>
        {:else if files}
          {#each files as file (file.id)}
            <SourceFileRow {file} depth={depth + 1} />
          {/each}
        {/if}
      {/if}
    </div>
  {/if}
</div>

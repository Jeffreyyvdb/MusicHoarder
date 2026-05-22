<script lang="ts">
  import { enrichFolder, type DirectoryMatchNode } from '$lib/api-client';
  import { AlertCircle, CheckCircle2, ChevronRight, Loader2, Sparkles } from '@lucide/svelte';
  import { cn } from '$lib/utils';
  import { Button } from '$lib/components/ui/button';
  import Self from './DirectoryTreeRow.svelte';

  let {
    node,
    depth = 0
  }: {
    node: DirectoryMatchNode;
    depth?: number;
  } = $props();

  let expanded = $state(false);
  const hasChildren = $derived(node.children.length > 0);

  let enrichState = $state<'idle' | 'loading' | 'success' | 'error'>('idle');
  let enrichCount = $state(0);

  // Segment widths for the stacked status bar, as a share of this folder's total.
  function pct(n: number): number {
    return node.total > 0 ? (n / node.total) * 100 : 0;
  }

  const matchedPctLabel = $derived(node.total > 0 ? Math.round(node.matchedPct) : 0);

  async function handleEnrichFolder() {
    enrichState = 'loading';
    try {
      const result = await enrichFolder(node.path);
      enrichState = 'success';
      enrichCount = result.enqueued;
      setTimeout(() => (enrichState = 'idle'), 4000);
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
    onclick={() => hasChildren && (expanded = !expanded)}
    class={cn(
      'flex w-full items-center gap-2 rounded-md py-1.5 pr-2 text-left text-[13px] transition-colors',
      hasChildren ? 'hover:bg-muted/60 cursor-pointer' : 'cursor-default'
    )}
    style="padding-left: {depth * 18 + 6}px"
    aria-expanded={hasChildren ? expanded : undefined}
  >
    <ChevronRight
      class={cn(
        'size-3.5 shrink-0 transition-transform',
        hasChildren ? 'text-muted-foreground' : 'opacity-0',
        expanded && 'rotate-90'
      )}
    />

    <span class="min-w-0 flex-1 truncate font-medium" title={node.path || node.name}>
      {node.name}
    </span>

    <!-- Stacked status bar: matched / review / failed / pending -->
    <div
      class="bg-muted hidden h-[6px] w-28 shrink-0 overflow-hidden rounded-full sm:flex"
      title={`matched ${node.matched} · review ${node.needsReview} · failed ${node.failed} · pending ${node.pending}`}
    >
      <span class="h-full bg-emerald-500" style="width: {pct(node.matched)}%"></span>
      <span class="h-full bg-amber-500" style="width: {pct(node.needsReview)}%"></span>
      <span class="h-full bg-red-500" style="width: {pct(node.failed)}%"></span>
      <span class="h-full bg-slate-400/60" style="width: {pct(node.pending)}%"></span>
    </div>

    <span
      class={cn(
        'w-10 shrink-0 text-right font-mono text-[11px] tabular-nums',
        matchedPctLabel >= 90
          ? 'text-emerald-600 dark:text-emerald-400'
          : matchedPctLabel >= 50
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
        enrichState !== 'idle' && 'opacity-100',
        enrichState === 'success' && 'text-primary',
        enrichState === 'error' && 'text-destructive'
      )}
      disabled={enrichState === 'loading'}
      title="Enqueue every song under this folder for enrichment"
      onclick={handleEnrichFolder}
    >
      {#if enrichState === 'loading'}
        <Loader2 class="mr-1 size-3 animate-spin" />
        Enriching…
      {:else if enrichState === 'success'}
        <CheckCircle2 class="mr-1 size-3" />
        Queued {enrichCount}
      {:else if enrichState === 'error'}
        <AlertCircle class="mr-1 size-3" />
        Failed
      {:else}
        <Sparkles class="mr-1 size-3" />
        Enrich
      {/if}
    </Button>
  </div>

  {#if hasChildren && expanded}
    <div>
      {#each node.children as child (child.path)}
        <Self node={child} depth={depth + 1} />
      {/each}
    </div>
  {/if}
</div>

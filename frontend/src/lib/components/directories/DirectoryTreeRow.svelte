<script lang="ts">
  import type { DirectoryMatchNode } from '$lib/api-client';
  import { ChevronRight } from '@lucide/svelte';
  import { cn } from '$lib/utils';
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

  // Segment widths for the stacked status bar, as a share of this folder's total.
  function pct(n: number): number {
    return node.total > 0 ? (n / node.total) * 100 : 0;
  }

  const matchedPctLabel = $derived(node.total > 0 ? Math.round(node.matchedPct) : 0);
</script>

<div class="select-none">
  <button
    type="button"
    onclick={() => hasChildren && (expanded = !expanded)}
    class={cn(
      'group flex w-full items-center gap-2 rounded-md py-1.5 pr-2 text-left text-[13px] transition-colors',
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

  {#if hasChildren && expanded}
    <div>
      {#each node.children as child (child.path)}
        <Self node={child} depth={depth + 1} />
      {/each}
    </div>
  {/if}
</div>

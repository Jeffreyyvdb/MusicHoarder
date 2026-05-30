<script lang="ts">
  import {
    enrichFolder,
    fetchFolderFiles,
    type DirectoryMatchNode,
    type SourceFile
  } from '$lib/api-client';
  import { AlertCircle, Check, ChevronRight, Loader2, Sparkles, Tag } from '@lucide/svelte';
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
    onEnriched,
    onToggleExpected
  }: {
    node: DirectoryMatchNode;
    depth?: number;
    enrichingPaths?: Set<string>;
    refreshToken?: number;
    onEnriched?: (path: string, count: number) => void;
    onToggleExpected?: (path: string, next: boolean) => void;
  } = $props();

  let expanded = $state(false);
  const hasChildren = $derived(node.children.length > 0);
  const hasFiles = $derived(node.directFileCount > 0);
  const expandable = $derived(hasChildren || hasFiles);

  // Lazily-loaded files that live directly in this folder.
  let files = $state<SourceFile[] | null>(null);
  let filesState = $state<'idle' | 'loading' | 'error'>('idle');

  let enrichState = $state<'idle' | 'loading' | 'error'>('idle');

  // Persistent in-flight state while the request is live OR this folder's enrich job is running.
  const isEnriching = $derived(enrichState === 'loading' || (enrichingPaths?.has(node.path) ?? false));

  // The action enriches and builds every track into the library, so the label reflects the
  // outcome. A folder counts as already in the library only once every track has been written
  // (node.done === node.total) — otherwise there's still something to add.
  const inLibrary = $derived(node.total > 0 && node.done >= node.total);

  // Enrich is only worth offering when there's still un-landed work, and never for folders the
  // user has tagged "expected low" (those are deliberately out of the work queue).
  const hasWork = $derived(node.pending > 0 || node.failed > 0);
  const showEnrich = $derived(!node.expectedLow && hasWork);

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
  const enriched = $derived(node.matched);

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
  <div
    class={cn(
      'group hover:bg-muted/50 relative flex items-center gap-2 rounded-md pr-2 transition-[background-color,opacity]',
      node.expectedLow && 'opacity-70 hover:opacity-100',
      expanded && 'bg-primary/[0.04]'
    )}
  >
    <button
      type="button"
      onclick={toggle}
      class={cn(
        'flex min-w-0 flex-1 items-center gap-2 py-1.5 text-left text-[13px]',
        expandable ? 'cursor-pointer' : 'cursor-default'
      )}
      style="padding-left: {depth * 18 + 8}px"
      aria-expanded={expandable ? expanded : undefined}
    >
      <ChevronRight
        class={cn(
          'size-3.5 shrink-0 transition-transform',
          expandable ? 'text-muted-foreground' : 'opacity-0',
          expanded && 'rotate-90'
        )}
      />

      <span class="flex min-w-0 flex-1 items-center gap-1.5">
        <span class="truncate font-medium" title={node.path || node.name}>
          {cleanDisplayName(node.name)}
        </span>
        {#if node.expectedLow}
          <span
            class="border-border text-muted-foreground inline-flex shrink-0 items-center rounded-full border border-dashed bg-muted px-1.5 py-px text-[10px] whitespace-nowrap"
            title="You marked this folder as expected to have a low match rate (leaks, unreleased, field recordings)."
          >
            expected low
          </span>
        {:else}
          {#if node.needsReview > 0}
            <span
              class="inline-flex shrink-0 items-center rounded-full bg-amber-500/15 px-1.5 py-px text-[10px] font-medium whitespace-nowrap text-amber-600 dark:text-amber-400"
              title={`${node.needsReview} files awaiting review`}
            >
              {node.needsReview.toLocaleString()} review
            </span>
          {/if}
          {#if node.failed > 0}
            <span
              class="inline-flex shrink-0 items-center rounded-full bg-red-500/10 px-1.5 py-px text-[10px] font-medium whitespace-nowrap text-red-600 dark:text-red-400"
              title={`${node.failed} files matched nothing`}
            >
              {node.failed.toLocaleString()} failed
            </span>
          {/if}
        {/if}
      </span>

      <!-- Stacked status bar: written / matched / review / failed / queued -->
      <span
        class={cn('bg-muted hidden h-[6px] w-28 shrink-0 overflow-hidden rounded-full sm:flex', node.expectedLow && 'opacity-60')}
        title={`in library ${written} · matched ${matchedNotWritten} · review ${node.needsReview} · failed ${node.failed} · queued ${node.pending}`}
      >
        <span class="h-full bg-emerald-600" style="width: {pct(written)}%"></span>
        <span class="h-full bg-emerald-500" style="width: {pct(matchedNotWritten)}%"></span>
        <span class="h-full bg-amber-500" style="width: {pct(node.needsReview)}%"></span>
        <span class="h-full bg-red-500" style="width: {pct(node.failed)}%"></span>
        <span class="h-full bg-slate-400/60" style="width: {pct(node.pending)}%"></span>
      </span>

      <span class="text-muted-foreground hidden w-16 shrink-0 text-right font-mono text-[11px] tabular-nums sm:block">
        <span class="text-foreground">{enriched.toLocaleString()}</span><span class="text-muted-foreground/50">/</span>{node.total.toLocaleString()}
      </span>

      <span
        class={cn(
          'w-10 shrink-0 text-right font-mono text-[11px] tabular-nums',
          node.expectedLow
            ? 'text-muted-foreground/70'
            : matchedPctLabel >= 90
              ? 'text-emerald-600 dark:text-emerald-400'
              : matchedPctLabel >= 60
                ? 'text-amber-600 dark:text-amber-400'
                : 'text-red-600 dark:text-red-400'
        )}
      >
        {matchedPctLabel}%
      </span>
    </button>

    <!-- Row actions: mark expected-low + enrich (revealed on hover; persistent when active).
         Fixed width matches the header's actions column so the Match% column aligns across rows. -->
    <div class="flex w-[88px] shrink-0 items-center justify-end gap-0.5">
      {#if onToggleExpected}
        <button
          type="button"
          onclick={() => onToggleExpected?.(node.path, !node.expectedLow)}
          title={node.expectedLow
            ? 'Clear expected-low tag'
            : 'Mark as expected low match (leaks, unreleased, field recordings)'}
          aria-label={node.expectedLow ? 'Clear expected-low tag' : 'Mark as expected low'}
          class={cn(
            'grid size-6 place-items-center rounded-md transition-[opacity,color,background-color] focus-visible:opacity-100',
            'hover:bg-card hover:text-foreground hover:border-border border border-transparent',
            node.expectedLow
              ? 'text-primary opacity-100'
              : 'text-muted-foreground opacity-0 group-hover:opacity-100'
          )}
        >
          {#if node.expectedLow}
            <Check class="size-3" />
          {:else}
            <Tag class="size-3" />
          {/if}
        </button>
      {/if}

      {#if showEnrich || isEnriching || enrichState === 'error'}
        <Button
          variant="ghost"
          size="sm"
          class={cn(
            'h-6 shrink-0 px-2 text-[11px] opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100',
            (isEnriching || enrichState === 'error') && 'opacity-100',
            isEnriching && 'text-primary',
            enrichState === 'error' && 'text-destructive'
          )}
          disabled={isEnriching}
          title="Add every song under this folder to your library (enrich + build)"
          onclick={handleEnrichFolder}
        >
          {#if isEnriching}
            <Loader2 class="mr-1 size-3 animate-spin" />
            {inLibrary ? 'Updating…' : 'Adding…'}
          {:else if enrichState === 'error'}
            <AlertCircle class="mr-1 size-3" />
            Failed
          {:else}
            <Sparkles class="mr-1 size-3" />
            Enrich
          {/if}
        </Button>
      {/if}
    </div>
  </div>

  {#if expanded}
    <div>
      {#each node.children as child (child.path)}
        <Self node={child} depth={depth + 1} {enrichingPaths} {refreshToken} {onEnriched} {onToggleExpected} />
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

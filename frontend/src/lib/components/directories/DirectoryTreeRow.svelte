<script lang="ts">
  import {
    enrichFolder,
    fetchFolderFiles,
    type DirectoryMatchNode,
    type SourceFile
  } from '$lib/api-client';
  import {
    AlertCircle,
    Check,
    ChevronRight,
    Ellipsis,
    Loader2,
    Sparkles,
    Tag
  } from '@lucide/svelte';
  import { cleanDisplayName } from '$lib/formatters';
  import { cn } from '$lib/utils';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
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
  const name = $derived(cleanDisplayName(node.name));

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

  const enrichLabel = $derived(
    isEnriching
      ? inLibrary
        ? 'Updating…'
        : 'Adding…'
      : enrichState === 'error'
        ? 'Enrich failed — try again'
        : inLibrary
          ? 'Update in library'
          : 'Add to library'
  );
  const offerEnrich = $derived(showEnrich || isEnriching || enrichState === 'error');
  const hasMenu = $derived(!!onToggleExpected || offerEnrich);
</script>

<div class="select-none">
  <!-- A 44pt row on a phone (36 on a desktop). Expected-low folders read quieter through their
       text tone and badge, never through opacity (which sank their text below 4.5:1). -->
  <div
    class="group hover:bg-accent relative flex min-h-11 items-center gap-1 rounded-lg pr-1 transition-colors duration-100 md:min-h-9 md:pr-2"
  >
    <button
      type="button"
      onclick={toggle}
      class={cn(
        'focus-visible:ring-ring/50 flex min-h-11 min-w-0 flex-1 items-center gap-2 rounded-lg text-left outline-none focus-visible:ring-3 focus-visible:ring-inset md:min-h-9',
        'text-body md:text-[13px]',
        // Below md the indent stops growing after three levels and steps 14px, not 18px: a
        // folder four deep on a phone otherwise starts its name 80px in.
        'pl-[calc(min(var(--depth),3)*14px_+_8px)] md:pl-[calc(var(--depth)*18px_+_8px)]',
        expandable ? 'cursor-pointer' : 'cursor-default'
      )}
      style:--depth={depth}
      aria-expanded={expandable ? expanded : undefined}
    >
      <ChevronRight
        aria-hidden="true"
        class={cn(
          'size-4 shrink-0 transition-transform duration-200 ease-[cubic-bezier(0.23,1,0.32,1)] md:size-3.5',
          expandable ? 'text-muted-foreground' : 'invisible',
          expanded && 'rotate-90'
        )}
      />

      <span class="flex min-w-0 flex-1 flex-wrap items-center gap-x-2 gap-y-0.5 py-1.5 md:flex-nowrap md:py-0">
        <span
          class={cn('min-w-0 truncate md:font-medium', node.expectedLow && 'text-muted-foreground')}
          title={node.path || node.name}
        >
          {name}
        </span>
        {#if node.expectedLow}
          <span
            class="bg-muted text-muted-foreground text-caption-1 inline-flex shrink-0 items-center rounded-full px-2 py-px whitespace-nowrap"
          >
            Expected low
          </span>
        {:else}
          {#if node.needsReview > 0}
            <span class="text-muted-foreground text-caption-1 inline-flex shrink-0 items-center gap-1.5 font-normal whitespace-nowrap">
              <span class="bg-warning size-1.5 rounded-full" aria-hidden="true"></span>
              {node.needsReview.toLocaleString()} review
            </span>
          {/if}
          {#if node.failed > 0}
            <span class="text-muted-foreground text-caption-1 inline-flex shrink-0 items-center gap-1.5 font-normal whitespace-nowrap">
              <span class="bg-destructive size-1.5 rounded-full" aria-hidden="true"></span>
              {node.failed.toLocaleString()} failed
            </span>
          {/if}
        {/if}
      </span>

      <!-- Stacked status bar (desktop): written / matched / review / failed / queued. The same
           figures are in the tooltip and, per folder, in the badges and counts beside it. -->
      <span
        class="bg-muted hidden h-[6px] w-28 shrink-0 overflow-hidden rounded-full md:flex"
        title={`in library ${written} · matched ${matchedNotWritten} · review ${node.needsReview} · failed ${node.failed} · queued ${node.pending}`}
      >
        <span class="bg-primary h-full" style="width: {pct(written)}%"></span>
        <span class="bg-primary/50 h-full" style="width: {pct(matchedNotWritten)}%"></span>
        <span class="bg-warning h-full" style="width: {pct(node.needsReview)}%"></span>
        <span class="bg-destructive h-full" style="width: {pct(node.failed)}%"></span>
        <span class="bg-muted-foreground-dim h-full" style="width: {pct(node.pending)}%"></span>
      </span>

      <span class="text-muted-foreground hidden w-16 shrink-0 text-right text-xs tabular-nums md:block">
        <span class="text-foreground">{enriched.toLocaleString()}</span><span aria-hidden="true">/</span><span
          class="sr-only"> of </span
        >{node.total.toLocaleString()}
      </span>

      <span
        class={cn(
          'text-subheadline w-11 shrink-0 text-right tabular-nums md:w-10 md:text-xs',
          node.expectedLow ? 'text-muted-foreground-dim' : 'text-muted-foreground'
        )}
      >
        {matchedPctLabel}%
      </span>
    </button>

    <!-- Row actions. A phone gets one ••• menu (a finger cannot hover, and two 32px icons per
         row crowded the name); the state of a running enrich shows beside it. A desktop keeps
         the inline buttons, quieter until the row is hovered. -->
    {#if hasMenu}
      <div class="flex shrink-0 items-center md:hidden">
        {#if isEnriching}
          <Loader2 class="text-primary size-4 animate-spin" aria-label={enrichLabel} />
        {:else if enrichState === 'error'}
          <AlertCircle class="text-destructive-text size-4" aria-label={enrichLabel} />
        {/if}
        <DropdownMenu.Root>
          <DropdownMenu.Trigger>
            {#snippet child({ props })}
              <Button
                {...props}
                variant="ghost"
                size="icon"
                class="text-muted-foreground size-11 rounded-full"
                aria-label="Actions for {name}"
              >
                <Ellipsis aria-hidden="true" />
              </Button>
            {/snippet}
          </DropdownMenu.Trigger>
          <DropdownMenu.Content align="end" class="max-w-[min(20rem,calc(100vw-2rem))] min-w-60">
            <!-- The folder's full source path: a phone shows only its name in the row (the
                 desktop's hover title is out of reach of a finger), and two "Disc 1"s under
                 different albums are told apart only here. -->
            <DropdownMenu.Label class="font-mono font-normal break-all">
              {node.path || node.name}
            </DropdownMenu.Label>
            <DropdownMenu.Separator />
            {#if offerEnrich}
              <DropdownMenu.Item disabled={isEnriching} onSelect={handleEnrichFolder}>
                {enrichLabel}
                <Sparkles />
              </DropdownMenu.Item>
            {/if}
            {#if onToggleExpected}
              <DropdownMenu.Item onSelect={() => onToggleExpected?.(node.path, !node.expectedLow)}>
                {node.expectedLow ? 'Clear expected-low tag' : 'Mark as expected low'}
                {#if node.expectedLow}<Check />{:else}<Tag />{/if}
              </DropdownMenu.Item>
            {/if}
          </DropdownMenu.Content>
        </DropdownMenu.Root>
      </div>
    {/if}

    <!-- Desktop: mark expected-low + enrich (hover-revealed; persistent when active). Fixed width
         matches the header's actions column so the Match% column aligns across rows. At rest
         they step down a text token rather than fading (an opacity fade took "Enrich" under
         3:1); a touch screen at this width, which cannot hover, keeps them at full tone. -->
    <div class="hidden w-[104px] shrink-0 items-center justify-end gap-0.5 md:flex">
      {#if onToggleExpected}
        <Button
          variant="ghost"
          size="icon"
          onclick={() => onToggleExpected?.(node.path, !node.expectedLow)}
          title={node.expectedLow
            ? 'Clear expected-low tag'
            : 'Mark as expected low match (leaks, unreleased, field recordings)'}
          aria-label={node.expectedLow ? `Clear expected-low tag on ${name}` : `Mark ${name} as expected low`}
          class={cn(
            '-my-1 shrink-0 transition-colors',
            node.expectedLow
              ? 'text-primary'
              : 'text-muted-foreground pointer-fine:text-muted-foreground-dim pointer-fine:group-hover:text-foreground pointer-fine:focus-visible:text-foreground'
          )}
        >
          {#if node.expectedLow}
            <Check class="size-3.5" aria-hidden="true" />
          {:else}
            <Tag class="size-3.5" aria-hidden="true" />
          {/if}
        </Button>
      {/if}

      {#if offerEnrich}
        <Button
          variant="ghost"
          size="sm"
          class={cn(
            '-my-1 h-8 shrink-0 gap-1 px-2 text-xs transition-colors',
            !isEnriching &&
              enrichState !== 'error' &&
              'pointer-fine:text-muted-foreground-dim pointer-fine:group-hover:text-foreground pointer-fine:focus-visible:text-foreground',
            isEnriching && 'text-primary',
            enrichState === 'error' && 'text-destructive-text'
          )}
          disabled={isEnriching}
          title="Add every song under this folder to your library (enrich + build)"
          aria-label={isEnriching || enrichState === 'error' ? enrichLabel : `Enrich ${name}`}
          onclick={handleEnrichFolder}
        >
          {#if isEnriching}
            <Loader2 class="size-3 animate-spin" aria-hidden="true" />
            {inLibrary ? 'Updating…' : 'Adding…'}
          {:else if enrichState === 'error'}
            <AlertCircle class="size-3" aria-hidden="true" />
            Failed
          {:else}
            <Sparkles class="size-3" aria-hidden="true" />
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
            class="text-muted-foreground text-subheadline flex min-h-11 items-center gap-2 pl-[calc(min(var(--depth),3)*14px_+_32px)] md:min-h-8 md:pl-[calc(var(--depth)*18px_+_30px)] md:text-xs"
            style:--depth={depth}
          >
            <Loader2 class="size-3.5 animate-spin" aria-hidden="true" />
            Loading files…
          </div>
        {:else if filesState === 'error'}
          <button
            type="button"
            onclick={loadFiles}
            class="text-muted-foreground hover:text-foreground text-subheadline flex min-h-11 items-center gap-2 pl-[calc(min(var(--depth),3)*14px_+_32px)] md:min-h-8 md:pl-[calc(var(--depth)*18px_+_30px)] md:text-xs"
            style:--depth={depth}
          >
            <AlertCircle class="text-warning-text size-3.5" aria-hidden="true" />
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

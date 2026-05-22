<script lang="ts">
  import { fetchDirectoryMatchTree, type DirectoryMatchNode } from '$lib/api-client';
  import DirectoryTreeRow from '$lib/components/directories/DirectoryTreeRow.svelte';
  import { FolderTree, Loader2, AlertTriangle } from '@lucide/svelte';

  let tree = $state<DirectoryMatchNode | null>(null);
  let isLoading = $state(true);
  let error = $state<string | null>(null);

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
    };
  });

  const total = $derived(tree?.total ?? 0);
  const matched = $derived(tree?.matched ?? 0);
  const notMatched = $derived(tree?.notMatched ?? 0);
  const matchedPct = $derived(tree && tree.total > 0 ? Math.round(tree.matchedPct) : 0);
</script>

<div class="flex h-full min-h-0 flex-col">
  <header class="border-border flex shrink-0 flex-col gap-1 border-b px-5 py-4">
    <div class="flex items-center gap-2">
      <FolderTree class="text-muted-foreground size-4" />
      <h1 class="text-[15px] font-semibold">Match rate by folder</h1>
    </div>
    <p class="text-muted-foreground text-[12.5px]">
      Songs that aren't matched, grouped by source directory. Drill into a folder to find where the
      enrichment backlog actually lives — well-tagged folders that simply aren't in the public
      databases (leaks, unreleased) will show low match rates that are expected.
    </p>
  </header>

  {#if !isLoading && tree}
    <div class="border-border flex shrink-0 flex-wrap items-center gap-x-6 gap-y-2 border-b px-5 py-3 text-[12.5px]">
      <div class="flex items-baseline gap-1.5">
        <span class="font-mono text-base font-semibold tabular-nums">{matchedPct}%</span>
        <span class="text-muted-foreground">matched overall</span>
      </div>
      <div class="text-muted-foreground">
        <span class="text-foreground font-mono font-medium tabular-nums">{matched.toLocaleString()}</span>
        matched
      </div>
      <div class="text-muted-foreground">
        <span class="text-foreground font-mono font-medium tabular-nums">{notMatched.toLocaleString()}</span>
        not matched
      </div>
      <div class="text-muted-foreground">
        <span class="text-foreground font-mono font-medium tabular-nums">{total.toLocaleString()}</span>
        total
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
    {:else if tree && tree.children.length > 0}
      <div class="text-muted-foreground mb-1 flex items-center gap-2 px-2 text-[10.5px] font-medium tracking-wide uppercase">
        <span class="flex-1">Folder</span>
        <span class="hidden w-28 text-center sm:block">Status</span>
        <span class="w-10 text-right">Match</span>
        <span class="w-20 text-right">Unmatched</span>
      </div>
      {#each tree.children as child (child.path)}
        <DirectoryTreeRow node={child} depth={0} />
      {/each}
    {:else}
      <div class="text-muted-foreground flex h-full items-center justify-center text-sm">
        No songs indexed yet.
      </div>
    {/if}
  </div>
</div>

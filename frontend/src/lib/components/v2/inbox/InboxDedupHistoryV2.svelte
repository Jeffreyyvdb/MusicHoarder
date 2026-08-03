<script lang="ts">
  import { History, Loader2, Undo2 } from '@lucide/svelte';
  import { fetchDedupActions, revertDedupAction, type DedupAction } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import { cn } from '$lib/utils';

  let actions = $state<DedupAction[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let reverting = $state<number | null>(null);

  async function load() {
    try {
      loading = true;
      error = null;
      const res = await fetchDedupActions();
      actions = res.actions ?? [];
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load action history';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  async function revert(action: DedupAction) {
    if (reverting != null) return;
    try {
      reverting = action.batchTicks;
      error = null;
      await revertDedupAction(action.source, action.batchTicks);
      await load();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Revert failed';
    } finally {
      reverting = null;
    }
  }

  const SOURCE_LABELS: Record<string, string> = {
    'artist-merge': 'Artist merge',
    'album-merge': 'Album merge',
    'artist-credit-split': 'Credit split',
    'album-identity-heal': 'Album heal'
  };

  function when(action: DedupAction): string {
    return new Date(action.createdAtUtc).toLocaleString(undefined, {
      month: 'short',
      day: 'numeric',
      hour: '2-digit',
      minute: '2-digit'
    });
  }
</script>

{#if !loading && actions.length === 0 && !error}
  <!-- Nothing to show, nothing to say. -->
{:else}
  <div class="border-border bg-surface-sunken mt-5 rounded-lg border p-4">
    <div class="mb-2 flex items-center gap-2">
      <History class="text-muted-foreground size-4" />
      <span class="text-[12.5px] font-semibold">Recent dedup actions</span>
      {#if loading}
        <Loader2 class="text-muted-foreground size-3.5 animate-spin" />
      {/if}
    </div>
    {#if error}
      <p class="text-destructive mb-2 text-[12px]">{error}</p>
    {/if}
    <div class="divide-border divide-y">
      {#each actions as action (action.source + action.batchTicks)}
        <div class="flex items-start gap-3 py-2">
          <div class="min-w-0 flex-1">
            <div class="flex flex-wrap items-center gap-2">
              <span class="text-[12.5px] font-medium">{SOURCE_LABELS[action.source] ?? action.source}</span>
              <span class="text-muted-foreground text-[11px]">{when(action)}</span>
              <span class="text-muted-foreground text-[11px] tabular-nums">
                {action.songCount} song{action.songCount === 1 ? '' : 's'}
              </span>
              {#if action.reverted}
                <span class="bg-accent text-muted-foreground rounded-sm px-1.5 py-px text-[10px]">reverted</span>
              {/if}
            </div>
            {#if action.highlights.length > 0}
              <div class={cn('text-muted-foreground mt-0.5 truncate text-[11.5px]', action.reverted && 'line-through opacity-60')}>
                {action.highlights.join(' · ')}
              </div>
            {/if}
          </div>
          {#if action.revertible}
            <Button
              variant="outline"
              size="sm"
              class="h-6 shrink-0 px-2 text-[11px]"
              disabled={reverting != null}
              onclick={() => revert(action)}
            >
              {#if reverting === action.batchTicks}
                <Loader2 class="mr-1 size-3 animate-spin" />
              {:else}
                <Undo2 class="mr-1 size-3" />
              {/if}
              Revert
            </Button>
          {:else if !action.reverted}
            <span class="text-muted-foreground/70 shrink-0 text-[10.5px]" title="Heals converge automatically — reverting one would just be re-applied by the next pass.">
              auto-heal
            </span>
          {/if}
        </div>
      {/each}
    </div>
    <p class="text-muted-foreground mt-2 text-[11px]">
      Reverting restores the previous tags and re-tags built files in place — nothing is ever deleted.
    </p>
  </div>
{/if}

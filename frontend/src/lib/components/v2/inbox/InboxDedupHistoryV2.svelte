<script lang="ts">
  import { untrack } from 'svelte';
  import { Loader2, Undo2 } from '@lucide/svelte';
  import { fetchDedupActions, revertDedupAction, type DedupAction } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { toast } from 'svelte-sonner';
  import { cn } from '$lib/utils';
  import { formatDateTime } from '$lib/formatters';
  import { dedupKey } from './dedup-undo';

  type Props = {
    /** Bump to reload quietly (after the queue above made or undid a change). */
    refresh?: number;
    /** A revert changed the library; the queue above may have a cluster back. */
    onreverted?: () => void;
  };
  const { refresh = 0, onreverted }: Props = $props();

  let actions = $state<DedupAction[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let reverting = $state<string | null>(null);

  async function load(quiet = false) {
    try {
      if (!quiet) loading = true;
      error = null;
      const res = await fetchDedupActions();
      actions = res.actions ?? [];
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load action history';
    } finally {
      loading = false;
    }
  }

  // The first run loads; every later bump of `refresh` reloads in place.
  $effect(() => {
    const quiet = refresh > 0;
    untrack(() => void load(quiet));
  });

  async function revert(action: DedupAction) {
    if (reverting != null) return;
    try {
      reverting = dedupKey(action);
      error = null;
      await revertDedupAction(action.source, action.batchTicks);
      toast.success(`Reverted the ${(SOURCE_LABELS[action.source] ?? 'change').toLowerCase()}`);
      await load(true);
      onreverted?.();
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

  // The app's one date-and-time style ("Sep 12, 2026, 6:51 AM"), as History and Settings use.
  function when(action: DedupAction): string {
    return formatDateTime(action.createdAtUtc);
  }

  const hasAutoHeal = $derived(actions.some((a) => !a.revertible && !a.reverted));
</script>

{#if !loading && actions.length === 0 && !error}
  <!-- Nothing to show, nothing to say. -->
{:else}
  <GroupedList.Section header="Recent dedup actions">
    {#if error}
      <p role="alert" class="text-destructive-text text-subheadline px-4 py-3 md:text-sm">
        {error}
      </p>
    {/if}
    {#if loading && actions.length === 0}
      <div class="text-body text-muted-foreground flex min-h-11 items-center gap-2 px-4 md:text-sm">
        <Loader2 class="size-4 animate-spin" /> Loading…
      </div>
    {/if}
    {#each actions as action (dedupKey(action))}
      <GroupedList.Row
        label={SOURCE_LABELS[action.source] ?? action.source}
        sublabel="{when(action)} · {action.songCount} track{action.songCount === 1
          ? ''
          : 's'}{action.reverted ? ' · Reverted' : ''}"
      >
        {#if action.highlights.length > 0}
          <span
            class={cn(
              'text-footnote text-muted-foreground mt-0.5 line-clamp-2 md:text-xs',
              action.reverted && 'line-through'
            )}
          >
            {action.highlights.join(' · ')}
          </span>
        {/if}
        {#snippet trailing()}
          {#if action.revertible}
            <Button
              variant="outline"
              size="sm"
              class="h-11 gap-1.5 rounded-full px-4 text-[15px] md:h-7 md:rounded-lg md:px-2.5 md:text-[12px]"
              disabled={reverting != null}
              onclick={() => revert(action)}
            >
              {#if reverting === dedupKey(action)}
                <Loader2 class="size-4 animate-spin md:size-3" />
              {:else}
                <Undo2 class="size-4 md:size-3" />
              {/if}
              Revert
            </Button>
          {:else if !action.reverted}
            <span class="text-footnote text-muted-foreground md:text-[11px]">Auto-heal</span>
          {/if}
        {/snippet}
      </GroupedList.Row>
    {/each}
    {#snippet footer()}
      <!-- The second sentence is an expression so it keeps its leading space: Svelte trims the
           whitespace a block starts with, which glued the two sentences together. -->
      Reverting restores the previous tags and re-tags built files in place — nothing is ever deleted.{hasAutoHeal
        ? ' Album heals can’t be reverted: they converge on their own, so the next pass would re-apply one.'
        : ''}
    {/snippet}
  </GroupedList.Section>
{/if}

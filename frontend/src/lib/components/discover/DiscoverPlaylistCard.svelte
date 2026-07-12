<script lang="ts">
  import type { DiscoverPlaylistSummary } from '$lib/api-client';
  import { ListMusic, Plus, Check, Loader2 } from '@lucide/svelte';

  type Props = {
    playlist: DiscoverPlaylistSummary;
    onClick: () => void;
    /** Quick-subscribe from the card corner (only shown when not yet subscribed). */
    onQuickSubscribe: () => void;
    /** Shows a spinner on the corner action while a subscribe request is in flight. */
    isBusy?: boolean;
  };
  const { playlist, onClick, onQuickSubscribe, isBusy = false }: Props = $props();
</script>

<div class="relative">
  <button
    onclick={onClick}
    class="border-border bg-card hover:border-primary/30 hover:bg-card/80 flex w-full flex-col items-center gap-3 rounded-xl border p-4 text-left transition-colors"
  >
    <div class="bg-secondary aspect-square w-full overflow-hidden rounded-lg">
      {#if playlist.coverUrl}
        <img
          src={playlist.coverUrl}
          alt={playlist.title}
          class="size-full object-cover"
          crossorigin="anonymous"
        />
      {:else}
        <div class="flex size-full items-center justify-center">
          <ListMusic class="text-muted-foreground size-10" />
        </div>
      {/if}
    </div>

    <div class="w-full min-w-0">
      <h3 class="truncate text-sm font-semibold">{playlist.title}</h3>
      <p class="text-muted-foreground mt-0.5 truncate text-xs">
        {playlist.trackCount.toLocaleString()} track{playlist.trackCount === 1 ? '' : 's'}{playlist.creatorName
          ? ` · ${playlist.creatorName}`
          : ''}
      </p>
    </div>
  </button>

  {#if playlist.subscribed}
    <div
      class="border-primary/40 bg-primary/15 text-primary absolute top-2 right-2 grid size-8 place-items-center rounded-full border shadow-sm"
      title="Subscribed"
      aria-label="Subscribed"
    >
      <Check class="size-4" />
    </div>
  {:else}
    <button
      type="button"
      aria-label="Subscribe to playlist"
      title="Subscribe"
      disabled={isBusy}
      onclick={(e) => {
        e.stopPropagation();
        onQuickSubscribe();
      }}
      class="bg-background/90 hover:bg-background text-foreground absolute top-2 right-2 grid size-8 place-items-center rounded-full border shadow-sm transition-colors disabled:opacity-60"
    >
      {#if isBusy}
        <Loader2 class="size-4 animate-spin" />
      {:else}
        <Plus class="size-4" />
      {/if}
    </button>
  {/if}
</div>

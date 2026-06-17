<script lang="ts">
  import type { SpotifyApiPlaylist } from '$lib/api-client';
  import { ListMusic, Heart, Loader2 } from '@lucide/svelte';

  type Props = {
    playlist: SpotifyApiPlaylist;
    onClick: () => void;
    /** When provided, renders an "add to wishlist" action in the card corner. */
    onAddToWishlist?: () => void;
    /** Shows a spinner on the add action while the request is in flight. */
    isAdding?: boolean;
  };
  const { playlist, onClick, onAddToWishlist, isAdding = false }: Props = $props();
</script>

<div class="relative">
  <button
    onclick={onClick}
    class="border-border bg-card hover:border-primary/30 hover:bg-card/80 flex w-full flex-col items-center gap-3 rounded-xl border p-4 text-left transition-colors"
  >
    <div class="bg-secondary aspect-square w-full overflow-hidden rounded-lg">
      {#if playlist.imageUrl}
        <img
          src={playlist.imageUrl}
          alt={playlist.name}
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
      <h3 class="truncate text-sm font-semibold">{playlist.name}</h3>
      <p class="text-muted-foreground mt-0.5 text-xs">
        {playlist.trackCount} tracks{playlist.ownerName ? ` · ${playlist.ownerName}` : ''}
      </p>
    </div>
  </button>

  {#if onAddToWishlist}
    <button
      type="button"
      aria-label="Add playlist to wishlist"
      title="Add to wishlist"
      disabled={isAdding}
      onclick={onAddToWishlist}
      class="bg-background/90 hover:bg-background text-foreground absolute top-2 right-2 grid size-8 place-items-center rounded-full border shadow-sm transition-colors disabled:opacity-60"
    >
      {#if isAdding}
        <Loader2 class="size-4 animate-spin" />
      {:else}
        <Heart class="size-4" />
      {/if}
    </button>
  {/if}
</div>

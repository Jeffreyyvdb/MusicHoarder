<script lang="ts">
  import type { SpotifyApiPlaylist } from '$lib/api-client';
  import { ListMusic, Plus, Loader2 } from '@lucide/svelte';

  // One Spotify playlist in the grid — the same cover tile as Discover's: the art with the title
  // and meta under it, no card. The tile opens the playlist (a link to its own page); the corner
  // adds the whole playlist to the wishlist. That used to be a Heart, which everywhere else in the
  // app means "like".
  type Props = {
    playlist: SpotifyApiPlaylist;
    href: string;
    /** Called as the link is followed (the grid remembers its scroll position). */
    onOpen?: () => void;
    /** When provided, renders an "add to wishlist" action in the card corner. */
    onAddToWishlist?: () => void;
    /** Shows a spinner on the add action while the request is in flight. */
    isAdding?: boolean;
  };
  const { playlist, href, onOpen, onAddToWishlist, isAdding = false }: Props = $props();
</script>

<div class="relative min-w-0">
  <a
    {href}
    onclick={() => onOpen?.()}
    class="group/tile focus-visible:ring-ring/50 block rounded-md outline-none focus-visible:ring-3"
  >
    <div
      class="bg-muted aspect-square w-full overflow-hidden rounded-md shadow-[0_1px_2px_rgb(0_0_0/0.08)] transition-transform duration-150 ease-[cubic-bezier(0.23,1,0.32,1)] group-active/tile:scale-[0.97]"
    >
      {#if playlist.imageUrl}
        <img
          src={playlist.imageUrl}
          alt=""
          loading="lazy"
          draggable="false"
          class="size-full object-cover"
          crossorigin="anonymous"
        />
      {:else}
        <div class="flex size-full items-center justify-center">
          <ListMusic class="text-muted-foreground size-10" aria-hidden="true" />
        </div>
      {/if}
    </div>
    <div class="mt-1.5 min-w-0">
      <h3 class="text-subheadline truncate font-medium md:text-sm">{playlist.name}</h3>
      <p class="text-footnote text-muted-foreground truncate md:text-xs">
        {playlist.trackCount} tracks{playlist.ownerName ? ` · ${playlist.ownerName}` : ''}
      </p>
    </div>
  </a>

  {#if onAddToWishlist}
    <!-- Over the artwork: the dark translucent disc media controls use on covers; 32px to look
         at, a 44pt hit area on touch. -->
    <button
      type="button"
      aria-label="Add “{playlist.name}” to the wishlist"
      title="Add to wishlist"
      disabled={isAdding}
      onclick={() => onAddToWishlist?.()}
      class="focus-visible:ring-ring/50 absolute top-1.5 right-1.5 grid size-8 place-items-center rounded-full bg-black/50 text-white shadow-sm backdrop-blur-md transition-[background-color,transform] duration-150 outline-none after:absolute after:-inset-1.5 hover:bg-black/65 focus-visible:ring-3 active:scale-[0.94] disabled:opacity-60"
    >
      {#if isAdding}
        <Loader2 class="size-4 animate-spin" aria-hidden="true" />
      {:else}
        <Plus class="size-[18px]" strokeWidth={2.5} aria-hidden="true" />
      {/if}
    </button>
  {/if}
</div>

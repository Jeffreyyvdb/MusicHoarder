<script lang="ts">
  import type { DiscoverPlaylistSummary } from '$lib/api-client';
  import { ListMusic, Plus, Check, Loader2 } from '@lucide/svelte';

  // One playlist in Discover's grid: the Apple Music tile — the cover (8px radius, no card
  // border) with the title and meta under it. The tile is a link (its detail is a page of its
  // own, /discover?playlist=…); the corner holds a quick Subscribe, or a check once subscribed.
  type Props = {
    playlist: DiscoverPlaylistSummary;
    href: string;
    /** Called as the link is followed (the grid remembers its scroll position). */
    onOpen?: () => void;
    /** Quick-subscribe from the card corner (only shown when not yet subscribed). */
    onQuickSubscribe: () => void;
    /** Shows a spinner on the corner action while a subscribe request is in flight. */
    isBusy?: boolean;
  };
  const { playlist, href, onOpen, onQuickSubscribe, isBusy = false }: Props = $props();
</script>

<div class="relative min-w-0">
  <a
    {href}
    onclick={() => onOpen?.()}
    class="group/tile focus-visible:ring-ring/50 block rounded-md outline-none focus-visible:ring-3"
  >
    <div
      class="bg-muted relative aspect-square w-full overflow-hidden rounded-md shadow-[0_1px_2px_rgb(0_0_0/0.08)] transition-transform duration-150 ease-[cubic-bezier(0.23,1,0.32,1)] group-active/tile:scale-[0.97]"
    >
      {#if playlist.coverUrl}
        <img
          src={playlist.coverUrl}
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
      <h3 class="text-subheadline truncate font-medium md:text-sm">{playlist.title}</h3>
      <p class="text-footnote text-muted-foreground truncate md:text-xs">
        {playlist.trackCount.toLocaleString()} track{playlist.trackCount === 1
          ? ''
          : 's'}{playlist.creatorName ? ` · ${playlist.creatorName}` : ''}
      </p>
    </div>
  </a>

  <!-- Over the artwork, so it takes the dark translucent disc media controls use on covers; 32px
       to look at, a 44pt hit area on touch. -->
  {#if playlist.subscribed}
    <span
      role="img"
      aria-label="Subscribed"
      class="bg-primary text-primary-foreground pointer-events-none absolute top-1.5 right-1.5 grid size-7 place-items-center rounded-full shadow-sm"
    >
      <Check class="size-4" strokeWidth={2.75} aria-hidden="true" />
    </span>
  {:else}
    <button
      type="button"
      aria-label="Subscribe to {playlist.title}"
      title="Subscribe"
      disabled={isBusy}
      onclick={onQuickSubscribe}
      class="focus-visible:ring-ring/50 absolute top-1.5 right-1.5 grid size-8 place-items-center rounded-full bg-black/50 text-white shadow-sm backdrop-blur-md transition-[background-color,transform] duration-150 outline-none after:absolute after:-inset-1.5 hover:bg-black/65 focus-visible:ring-3 active:scale-[0.94] disabled:opacity-60"
    >
      {#if isBusy}
        <Loader2 class="size-4 animate-spin" aria-hidden="true" />
      {:else}
        <Plus class="size-[18px]" strokeWidth={2.5} aria-hidden="true" />
      {/if}
    </button>
  {/if}
</div>

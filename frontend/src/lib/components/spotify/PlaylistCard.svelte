<script lang="ts">
  import type { SpotifyApiPlaylist } from '$lib/api-client';
  import { ListMusic } from '@lucide/svelte';

  type Props = { playlist: SpotifyApiPlaylist; onClick: () => void };
  const { playlist, onClick }: Props = $props();
</script>

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

<script lang="ts">
  import { Skeleton } from '$lib/components/ui/skeleton';
</script>

<!-- Shaped like the cover grid it stands in for (Discover and Spotify's playlists): a square of
     art with two lines under it, no card. -->
<div
  role="status"
  class="grid grid-cols-2 gap-x-4 gap-y-5 px-4 pt-2 pb-4 sm:grid-cols-3 md:grid-cols-4 md:px-7 lg:grid-cols-5"
>
  <span class="sr-only">Loading playlists…</span>
  {#each Array.from({ length: 10 }) as _, i (i)}
    <div aria-hidden="true" class="flex flex-col gap-2">
      <Skeleton class="aspect-square w-full rounded-md" />
      <div class="space-y-1.5">
        <Skeleton class="h-4 w-3/4" />
        <Skeleton class="h-3 w-1/2" />
      </div>
    </div>
  {/each}
</div>

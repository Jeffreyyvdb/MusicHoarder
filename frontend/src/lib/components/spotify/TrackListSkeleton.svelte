<script lang="ts">
  import { Skeleton } from '$lib/components/ui/skeleton';
</script>

<!-- Shaped like the track rows it stands in for: art, two lines, a trailing status/duration. -->
<div role="status" class="py-1 md:px-3">
  <span class="sr-only">Loading tracks…</span>
  {#each Array.from({ length: 10 }) as _, i (i)}
    <div aria-hidden="true" class="flex min-h-14 items-center gap-3 px-4 py-2">
      <Skeleton class="size-11 shrink-0 rounded-sm md:size-10" />
      <div class="min-w-0 flex-1 space-y-1.5">
        <Skeleton class="h-4 w-2/3 max-w-48" />
        <Skeleton class="h-3 w-1/2 max-w-32" />
      </div>
      <Skeleton class="h-3 w-10 shrink-0" />
    </div>
  {/each}
</div>

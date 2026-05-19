<script lang="ts">
  import { Skeleton } from '$lib/components/ui/skeleton';
</script>

<div class="space-y-2 p-4">
  {#each Array.from({ length: 10 }) as _, i (i)}
    <div class="flex items-center gap-3 px-3 py-2.5">
      <Skeleton class="h-4 w-8" />
      <Skeleton class="size-10 rounded" />
      <div class="flex-1 space-y-1.5">
        <Skeleton class="h-4 w-48" />
        <Skeleton class="h-3 w-32" />
      </div>
      <Skeleton class="h-3 w-12" />
    </div>
  {/each}
</div>

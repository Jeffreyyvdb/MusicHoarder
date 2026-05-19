<script lang="ts">
  import { Skeleton } from '$lib/components/ui/skeleton';
</script>

<div class="grid grid-cols-2 gap-4 p-4 sm:grid-cols-3 md:grid-cols-4 md:p-6 lg:grid-cols-5">
  {#each Array.from({ length: 10 }) as _, i (i)}
    <div class="border-border bg-card flex flex-col gap-3 rounded-xl border p-4">
      <Skeleton class="aspect-square w-full rounded-lg" />
      <div class="space-y-1.5">
        <Skeleton class="h-4 w-3/4" />
        <Skeleton class="h-3 w-1/2" />
      </div>
    </div>
  {/each}
</div>

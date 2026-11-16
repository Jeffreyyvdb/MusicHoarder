<script lang="ts">
  import type { ActivityBucket } from '#lib/listening.ts';

  type Props = {
    buckets: ActivityBucket[];
    /** What one bar covers, for the spoken summary ("day", "week", "month", "year"). */
    unit: string;
  };
  const { buckets, unit }: Props = $props();

  const max = $derived(Math.max(1, ...buckets.map((b) => b.streams)));
  const total = $derived(buckets.reduce((sum, b) => sum + b.streams, 0));
</script>

<div role="img" aria-label="Streams per {unit}: {total} in total">
  <div class="flex h-28 items-end gap-[3px]">
    {#each buckets as bucket (bucket.key)}
      <div
        class={bucket.streams > 0 ? 'bg-primary' : 'bg-secondary'}
        style:height="{bucket.streams > 0 ? Math.max(4, (bucket.streams / max) * 100) : 2}%"
        style:min-height="2px"
        class:rounded-t-sm={true}
        style:flex="1 1 0"
        title="{bucket.label}: {bucket.streams}"
      ></div>
    {/each}
  </div>
  <div class="text-caption-2 text-muted-foreground mt-1.5 flex justify-between" aria-hidden="true">
    <span>{buckets[0]?.label}</span>
    <span>{buckets.at(-1)?.label}</span>
  </div>
</div>

<ul class="sr-only">
  {#each buckets.filter((b) => b.streams > 0) as bucket (bucket.key)}
    <li>{bucket.label}: {bucket.streams} {bucket.streams === 1 ? 'stream' : 'streams'}</li>
  {/each}
</ul>

<script lang="ts">
  import { sparkHeights } from '#lib/background-work/chart-math.ts';

  type Props = {
    values: number[];
    /** What one column is, e.g. "songs synced per day"; the accessible name carries the numbers. */
    label: string;
    class?: string;
  };
  let { values, label, class: className = '' }: Props = $props();

  const heights = $derived(sparkHeights(values));
  const summary = $derived(
    `${label}, last ${values.length} days, oldest first: ${values.map((v) => Math.round(v).toLocaleString()).join(', ')}`
  );
</script>

<!-- One column per UTC day, the busiest at full height. The days before today are the quieter
     tint and today (still filling) is the full one, so the eye lands on the present. -->
<span role="img" aria-label={summary} class="flex h-8 items-end gap-0.5 {className}">
  {#each heights as height, i (i)}
    <span class="flex h-full min-w-0 flex-1 items-end" aria-hidden="true">
      <span
        class="w-full rounded-t-[2px]"
        style:height={height === 0 ? '2px' : `${height}%`}
        style:background-color={height === 0 ? 'var(--viz-muted)' : 'var(--viz-neutral)'}
        style:opacity={i === heights.length - 1 || height === 0 ? 1 : 0.55}
      ></span>
    </span>
  {/each}
</span>

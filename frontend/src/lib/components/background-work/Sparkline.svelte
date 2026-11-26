<script lang="ts">
  import { sparkHeights } from '#lib/background-work/chart-math.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    values: number[];
    label: string;
    class?: string;
  };
  let { values, label, class: className = '' }: Props = $props();

  const heights = $derived(sparkHeights(values));
  const summary = $derived(
    `${label}, last ${values.length} days, oldest first: ${values.map((v) => Math.round(v).toLocaleString()).join(', ')}`
  );
</script>

<span role="img" aria-label={summary} class={cn('flex h-8 items-end gap-[3px]', className)}>
  {#each heights as height, i (i)}
    <span class="flex h-full min-w-0 flex-1 items-end" aria-hidden="true">
      <span
        class="w-full rounded-t-[3px]"
        style:height={height === 0 ? '2px' : `${height}%`}
        style:background-color={height === 0 ? 'var(--viz-muted)' : 'var(--viz-neutral)'}
      ></span>
    </span>
  {/each}
</span>

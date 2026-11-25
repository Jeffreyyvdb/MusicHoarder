<script lang="ts">
  import { formatValue } from '#lib/background-work/format.ts';
  import { toneColor } from '#lib/background-work/tone.ts';
  import type { BarsPanel } from '#lib/background-work/types.ts';
  import PanelFrame from './PanelFrame.svelte';
  import TroubleGlyph from './TroubleGlyph.svelte';

  type Props = { panel: BarsPanel };
  let { panel }: Props = $props();

  // Columns share one baseline and carry their own labels, so a repeated tone is not stepped:
  // a lighter bar would read as a smaller value.
  const fills = $derived(panel.bars.map((b) => toneColor(b.tone)));
  const max = $derived(Math.max(0, ...panel.bars.map((b) => b.value)));
  const pct = (value: number) => (max > 0 && value > 0 ? Math.max(2, (value / max) * 100) : 0);

  // A few short labels stand under their columns; many, or long ones, would collide at phone
  // width, so they read as ranked rows instead.
  const columns = $derived(panel.bars.length <= 7 && panel.bars.every((b) => b.label.length <= 12));
</script>

<PanelFrame title={panel.title} caption={panel.caption} kind="bars">
  {#if columns}
    <ul class="flex h-40 items-end gap-2" aria-label={panel.title}>
      {#each panel.bars as bar, i (i)}
        <li class="flex h-full min-w-0 flex-1 flex-col items-center justify-end">
          <span class="text-caption-2 mb-1 flex items-center gap-0.5 font-medium tabular-nums">
            <TroubleGlyph tone={bar.tone} />{formatValue(panel.format, bar.value)}
          </span>
          <span class="flex min-h-0 w-full flex-1 items-end justify-center">
            <span
              class="w-full max-w-6 rounded-t-[4px]"
              style:height="{pct(bar.value)}%"
              style:background-color={fills[i]}
            ></span>
          </span>
          <span
            class="text-caption-2 text-muted-foreground mt-1 flex h-7 items-start gap-1 text-center leading-tight"
          >
            {bar.label}
          </span>
        </li>
      {/each}
    </ul>
  {:else}
    <ul class="flex flex-col gap-2.5" aria-label={panel.title}>
      {#each panel.bars as bar, i (i)}
        <li class="grid grid-cols-[minmax(5rem,9rem)_minmax(0,1fr)_auto] items-center gap-x-3">
          <span class="text-footnote text-muted-foreground min-w-0 break-words md:text-xs">
            {bar.label}
          </span>
          <span class="bg-muted block h-3 overflow-hidden rounded-full">
            <span
              class="block h-full rounded-r-[4px]"
              style:width="{pct(bar.value)}%"
              style:background-color={fills[i]}
            ></span>
          </span>
          <span
            class="text-footnote flex w-20 items-center justify-end gap-1 font-medium tabular-nums md:text-xs"
          >
            <TroubleGlyph tone={bar.tone} />{formatValue(panel.format, bar.value)}
          </span>
        </li>
      {/each}
    </ul>
  {/if}

  {#snippet table()}
    <table>
      <caption>{panel.title}</caption>
      <thead><tr><th>Label</th><th>Value</th></tr></thead>
      <tbody>
        {#each panel.bars as bar, i (i)}
          <tr><th scope="row">{bar.label}</th><td>{formatValue(panel.format, bar.value)}</td></tr>
        {/each}
      </tbody>
    </table>
  {/snippet}
</PanelFrame>

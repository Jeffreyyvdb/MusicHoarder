<script lang="ts">
  import { formatMoment, formatValue } from '#lib/background-work/format.ts';
  import { toneColor, toneTextClass } from '#lib/background-work/tone.ts';
  import type { FiguresPanel } from '#lib/background-work/types.ts';
  import PanelFrame from './PanelFrame.svelte';
  import TroubleGlyph from './TroubleGlyph.svelte';

  type Props = { panel: FiguresPanel; now?: number };
  let { panel, now = Date.now() }: Props = $props();

  // A figure is a number, a moment, or both ("0 left, resets at 11:00"); the value wears the text
  // token for its tone and a coloured mark stands beside it.
  const shown = (item: FiguresPanel['items'][number]) => ({
    value: item.value == null ? null : formatValue(item.format, item.value),
    moment: item.atUtc ? formatMoment(item.atUtc, { now }) : null
  });
</script>

<PanelFrame title={panel.title} caption={panel.caption} kind="figures">
  <dl class="grid grid-cols-2 gap-x-4 gap-y-4 md:grid-cols-3">
    {#each panel.items as item (item.label)}
      {@const text = shown(item)}
      <div class="flex min-w-0 gap-2.5">
        <span
          aria-hidden="true"
          class="w-1 shrink-0 self-stretch rounded-full"
          style:background-color={toneColor(item.tone)}
        ></span>
        <div class="min-w-0">
          <dt class="text-footnote text-muted-foreground flex items-center gap-1 md:text-xs">
            {item.label}
            <TroubleGlyph tone={item.tone} />
          </dt>
          <dd class="m-0">
            {#if text.value !== null}
              <span class="text-title-3 font-semibold tabular-nums {toneTextClass(item.tone)}"
                >{text.value}</span
              >
            {:else if text.moment}
              <span class="text-headline font-semibold {toneTextClass(item.tone)}"
                >{text.moment}</span
              >
            {:else}
              <span class="text-title-3 text-muted-foreground font-semibold">—</span>
            {/if}
            {#if text.value !== null && text.moment}
              <span class="text-footnote text-muted-foreground block md:text-xs">{text.moment}</span
              >
            {/if}
            {#if item.sublabel}
              <span class="text-footnote text-muted-foreground block md:text-xs"
                >{item.sublabel}</span
              >
            {/if}
          </dd>
        </div>
      </div>
    {/each}
  </dl>
</PanelFrame>

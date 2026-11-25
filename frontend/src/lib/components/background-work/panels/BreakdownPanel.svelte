<script lang="ts">
  import { formatCount, formatShare } from '#lib/background-work/format.ts';
  import { toneFills } from '#lib/background-work/tone.ts';
  import type { BreakdownPanel } from '#lib/background-work/types.ts';
  import { cn } from '#lib/utils.ts';
  import PanelFrame from './PanelFrame.svelte';
  import Swatch from './Swatch.svelte';
  import TroubleGlyph from './TroubleGlyph.svelte';

  type Props = { panel: BreakdownPanel };
  let { panel }: Props = $props();

  let active = $state<string | null>(null);

  const fills = $derived(toneFills(panel.segments.map((s) => s.tone)));
  const shown = $derived(panel.segments.filter((s) => s.count > 0));
  const summary = $derived(
    `${panel.title}: ` +
      panel.segments
        .map((s) => `${s.label} ${formatCount(s.count)} (${formatShare(s.count, panel.total)})`)
        .join(', ')
  );

  function hover(event: PointerEvent) {
    if (event.pointerType !== 'mouse') return;
    const part = (event.target as Element).closest<HTMLElement>('[data-key]');
    active = part?.dataset.key ?? null;
  }

  function toggle(key: string) {
    active = active === key ? null : key;
  }
</script>

<PanelFrame title={panel.title} caption={panel.caption} kind="breakdown">
  <div
    role="img"
    aria-label={summary}
    class="flex h-7 gap-0.5"
    onpointerover={hover}
    onpointerleave={() => (active = null)}
  >
    {#each panel.segments as segment, i (segment.key)}
      {#if segment.count > 0}
        <div
          class="min-w-0.5 rounded-[4px] transition-opacity duration-150"
          style:flex="{segment.count} 1 0"
          style:background-color={fills[i]}
          style:opacity={active === null || active === segment.key ? 1 : 0.35}
          data-key={segment.key}
        ></div>
      {/if}
    {/each}
  </div>
  <p class="text-footnote text-muted-foreground mt-2 md:text-xs">
    {formatCount(panel.total)}
    {panel.unit}
    {#if shown.length < panel.segments.length}
      <span class="sr-only">; parts with none are left out of the bar</span>
    {/if}
  </p>

  <ul class="mt-1 flex flex-col">
    {#each panel.segments as segment, i (segment.key)}
      <li>
        <button
          type="button"
          aria-pressed={active === segment.key}
          onclick={() => toggle(segment.key)}
          class={cn(
            'focus-visible:ring-ring -mx-2 grid min-h-11 w-[calc(100%+1rem)] grid-cols-[1.75rem_minmax(0,1fr)_auto_2.75rem] items-start gap-x-2.5 rounded-lg px-2 py-2 text-left focus-visible:ring-2 focus-visible:outline-hidden',
            active === segment.key && 'bg-accent'
          )}
        >
          <span class="flex h-5 items-center"
            ><Swatch fill={fills[i]} tone={segment.tone} glyph={false} /></span
          >
          <span class="flex min-w-0 flex-col">
            <span class="text-subheadline flex items-center gap-1.5 md:text-sm">
              {segment.label}
              <TroubleGlyph tone={segment.tone} />
            </span>
            {#if segment.note}
              <span class="text-footnote text-muted-foreground md:text-xs">{segment.note}</span>
            {/if}
          </span>
          <span class="text-subheadline text-right font-medium tabular-nums md:text-sm">
            {formatCount(segment.count)}
          </span>
          <span class="text-footnote text-muted-foreground text-right tabular-nums md:text-xs">
            {formatShare(segment.count, panel.total)}
          </span>
        </button>
      </li>
    {/each}
  </ul>

  {#snippet table()}
    <table>
      <caption>{panel.title}</caption>
      <thead><tr><th>Part</th><th>{panel.unit}</th><th>Share</th></tr></thead>
      <tbody>
        {#each panel.segments as segment (segment.key)}
          <tr>
            <th scope="row">{segment.label}</th>
            <td>{formatCount(segment.count)}</td>
            <td>{formatShare(segment.count, panel.total)}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  {/snippet}
</PanelFrame>

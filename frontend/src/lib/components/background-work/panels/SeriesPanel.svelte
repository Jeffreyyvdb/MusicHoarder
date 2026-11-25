<script lang="ts">
  import {
    axisScale,
    formatTick,
    heightPct,
    seriesMax,
    stackSegments,
    timeTicks
  } from '#lib/background-work/chart-math.ts';
  import { formatDayBucket, formatHourBucket, formatValue } from '#lib/background-work/format.ts';
  import { scrub } from '#lib/background-work/scrub.ts';
  import { toneColor, toneFills } from '#lib/background-work/tone.ts';
  import type { SeriesPanel } from '#lib/background-work/types.ts';
  import { IsMobile } from '#lib/hooks/is-mobile.svelte.ts';
  import PanelFrame from './PanelFrame.svelte';
  import Swatch from './Swatch.svelte';

  type Props = { panel: SeriesPanel };
  let { panel }: Props = $props();

  const isMobile = new IsMobile();

  let selected = $state<number | null>(null);

  const count = $derived(panel.points.length);
  const scale = $derived(axisScale(panel.format, seriesMax(panel)));
  const fills = $derived(toneFills(panel.series.map((s) => s.tone)));
  const ticks = $derived(
    timeTicks(
      panel.points.map((p) => p.startUtc),
      panel.bucket,
      isMobile.current ? 4 : 8
    )
  );
  // Dense hourly columns have no room for a gap; a few days' worth do.
  const gap = $derived(count <= 48 ? 2 : count <= 100 ? 1 : 0);
  const columns = $derived(
    panel.points.map((point) => ({
      segments: stackSegments(point.values, scale.top),
      total: point.values.reduce((sum, v) => sum + Math.max(0, v), 0)
    }))
  );

  const bucketName = (startUtc: string) =>
    panel.bucket === 'hour' ? formatHourBucket(startUtc) : formatDayBucket(startUtc);

  const readout = $derived.by(() => {
    if (selected === null) return null;
    const point = panel.points[selected];
    if (!point) return null;
    return {
      when: bucketName(point.startUtc),
      rows: panel.series.map((s, i) => ({
        key: s.key,
        label: s.label,
        value: formatValue(panel.format, point.values[i] ?? 0)
      })),
      total:
        panel.stacked && panel.series.length > 1
          ? formatValue(panel.format, columns[selected].total)
          : null
    };
  });

  const readoutText = $derived(
    readout
      ? `${readout.when}: ${readout.rows.map((r) => `${r.label} ${r.value}`).join(', ')}`
      : `${panel.title}. Use the arrow keys, or touch the chart, to read one ${panel.bucket}.`
  );

  const centre = (index: number) => ((index + 0.5) / Math.max(1, count)) * 100;
  const up = (value: number) => (scale.top > 0 ? (value / scale.top) * 100 : 0);
</script>

<PanelFrame title={panel.title} caption={panel.caption} kind="series">
  <div class="min-h-14" aria-live="polite">
    {#if readout}
      <p class="text-footnote text-muted-foreground md:text-xs">{readout.when}</p>
      <ul class="mt-0.5 flex flex-wrap gap-x-4 gap-y-0.5">
        {#each readout.rows as row, i (row.key)}
          <li class="flex items-center gap-1.5">
            <Swatch fill={fills[i]} tone={panel.series[i].tone} />
            <span class="text-subheadline font-semibold tabular-nums md:text-sm">{row.value}</span>
            <span class="text-footnote text-muted-foreground md:text-xs">{row.label}</span>
          </li>
        {/each}
        {#if readout.total}
          <li class="flex items-center gap-1.5">
            <span class="text-subheadline font-semibold tabular-nums md:text-sm"
              >{readout.total}</span
            >
            <span class="text-footnote text-muted-foreground md:text-xs">Total</span>
          </li>
        {/if}
      </ul>
    {:else}
      <p class="text-footnote text-muted-foreground md:text-xs">
        Touch or hover the chart to read one {panel.bucket}.
      </p>
    {/if}
  </div>

  <div class="mt-2 flex gap-2">
    <div class="relative h-44 w-11 shrink-0" aria-hidden="true">
      {#each scale.ticks as tick (tick)}
        <span
          class="text-caption-2 text-muted-foreground absolute right-0 translate-y-1/2 tabular-nums"
          style:bottom="{up(tick)}%"
        >
          {formatTick(panel.format, tick)}
        </span>
      {/each}
    </div>

    <div class="min-w-0 flex-1">
      <div
        role="slider"
        tabindex="0"
        aria-label={`${panel.title}, inspect one ${panel.bucket}`}
        aria-orientation="horizontal"
        aria-valuemin={0}
        aria-valuemax={Math.max(0, count - 1)}
        aria-valuenow={selected ?? 0}
        aria-valuetext={readoutText}
        class="focus-visible:ring-ring relative h-44 touch-pan-y rounded-sm select-none focus-visible:ring-2 focus-visible:outline-hidden"
        use:scrub={{ count, index: selected, onchange: (i) => (selected = i) }}
      >
        {#each scale.ticks as tick (tick)}
          <span
            class="bg-separator absolute inset-x-0 h-(--hairline)"
            style:bottom="{up(tick)}%"
            aria-hidden="true"
          ></span>
        {/each}

        {#if selected !== null}
          <span
            aria-hidden="true"
            class="bg-foreground/[0.07] absolute inset-y-0 -translate-x-1/2"
            style:left="{centre(selected)}%"
            style:width="max({100 / count}%, 6px)"
          ></span>
        {/if}

        <div class="absolute inset-0 flex items-end" style:gap="{gap}px" aria-hidden="true">
          {#each panel.points as point, p (point.startUtc)}
            <div class="flex h-full min-w-0 flex-1 items-end justify-center">
              {#if panel.stacked}
                <div
                  class="flex w-full max-w-6 flex-col-reverse"
                  style:height="{heightPct(columns[p].total, scale.top, 1.2)}%"
                  style:gap="{count <= 48 ? 1 : 0}px"
                >
                  {#each columns[p].segments as segment (segment.series)}
                    <div
                      class={segment.top ? 'rounded-t-[3px]' : ''}
                      style:flex="{segment.value} 1 0"
                      style:min-height="1px"
                      style:background-color={fills[segment.series]}
                    ></div>
                  {/each}
                </div>
              {:else}
                <div class="flex h-full w-full max-w-6 items-end gap-px">
                  {#each point.values as value, s (s)}
                    <div
                      class="min-w-0 flex-1 rounded-t-[3px]"
                      style:height="{heightPct(value, scale.top, 1.2)}%"
                      style:background-color={fills[s]}
                    ></div>
                  {/each}
                </div>
              {/if}
            </div>
          {/each}
        </div>

        {#each panel.limits as limit (limit.label)}
          {@const at = up(limit.value)}
          <span
            aria-hidden="true"
            class="absolute inset-x-0 h-0 border-t-2 border-dashed"
            style:bottom="{at}%"
            style:border-color={toneColor(limit.tone)}
          ></span>
          <span
            aria-hidden="true"
            class="text-caption-2 text-muted-foreground bg-card absolute left-0 rounded-[3px] px-1 whitespace-nowrap"
            style:bottom={at > 88 ? undefined : `calc(${at}% + 3px)`}
            style:top={at > 88 ? `calc(${100 - at}% + 3px)` : undefined}
          >
            {limit.label}
          </span>
        {/each}

        {#if selected !== null}
          <span
            aria-hidden="true"
            class="bg-foreground/50 absolute inset-y-0 w-px"
            style:left="{centre(selected)}%"
          ></span>
        {/if}
      </div>

      <div class="relative mt-1 h-5" aria-hidden="true">
        {#each ticks as tick (tick.index)}
          <span
            class="text-caption-2 text-muted-foreground absolute whitespace-nowrap tabular-nums"
            style:left="{centre(tick.index)}%"
            style:transform={tick.index === ticks[0]?.index && centre(tick.index) < 12
              ? 'translateX(-30%)'
              : 'translateX(-50%)'}
          >
            {tick.label}
          </span>
        {/each}
      </div>
    </div>
  </div>

  {#if panel.series.length > 1 || panel.limits.length > 0}
    <ul class="mt-3 flex flex-wrap gap-x-4 gap-y-1.5">
      {#each panel.series as s, i (s.key)}
        <li class="flex items-center gap-1.5">
          <Swatch fill={fills[i]} tone={s.tone} />
          <span class="text-footnote text-muted-foreground md:text-xs">{s.label}</span>
        </li>
      {/each}
      {#each panel.limits as limit (limit.label)}
        <li class="flex items-center gap-1.5">
          <Swatch fill={toneColor(limit.tone)} tone={limit.tone} shape="dash" />
          <span class="text-footnote text-muted-foreground md:text-xs">
            {limit.label} ({formatValue(panel.format, limit.value)})
          </span>
        </li>
      {/each}
    </ul>
  {/if}

  {#snippet table()}
    <table>
      <caption>{panel.title}</caption>
      <thead>
        <tr>
          <th>{panel.bucket === 'hour' ? 'Hour' : 'Day'}</th>
          {#each panel.series as s (s.key)}<th>{s.label}</th>{/each}
        </tr>
      </thead>
      <tbody>
        {#each panel.points as point (point.startUtc)}
          <tr>
            <th scope="row">{bucketName(point.startUtc)}</th>
            {#each point.values as value, i (i)}<td>{formatValue(panel.format, value)}</td>{/each}
          </tr>
        {/each}
      </tbody>
    </table>
  {/snippet}
</PanelFrame>

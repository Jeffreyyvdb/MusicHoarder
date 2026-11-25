<script lang="ts">
  import {
    binShares,
    mapTicks,
    placeMarkers,
    positionOnMap,
    stretches
  } from '#lib/background-work/chart-math.ts';
  import { formatCount, formatMonthYear } from '#lib/background-work/format.ts';
  import { scrub } from '#lib/background-work/scrub.ts';
  import { toneColor } from '#lib/background-work/tone.ts';
  import type { LibraryMapPanel } from '#lib/background-work/types.ts';
  import { IsMobile } from '#lib/hooks/is-mobile.svelte.ts';
  import PanelFrame from './PanelFrame.svelte';
  import Swatch from './Swatch.svelte';

  type Props = { panel: LibraryMapPanel };
  let { panel }: Props = $props();

  const isMobile = new IsMobile();

  let selected = $state<number | null>(null);

  const count = $derived(panel.bins.length);
  const ticks = $derived(mapTicks(panel.bins, isMobile.current ? 3 : 5));
  const done = toneColor('good');
  const waiting = toneColor('neutral');
  const excluded = toneColor('muted');

  const LANE_HEIGHT = 18;
  const PLOT_WIDTH_COMPACT = 290;
  const PLOT_WIDTH_WIDE = 560;
  const MARKER_CHAR_WIDTH = 6.2;
  const MARKER_PADDING = 8;
  const plotWidth = $derived(isMobile.current ? PLOT_WIDTH_COMPACT : PLOT_WIDTH_WIDE);
  const markers = $derived.by(() => {
    const raw = panel.markers.map((m) => ({
      ...m,
      position: positionOnMap(panel.bins, new Date(m.atUtc).getTime()),
      width: (m.label.length * MARKER_CHAR_WIDTH + MARKER_PADDING) / plotWidth
    }));
    const placed = placeMarkers(raw);
    return raw.map((m, i) => ({ ...m, ...placed[i] }));
  });
  const lanes = $derived(Math.max(0, ...markers.map((m) => m.lane + 1)));

  const runs = $derived(stretches(panel.bins));
  const span = (run: { fromUtc: string; toUtc: string }) =>
    `${formatMonthYear(run.fromUtc)} to ${formatMonthYear(run.toUtc)}`;
  const doneRuns = $derived(runs.filter((r) => r.state === 'done'));
  const waitingRuns = $derived(runs.filter((r) => r.state === 'waiting'));
  const where = $derived(
    [
      doneRuns.length > 0 ? `${panel.doneLabel}: ${doneRuns.map(span).join(', ')}.` : '',
      waitingRuns.length > 0 ? `${panel.waitingLabel}: ${waitingRuns.map(span).join(', ')}.` : ''
    ]
      .filter(Boolean)
      .join(' ')
  );

  const totals = $derived(
    panel.bins.reduce(
      (sum, b) => ({
        done: sum.done + b.done,
        waiting: sum.waiting + b.waiting,
        excluded: sum.excluded + b.excluded
      }),
      { done: 0, waiting: 0, excluded: 0 }
    )
  );

  const readout = $derived.by(() => {
    if (selected === null) return null;
    const bin = panel.bins[selected];
    if (!bin) return null;
    const songs = binShares(bin).total;
    return {
      when: `${formatMonthYear(bin.fromUtc)} to ${formatMonthYear(bin.toUtc)}`,
      songs: formatCount(songs),
      rows: [
        { label: panel.doneLabel, value: formatCount(bin.done), fill: done, tone: 'good' as const },
        {
          label: panel.waitingLabel,
          value: formatCount(bin.waiting),
          fill: waiting,
          tone: 'neutral' as const
        },
        {
          label: panel.excludedLabel,
          value: formatCount(bin.excluded),
          fill: excluded,
          tone: 'muted' as const
        }
      ]
    };
  });

  const readoutText = $derived(
    readout
      ? `${readout.when}, ${readout.songs} songs: ${readout.rows.map((r) => `${r.label} ${r.value}`).join(', ')}`
      : `${panel.title}. ${where}`
  );
</script>

<PanelFrame title={panel.title} caption={panel.caption} kind="library-map">
  <div class="min-h-[6.5rem] md:min-h-[3.25rem]">
    {#if readout}
      <p class="text-footnote text-muted-foreground md:text-xs">
        {readout.when} · {readout.songs} songs
      </p>
      <ul class="mt-0.5 flex flex-col gap-0.5 md:flex-row md:flex-wrap md:gap-x-4">
        {#each readout.rows as row, i (i)}
          <li class="flex min-w-0 items-center gap-1.5">
            <Swatch fill={row.fill} tone={row.tone} />
            <span class="text-subheadline font-semibold tabular-nums md:text-sm">{row.value}</span>
            <span class="text-footnote text-muted-foreground truncate md:text-xs">{row.label}</span>
          </li>
        {/each}
      </ul>
    {:else}
      <p class="text-footnote text-muted-foreground md:text-xs">
        Touch or hover the strip to read one slice of the library.
      </p>
      {#if where}<p class="text-footnote mt-0.5 md:text-xs">{where}</p>{/if}
    {/if}
  </div>

  <div class="relative mt-2">
    {#if lanes > 0}
      <div class="relative" style:height="{lanes * LANE_HEIGHT + 4}px" aria-hidden="true">
        {#each markers as marker, i (i)}
          <span
            class="bg-foreground absolute bottom-0 w-0.5 -translate-x-1/2"
            style:left="{marker.position * 100}%"
            style:height="{marker.lane * LANE_HEIGHT + 4}px"
          ></span>
          <span
            class="text-caption-2 text-foreground bg-card absolute z-10 rounded-[3px] px-1 font-medium whitespace-nowrap"
            style:left="{marker.position * 100}%"
            style:bottom="{marker.lane * LANE_HEIGHT + 4}px"
            style:transform={marker.align === 'start'
              ? 'translateX(-4px)'
              : marker.align === 'end'
                ? 'translateX(calc(-100% + 4px))'
                : 'translateX(-50%)'}
          >
            {marker.label}
          </span>
        {/each}
      </div>
    {/if}

    <div
      role="slider"
      tabindex="0"
      aria-label={`${panel.title}, inspect one slice of the library`}
      aria-orientation="horizontal"
      aria-valuemin={0}
      aria-valuemax={Math.max(0, count - 1)}
      aria-valuenow={selected ?? 0}
      aria-valuetext={readoutText}
      class="focus-visible:ring-ring relative h-20 rounded-sm select-none focus-visible:ring-2 focus-visible:outline-hidden md:h-24"
      use:scrub={{ count, index: selected, onchange: (i) => (selected = i) }}
    >
      <div class="absolute inset-0 flex gap-px" aria-hidden="true">
        {#each panel.bins as bin, i (i)}
          <div
            class="flex h-full min-w-0 flex-1 flex-col-reverse overflow-hidden rounded-[2px]"
            style:outline={selected === i ? '2px solid var(--foreground)' : undefined}
            style:outline-offset="1px"
          >
            <div style:flex="{bin.done} 1 0" style:background-color={done}></div>
            <div style:flex="{bin.waiting} 1 0" style:background-color={waiting}></div>
            <div style:flex="{bin.excluded} 1 0" style:background-color={excluded}></div>
          </div>
        {/each}
      </div>
      {#each markers as marker, i (i)}
        <span
          aria-hidden="true"
          class="bg-foreground absolute inset-y-0 w-0.5 -translate-x-1/2"
          style:left="{marker.position * 100}%"
          style:box-shadow="0 0 0 1px var(--card)"
        ></span>
      {/each}
    </div>

    <div class="relative mt-1 h-5" aria-hidden="true">
      {#each ticks as tick, i (i)}
        <span
          class="text-caption-2 text-muted-foreground absolute whitespace-nowrap tabular-nums"
          style:left="{tick.position * 100}%"
          style:transform={tick.position === 0
            ? 'none'
            : tick.position === 1
              ? 'translateX(-100%)'
              : 'translateX(-50%)'}
        >
          {tick.label}
        </span>
      {/each}
    </div>
  </div>

  <ul class="mt-3 flex flex-wrap gap-x-4 gap-y-1.5">
    <li class="flex items-center gap-1.5">
      <Swatch fill={done} tone="good" />
      <span class="text-footnote text-muted-foreground md:text-xs">
        {panel.doneLabel}
        {formatCount(totals.done)}
      </span>
    </li>
    <li class="flex items-center gap-1.5">
      <Swatch fill={waiting} tone="neutral" />
      <span class="text-footnote text-muted-foreground md:text-xs">
        {panel.waitingLabel}
        {formatCount(totals.waiting)}
      </span>
    </li>
    <li class="flex items-center gap-1.5">
      <Swatch fill={excluded} tone="muted" />
      <span class="text-footnote text-muted-foreground md:text-xs">
        {panel.excludedLabel}
        {formatCount(totals.excluded)}
      </span>
    </li>
  </ul>

  {#snippet table()}
    <table>
      <caption>{panel.title}</caption>
      <thead>
        <tr>
          <th>Slice of the library</th>
          <th>{panel.doneLabel}</th>
          <th>{panel.waitingLabel}</th>
          <th>{panel.excludedLabel}</th>
        </tr>
      </thead>
      <tbody>
        {#each panel.bins as bin, i (i)}
          <tr>
            <th scope="row">{formatMonthYear(bin.fromUtc)} to {formatMonthYear(bin.toUtc)}</th>
            <td>{formatCount(bin.done)}</td>
            <td>{formatCount(bin.waiting)}</td>
            <td>{formatCount(bin.excluded)}</td>
          </tr>
        {/each}
      </tbody>
    </table>
  {/snippet}
</PanelFrame>

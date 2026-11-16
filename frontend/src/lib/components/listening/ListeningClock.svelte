<script lang="ts">
  import { clockArcs, formatHour, WEEKDAY_LABELS } from '#lib/listening.ts';

  type Props = {
    /** Streams per local hour, 24 entries. */
    clock: number[];
    /** Streams per weekday, Monday first. */
    weekdays: number[];
    peakHour?: number;
  };
  const { clock, weekdays, peakHour }: Props = $props();

  const SIZE = 200;
  const arcs = $derived(clockArcs(clock, SIZE / 2, SIZE / 2, 52, 92));
  const weekdayMax = $derived(Math.max(1, ...weekdays));
  const summary = $derived(
    peakHour === undefined
      ? 'Listening clock: no streams yet'
      : `Listening clock: most streams at ${formatHour(peakHour)}`
  );

  const MARKS = [0, 6, 12, 18];
  function markPoint(hour: number) {
    const angle = ((hour + 0.5) / 24) * Math.PI * 2;
    const r = 102;
    return { x: SIZE / 2 + r * Math.sin(angle), y: SIZE / 2 - r * Math.cos(angle) };
  }
</script>

<div class="flex flex-col items-center gap-6 md:flex-row md:items-center md:gap-10">
  <svg
    viewBox="-14 -14 {SIZE + 28} {SIZE + 28}"
    role="img"
    aria-label={summary}
    class="aspect-square w-full max-w-[280px] shrink-0"
  >
    {#each arcs as arc (arc.hour)}
      <path
        d={arc.path}
        style:fill="color-mix(in oklab, var(--primary) {Math.round(arc.level * 100)}%,
        var(--secondary))"
        stroke={arc.hour === peakHour ? 'var(--foreground)' : 'none'}
        stroke-width="1.5"
      />
    {/each}
    {#each MARKS as hour (hour)}
      {@const p = markPoint(hour - 0.5)}
      <text
        x={p.x}
        y={p.y}
        text-anchor="middle"
        dominant-baseline="central"
        font-size="9"
        class="fill-muted-foreground tabular-nums"
      >
        {String(hour).padStart(2, '0')}
      </text>
    {/each}
    <text
      x={SIZE / 2}
      y={SIZE / 2 - 6}
      text-anchor="middle"
      font-size="9"
      class="fill-muted-foreground">Peak</text
    >
    <text
      x={SIZE / 2}
      y={SIZE / 2 + 12}
      text-anchor="middle"
      font-size="16"
      font-weight="700"
      class="fill-foreground tabular-nums"
    >
      {peakHour === undefined ? '–' : formatHour(peakHour)}
    </text>
  </svg>

  <div class="w-full min-w-0 flex-1" aria-hidden="true">
    <div class="flex h-28 items-end gap-2">
      {#each weekdays as streams, i (i)}
        <div class="flex h-full min-w-0 flex-1 flex-col items-center justify-end gap-1.5">
          <div
            class="bg-primary w-full rounded-t-md"
            style:height="{streams === 0 ? 2 : Math.max(4, (streams / weekdayMax) * 100)}%"
            style:min-height="2px"
          ></div>
        </div>
      {/each}
    </div>
    <div class="mt-1.5 flex gap-2">
      {#each WEEKDAY_LABELS as label (label)}
        <span class="text-caption-2 text-muted-foreground min-w-0 flex-1 text-center">{label}</span>
      {/each}
    </div>
  </div>

  <ul class="sr-only">
    {#each clock as streams, hour (hour)}
      <li>{formatHour(hour)}: {streams} {streams === 1 ? 'stream' : 'streams'}</li>
    {/each}
    {#each weekdays as streams, i (i)}
      <li>{WEEKDAY_LABELS[i]}: {streams} {streams === 1 ? 'stream' : 'streams'}</li>
    {/each}
  </ul>
</div>

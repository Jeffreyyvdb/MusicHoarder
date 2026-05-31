<script lang="ts">
  // Lightweight hand-rolled SVG line chart (no chart dependency). Plots a sequence of values at
  // evenly-spaced x positions (one per snapshot). Nulls break the line into gaps. Hovering a point
  // shows its label + formatted value.
  interface Props {
    values: (number | null)[];
    labels: string[];
    color?: string;
    format?: (v: number) => string;
    /** Force the y-axis lower bound (e.g. 0 for counts). Defaults to the data min. */
    yMin?: number;
    /** Force the y-axis upper bound (e.g. 100 for percentages). Defaults to the data max. */
    yMax?: number;
    height?: number;
  }

  let {
    values,
    labels,
    color = 'var(--color-primary, #6366f1)',
    format = (v: number) => String(Math.round(v * 100) / 100),
    yMin,
    yMax,
    height = 56
  }: Props = $props();

  const W = 100; // viewBox width units; svg stretches to container width
  const H = 36; // viewBox height units
  const PAD = 3;

  const present = $derived(values.filter((v): v is number => v != null));
  const lo = $derived(yMin ?? (present.length ? Math.min(...present) : 0));
  const hi = $derived(yMax ?? (present.length ? Math.max(...present) : 1));

  function x(i: number): number {
    if (values.length <= 1) return W / 2;
    return PAD + (i / (values.length - 1)) * (W - 2 * PAD);
  }
  function y(v: number): number {
    const span = hi - lo || 1;
    const t = (v - lo) / span;
    return H - PAD - t * (H - 2 * PAD);
  }

  // Build polyline segments, breaking on nulls.
  const segments = $derived.by(() => {
    const segs: { x: number; y: number }[][] = [];
    let cur: { x: number; y: number }[] = [];
    values.forEach((v, i) => {
      if (v == null) {
        if (cur.length) segs.push(cur);
        cur = [];
      } else {
        cur.push({ x: x(i), y: y(v) });
      }
    });
    if (cur.length) segs.push(cur);
    return segs;
  });

  let hover = $state<number | null>(null);
</script>

<div class="relative w-full" style="height: {height}px">
  <svg viewBox="0 0 {W} {H}" preserveAspectRatio="none" class="h-full w-full overflow-visible">
    {#each segments as seg, si (si)}
      <polyline
        points={seg.map((p) => `${p.x},${p.y}`).join(' ')}
        fill="none"
        stroke={color}
        stroke-width="0.8"
        stroke-linecap="round"
        stroke-linejoin="round"
        vector-effect="non-scaling-stroke"
      />
    {/each}
    {#each values as v, i (i)}
      {#if v != null}
        <circle
          cx={x(i)}
          cy={y(v)}
          r={hover === i ? 1.8 : 1.1}
          fill={color}
          vector-effect="non-scaling-stroke"
          role="presentation"
          onmouseenter={() => (hover = i)}
          onmouseleave={() => (hover = null)}
        />
      {/if}
    {/each}
  </svg>
  {#if hover != null && values[hover] != null}
    <div
      class="pointer-events-none absolute -top-1 z-10 -translate-x-1/2 -translate-y-full rounded-md border border-border bg-popover px-2 py-1 text-xs whitespace-nowrap text-popover-foreground shadow-md"
      style="left: {x(hover)}%"
    >
      <div class="font-medium">{format(values[hover] as number)}</div>
      <div class="text-muted-foreground">{labels[hover] ?? ''}</div>
    </div>
  {/if}
</div>

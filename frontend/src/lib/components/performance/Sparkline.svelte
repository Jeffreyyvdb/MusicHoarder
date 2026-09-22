<script lang="ts">
  // Lightweight hand-rolled SVG line chart (no chart dependency). Plots a sequence of values at
  // evenly-spaced x positions (one per snapshot). Nulls break the line into gaps. Pointing at the
  // chart shows the nearest point's label + formatted value; a tap or click pins it, so the values
  // are reachable on a touch screen too. Each point carries its own accessible name.
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
    /** What the series measures (the card title) — names the chart for assistive tech. */
    name?: string;
  }

  let {
    values,
    labels,
    color = 'var(--color-primary, #6366f1)',
    format = (v: number) => String(Math.round(v * 100) / 100),
    yMin,
    yMax,
    height = 56,
    name
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

  // `hover` follows a mouse; `pinned` is set by a tap or click and survives the pointer leaving,
  // which is the only way a finger (which never hovers) gets to read a value.
  let hover = $state<number | null>(null);
  let pinned = $state<number | null>(null);
  const active = $derived(hover ?? pinned);

  let root = $state<HTMLDivElement | null>(null);

  /** Nearest non-null point to a pointer x — the whole chart is the target, not a 2px dot. */
  function nearestIndex(clientX: number): number | null {
    if (!root) return null;
    const rect = root.getBoundingClientRect();
    if (rect.width === 0) return null;
    const vx = ((clientX - rect.left) / rect.width) * W;
    let best: number | null = null;
    let bestD = Infinity;
    values.forEach((v, i) => {
      if (v == null) return;
      const d = Math.abs(x(i) - vx);
      if (d < bestD) {
        bestD = d;
        best = i;
      }
    });
    return best;
  }

  function onPointerMove(e: PointerEvent) {
    if (e.pointerType !== 'mouse') return;
    hover = nearestIndex(e.clientX);
  }

  function onPointerLeave(e: PointerEvent) {
    if (e.pointerType === 'mouse') hover = null;
  }

  function onPointerUp(e: PointerEvent) {
    const i = nearestIndex(e.clientX);
    if (i == null) return;
    pinned = pinned === i ? null : i;
  }

  function pointLabel(i: number, v: number): string {
    return `${labels[i] ?? `Point ${i + 1}`}: ${format(v)}`;
  }
</script>

<div
  bind:this={root}
  class="relative w-full cursor-crosshair touch-pan-y"
  style="height: {height}px"
  role="group"
  aria-label={name ? `${name} over time` : 'Trend over time'}
  onpointermove={onPointerMove}
  onpointerleave={onPointerLeave}
  onpointerup={onPointerUp}
>
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
        aria-hidden="true"
      />
    {/each}
    {#each values as v, i (i)}
      {#if v != null}
        <circle
          cx={x(i)}
          cy={y(v)}
          r={active === i ? 1.8 : 1.1}
          fill={color}
          vector-effect="non-scaling-stroke"
          role="img"
          aria-label={pointLabel(i, v)}
        />
      {/if}
    {/each}
  </svg>
  {#if active != null && values[active] != null}
    <div
      class="pointer-events-none absolute -top-1 z-10 -translate-x-1/2 -translate-y-full rounded-md border border-border bg-popover px-2 py-1 text-xs whitespace-nowrap text-popover-foreground shadow-md"
      style="left: {x(active)}%"
      aria-hidden="true"
    >
      <div class="font-medium">{format(values[active] as number)}</div>
      <div class="text-muted-foreground">{labels[active] ?? ''}</div>
    </div>
  {/if}
</div>

import { formatBytesShort, formatCompactCount } from '#lib/formatters.ts';
import { formatDayBucket, formatMonthYear, type MomentOptions } from './format.ts';
import type { BackgroundFormat, LibraryMapPanel, SeriesPanel } from './types.ts';

// The geometry behind the charts, with no DOM in it: scales, ticks, stacks and the library map's
// bins. A chart component only turns these numbers into percentages of its own box, which is why
// none of them needs the pixel size of anything.

export interface AxisScale {
  /** The value at the top of the plot. */
  top: number;
  /** Tick values from 0 to `top`. */
  ticks: number[];
}

function niceStep(rough: number): number {
  const magnitude = 10 ** Math.floor(Math.log10(rough));
  const normal = rough / magnitude;
  const nice = normal <= 1 ? 1 : normal <= 2 ? 2 : normal <= 5 ? 5 : 10;
  return nice * magnitude;
}

/** Round ticks from 0 up past `max`: 0, 2,000, 4,000 for a max of 3,700. */
export function niceTicks(max: number, target = 4): AxisScale {
  if (!Number.isFinite(max) || max <= 0) return { top: 1, ticks: [0, 1] };
  const step = niceStep(max / target);
  const count = Math.ceil(max / step - 1e-9);
  const ticks = Array.from({ length: count + 1 }, (_, i) => +(i * step).toPrecision(12));
  return { top: ticks[ticks.length - 1], ticks };
}

function durationUnit(max: number): number {
  if (max < 120) return 1;
  if (max < 7200) return 60;
  if (max < 172_800) return 3600;
  return 86_400;
}

/** The scale of a y axis in this format. A duration steps in whole minutes, hours or days, never in 1,000 s. */
export function axisScale(format: BackgroundFormat, max: number): AxisScale {
  if (format === 'duration') {
    const unit = durationUnit(max);
    const scale = niceTicks(max / unit);
    return { top: scale.top * unit, ticks: scale.ticks.map((t) => t * unit) };
  }
  if (format === 'percent') {
    const scale = niceTicks(Math.min(max, 1) * 100, 4);
    return { top: scale.top / 100, ticks: scale.ticks.map((t) => t / 100) };
  }
  return niceTicks(max);
}

/** A tick label: compact, one style per axis. */
export function formatTick(format: BackgroundFormat, value: number): string {
  if (value === 0) return '0';
  switch (format) {
    case 'count':
      return value >= 10_000 ? formatCompactCount(value) : Math.round(value).toLocaleString();
    case 'bytes':
      return formatBytesShort(value);
    case 'percent':
      return `${Math.round(value * 100)}%`;
    case 'duration': {
      if (value < 60) return `${Math.round(value)} s`;
      if (value < 3600) return `${+(value / 60).toFixed(1)} min`;
      if (value < 86_400) return `${+(value / 3600).toFixed(1)} h`;
      return `${+(value / 86_400).toFixed(1)} d`;
    }
  }
}

/** The largest a column reaches: the stack's total when stacked, else its tallest series; limits count too. */
export function seriesMax(panel: Pick<SeriesPanel, 'points' | 'stacked' | 'limits'>): number {
  let max = 0;
  for (const point of panel.points) {
    const value = panel.stacked
      ? point.values.reduce((sum, v) => sum + Math.max(0, v), 0)
      : Math.max(0, ...point.values);
    max = Math.max(max, value);
  }
  for (const limit of panel.limits) max = Math.max(max, limit.value);
  return max;
}

export interface StackSegment {
  /** Index into the panel's `series`. */
  series: number;
  value: number;
  /** Height as a percentage of the plot. */
  heightPct: number;
  /** The last (highest) segment of its column: the one with a rounded cap. */
  top: boolean;
}

/** One column's segments from the baseline up, zeros left out. */
export function stackSegments(values: readonly number[], top: number): StackSegment[] {
  const segments: StackSegment[] = [];
  values.forEach((value, series) => {
    if (value > 0)
      segments.push({ series, value, heightPct: top > 0 ? (value / top) * 100 : 0, top: false });
  });
  if (segments.length > 0) segments[segments.length - 1].top = true;
  return segments;
}

/** A value's height as a percentage of the plot, with a floor so a real value never vanishes. */
export function heightPct(value: number, top: number, minPct = 0): number {
  if (value <= 0 || top <= 0) return 0;
  return Math.min(100, Math.max(minPct, (value / top) * 100));
}

// ── time axis ───────────────────────────────────────────────────────────────────────────────────

function localHour(date: Date, timeZone?: string): number {
  const parts = new Intl.DateTimeFormat('en-GB', {
    hour: 'numeric',
    hourCycle: 'h23',
    timeZone
  }).formatToParts(date);
  return Number(parts.find((p) => p.type === 'hour')?.value ?? 0);
}

export interface TimeTick {
  index: number;
  label: string;
}

/**
 * Where an x axis of time buckets is labelled: each local midnight for hours (6-hourly when the
 * range is shorter than a day), each day for days, thinned to `maxLabels`.
 */
export function timeTicks(
  starts: readonly string[],
  bucket: 'hour' | 'day',
  maxLabels: number,
  options: MomentOptions = {}
): TimeTick[] {
  const dates = starts.map((s) => new Date(s));
  let candidates: TimeTick[] = [];
  if (bucket === 'day') {
    candidates = dates.map((d, index) => ({
      index,
      label: formatDayBucket(d.toISOString(), options)
    }));
  } else {
    const midnights = dates.flatMap((d, index) =>
      localHour(d, options.timeZone) === 0 ? [index] : []
    );
    if (midnights.length >= 2) {
      candidates = midnights.map((index) => ({
        index,
        label: dates[index].toLocaleDateString(options.locale, {
          weekday: 'short',
          day: 'numeric',
          timeZone: options.timeZone
        })
      }));
    } else {
      candidates = dates.flatMap((d, index) =>
        localHour(d, options.timeZone) % 6 === 0
          ? [
              {
                index,
                label: d.toLocaleTimeString(options.locale, {
                  hour: '2-digit',
                  minute: '2-digit',
                  hour12: false,
                  timeZone: options.timeZone
                })
              }
            ]
          : []
      );
    }
  }
  if (candidates.length <= maxLabels) return candidates;
  const stride = Math.ceil(candidates.length / maxLabels);
  return candidates.filter((_, i) => i % stride === 0);
}

// ── library map ─────────────────────────────────────────────────────────────────────────────────

type Bin = LibraryMapPanel['bins'][number];

export interface BinShares {
  total: number;
  done: number;
  waiting: number;
  excluded: number;
}

/** A bin's three parts as fractions of its songs (bins hold equal counts, but the last can come up short). */
export function binShares(bin: Bin): BinShares {
  const total = bin.done + bin.waiting + bin.excluded;
  if (total <= 0) return { total: 0, done: 0, waiting: 0, excluded: 0 };
  return {
    total,
    done: bin.done / total,
    waiting: bin.waiting / total,
    excluded: bin.excluded / total
  };
}

/** Where a moment falls along the strip, 0 to 1: bins are equal in songs, not in time, so it is placed inside its own bin. */
export function positionOnMap(bins: readonly Bin[], atMs: number): number {
  if (bins.length === 0) return 0;
  const first = new Date(bins[0].fromUtc).getTime();
  if (atMs <= first) return 0;
  for (let i = 0; i < bins.length; i++) {
    const from = new Date(bins[i].fromUtc).getTime();
    const to = new Date(bins[i].toUtc).getTime();
    if (atMs < to || i === bins.length - 1) {
      const span = to - from;
      const within = span > 0 ? Math.min(1, Math.max(0, (atMs - from) / span)) : 0;
      return (i + within) / bins.length;
    }
  }
  return 1;
}

export interface MapTick {
  /** 0 to 1 along the strip. */
  position: number;
  label: string;
}

/** Evenly spaced axis labels, each the date at that point of the library; the last is the newest bin's end. */
export function mapTicks(
  bins: readonly Bin[],
  count: number,
  options: MomentOptions = {}
): MapTick[] {
  if (bins.length === 0 || count < 2) return [];
  return Array.from({ length: count }, (_, i) => {
    const position = i / (count - 1);
    const at = position * bins.length;
    const index = Math.min(bins.length - 1, Math.floor(at));
    const iso = i === count - 1 ? bins[bins.length - 1].toUtc : bins[index].fromUtc;
    return { position, label: formatMonthYear(iso, options) };
  });
}

export type MapState = 'done' | 'waiting' | 'excluded';

export interface Stretch {
  state: MapState;
  from: number;
  to: number;
  fromUtc: string;
  toUtc: string;
  /** Songs in the stretch, per the bins. */
  songs: number;
}

function dominant(bin: Bin): MapState | null {
  if (bin.done + bin.waiting + bin.excluded <= 0) return null;
  if (bin.done >= bin.waiting && bin.done >= bin.excluded) return 'done';
  return bin.waiting >= bin.excluded ? 'waiting' : 'excluded';
}

/** Runs of neighbouring bins whose largest part is the same: "done up to 2019, waiting from there". */
export function stretches(bins: readonly Bin[]): Stretch[] {
  const runs: Stretch[] = [];
  bins.forEach((bin, i) => {
    const state = dominant(bin);
    if (state === null) return;
    const songs = bin.done + bin.waiting + bin.excluded;
    const last = runs[runs.length - 1];
    if (last && last.state === state && last.to === i - 1) {
      last.to = i;
      last.toUtc = bin.toUtc;
      last.songs += songs;
    } else {
      runs.push({ state, from: i, to: i, fromUtc: bin.fromUtc, toUtc: bin.toUtc, songs });
    }
  });
  return runs;
}

export interface MarkerPlacement {
  position: number;
  /** Which side of its tick the label hangs on, so it stays inside the strip. */
  align: 'start' | 'center' | 'end';
  lane: number;
}

/**
 * Rows for marker labels so none overlaps another. `labelWidths` and `position` share a unit
 * (fractions of the strip); a label near an edge hangs inward.
 */
export function placeMarkers(
  markers: readonly { position: number; width: number }[],
  gap = 0.015
): MarkerPlacement[] {
  const order = markers.map((_, i) => i).sort((a, b) => markers[a].position - markers[b].position);
  const laneEnds: number[] = [];
  const placed: MarkerPlacement[] = new Array(markers.length);
  for (const i of order) {
    const { position, width } = markers[i];
    const align = position < 0.25 ? 'start' : position > 0.75 ? 'end' : 'center';
    const start =
      align === 'start' ? position : align === 'end' ? position - width : position - width / 2;
    const end = start + width;
    let lane = laneEnds.findIndex((laneEnd) => laneEnd + gap <= start);
    if (lane === -1) lane = laneEnds.length;
    laneEnds[lane] = end;
    placed[i] = { position, align, lane };
  }
  return placed;
}

// ── sparkline ───────────────────────────────────────────────────────────────────────────────────

/** Each value's column height as a percentage of the tallest; a day with work never rounds to nothing. */
export function sparkHeights(values: readonly number[]): number[] {
  const max = Math.max(0, ...values);
  if (max <= 0) return values.map(() => 0);
  return values.map((v) => (v <= 0 ? 0 : Math.max(8, (v / max) * 100)));
}

/** A fraction of a whole, clamped to the bar's range. */
export function coverage(value: number, of: number | null | undefined): number | null {
  if (of == null || of <= 0) return null;
  return Math.min(1, Math.max(0, value / of));
}

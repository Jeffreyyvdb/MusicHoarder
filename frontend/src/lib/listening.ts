import type { ListeningDay, ListeningRange, ListeningRecentStream } from '#lib/api-client.ts';

export const LISTENING_RANGES: { value: ListeningRange; label: string; long: string }[] = [
  { value: '4w', label: '4 weeks', long: 'the last 4 weeks' },
  { value: '6m', label: '6 months', long: 'the last 6 months' },
  { value: '1y', label: '1 year', long: 'the last year' },
  { value: 'all', label: 'Lifetime', long: 'all time' }
];

export const DEFAULT_LISTENING_RANGE: ListeningRange = '4w';

export function parseListeningRange(value: string | null): ListeningRange {
  return LISTENING_RANGES.find((r) => r.value === value)?.value ?? DEFAULT_LISTENING_RANGE;
}

const DAY_MS = 86_400_000;

/** `yyyy-MM-dd` as a UTC midnight, so day arithmetic never meets a daylight-saving shift. */
function dayMs(key: string): number {
  const [y, m, d] = key.split('-').map(Number);
  return Date.UTC(y, m - 1, d);
}

function keyOf(ms: number): string {
  return new Date(ms).toISOString().slice(0, 10);
}

/** The calendar day an instant falls on at `offsetMinutes` from UTC (the server buckets the same way). */
export function dayKeyAt(iso: string, offsetMinutes: number): string {
  const ms = new Date(iso).getTime();
  return Number.isNaN(ms) ? '' : keyOf(ms + offsetMinutes * 60_000);
}

export type RecentGroup = { key: string; label: string; items: ListeningRecentStream[] };

/** Recent streams (newest first) under one header per local day: Today, Yesterday, then "Tue 12 Aug". */
export function groupRecentByDay(
  recent: readonly ListeningRecentStream[],
  offsetMinutes: number,
  nowMs: number = Date.now()
): RecentGroup[] {
  const today = keyOf(nowMs + offsetMinutes * 60_000);
  const yesterday = keyOf(dayMs(today) - DAY_MS);
  const groups: RecentGroup[] = [];
  for (const item of recent) {
    const key = dayKeyAt(item.playedAtUtc, offsetMinutes);
    if (!key) continue;
    const last = groups.at(-1);
    if (last && last.key === key) {
      last.items.push(item);
      continue;
    }
    groups.push({ key, label: dayLabel(key, today, yesterday), items: [item] });
  }
  return groups;
}

function dayLabel(key: string, today: string, yesterday: string): string {
  if (key === today) return 'Today';
  if (key === yesterday) return 'Yesterday';
  return formatDayKey(key, Date.UTC(Number(today.slice(0, 4)), 0, 1));
}

/** "Tue 12 Aug" for a `yyyy-MM-dd` key, with the year when it is not the year of `nowMs`. */
export function formatDayKey(key: string, nowMs: number = Date.now()): string {
  return new Date(dayMs(key)).toLocaleDateString([], {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    ...(Number(key.slice(0, 4)) === new Date(nowMs).getUTCFullYear() ? {} : { year: 'numeric' }),
    timeZone: 'UTC'
  });
}

export type ActivityBucket = { key: string; label: string; streams: number };

/**
 * The activity chart's bars: a bar per day for 4 weeks, per week for 6 months, per month for a
 * year, and per month (per year once it spans more than three) for lifetime. Empty buckets are
 * kept so the bars sit on an even time axis. Capped by construction: 28, 26, 12, then at most 36.
 */
export function activityBuckets(
  days: readonly ListeningDay[],
  range: ListeningRange,
  todayKey: string,
  firstDayKey?: string
): ActivityBucket[] {
  const byDay = new Map(days.map((d) => [d.date, d.streams]));
  const today = dayMs(todayKey);

  if (range === '4w') {
    return Array.from({ length: 28 }, (_, i) => {
      const key = keyOf(today - (27 - i) * DAY_MS);
      return { key, label: formatDayKey(key), streams: byDay.get(key) ?? 0 };
    });
  }

  if (range === '6m') {
    const weekStart = (ms: number) => ms - ((new Date(ms).getUTCDay() + 6) % 7) * DAY_MS;
    const last = weekStart(today);
    return Array.from({ length: 26 }, (_, i) => {
      const start = last - (25 - i) * 7 * DAY_MS;
      let streams = 0;
      for (let d = 0; d < 7; d++) streams += byDay.get(keyOf(start + d * DAY_MS)) ?? 0;
      const key = keyOf(start);
      return { key, label: `Week of ${formatDayKey(key)}`, streams };
    });
  }

  const [ty, tm] = todayKey.split('-').map(Number);
  const first = firstDayKey ?? days[0]?.date ?? todayKey;
  const [fy, fm] = first.split('-').map(Number);
  const spanMonths = (ty - fy) * 12 + (tm - fm) + 1;
  const yearly = range === 'all' && spanMonths > 36;
  const monthCount = range === '1y' ? 12 : Math.min(Math.max(spanMonths, 1), 36);

  if (yearly) {
    const years = Math.min(ty - fy + 1, 36);
    return Array.from({ length: years }, (_, i) => {
      const year = String(ty - (years - 1 - i));
      let streams = 0;
      for (const d of days) if (d.date.startsWith(year)) streams += d.streams;
      return { key: year, label: year, streams };
    });
  }

  return Array.from({ length: monthCount }, (_, i) => {
    const offset = monthCount - 1 - i;
    const index = ty * 12 + (tm - 1) - offset;
    const key = `${Math.floor(index / 12)}-${String((index % 12) + 1).padStart(2, '0')}`;
    let streams = 0;
    for (const d of days) if (d.date.startsWith(key)) streams += d.streams;
    const label = new Date(Date.UTC(Math.floor(index / 12), index % 12, 1)).toLocaleDateString([], {
      month: 'short',
      year: 'numeric',
      timeZone: 'UTC'
    });
    return { key, label, streams };
  });
}

export type ClockArc = { hour: number; path: string; level: number; streams: number };

/** The 24 ring segments of the listening clock, midnight at the top and clockwise. `level` is 0 to 1. */
export function clockArcs(
  clock: readonly number[],
  cx: number,
  cy: number,
  inner: number,
  outer: number
): ClockArc[] {
  const max = Math.max(0, ...clock);
  const gap = 0.012;
  const point = (r: number, angle: number) =>
    `${(cx + r * Math.sin(angle)).toFixed(2)} ${(cy - r * Math.cos(angle)).toFixed(2)}`;
  return clock.map((streams, hour) => {
    const a0 = (hour / 24) * Math.PI * 2 + gap;
    const a1 = ((hour + 1) / 24) * Math.PI * 2 - gap;
    const path = [
      `M ${point(inner, a0)}`,
      `L ${point(outer, a0)}`,
      `A ${outer} ${outer} 0 0 1 ${point(outer, a1)}`,
      `L ${point(inner, a1)}`,
      `A ${inner} ${inner} 0 0 0 ${point(inner, a0)}`,
      'Z'
    ].join(' ');
    return { hour, path, streams, level: max > 0 ? streams / max : 0 };
  });
}

export function formatHour(hour: number): string {
  return `${String(hour).padStart(2, '0')}:00`;
}

export const WEEKDAY_LABELS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const;

/** Hours listened with one decimal below ten ("4.3"), whole above. */
export function formatHours(minutes: number): string {
  const hours = minutes / 60;
  return hours < 10 ? hours.toFixed(1) : Math.round(hours).toLocaleString();
}

export function streamsLabel(streams: number, minutes: number): string {
  return `${streams.toLocaleString()} ${streams === 1 ? 'stream' : 'streams'} · ${minutes.toLocaleString()} min`;
}

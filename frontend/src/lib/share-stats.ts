import type { ShareDailyPoint, ShareStatsRow } from '$lib/api-client';

/**
 * The pure half of the Share links page (`/shares`): which list a URL shows, which link it has
 * pushed, and the words and scales the rows and the opens chart are built from. Kept out of the
 * components so the rules are unit-tested rather than eyeballed.
 */

export type ShareListView = 'active' | 'revoked';

/** `/shares?view=revoked` lists the revoked links; anything else, the live ones. */
export function shareListView(url: URL): ShareListView {
  return url.searchParams.get('view') === 'revoked' ? 'revoked' : 'active';
}

/** The pushed link's id (`/shares?link=12`), or null for the list. */
export function shareDetailId(url: URL): number | null {
  const raw = url.searchParams.get('link');
  if (!raw) return null;
  const n = Number(raw);
  return Number.isInteger(n) && n > 0 ? n : null;
}

/** The list href a view lives at — Back from a link returns to the list it was opened from. */
export function shareListHref(view: ShareListView): string {
  return view === 'revoked' ? '/shares?view=revoked' : '/shares';
}

export function isRevoked(row: Pick<ShareStatsRow, 'revokedAtUtc'>): boolean {
  return row.revokedAtUtc != null;
}

/** An album link is named by its album — the track it was made from is an implementation detail. */
export function shareTitle(row: Pick<ShareStatsRow, 'scope' | 'title' | 'album'>): string {
  if (row.scope === 'Album') return row.album?.trim() || row.title;
  return row.title;
}

/** "Song · Daft Punk", "Album · Daft Punk", or just the kind when the artist is unknown. */
export function shareKindLine(row: Pick<ShareStatsRow, 'scope' | 'artist'>): string {
  const kind = row.scope === 'Album' ? 'Album' : 'Song';
  const artist = row.artist?.trim();
  return artist ? `${kind} · ${artist}` : kind;
}

/** "1 open", "1,204 opens". */
export function countLabel(n: number, one: string, many: string): string {
  return `${n.toLocaleString()} ${n === 1 ? one : many}`;
}

/** A source row's name. The server leaves it null when neither referrer nor browser said. */
export function sourceLabel(source: string | null | undefined): string {
  return source?.trim() || 'Direct or unknown';
}

/**
 * The top of the chart's value axis: the smallest 1, 2 or 5 × 10ⁿ that holds the busiest day, so
 * the one labelled tick is a round number and the tallest bar never quite touches the top.
 */
export function niceMax(peak: number): number {
  if (!(peak > 0)) return 1;
  const magnitude = 10 ** Math.floor(Math.log10(peak));
  for (const step of [1, 2, 5, 10]) {
    if (step * magnitude >= peak) return step * magnitude;
  }
  return 10 * magnitude;
}

/** `YYYY-MM-DD` as a local-midnight Date — never `new Date(string)`, which reads it as UTC. */
export function parseDay(day: string): Date {
  const [y, m, d] = day.split('-').map(Number);
  return new Date(y, (m ?? 1) - 1, d ?? 1);
}

/** The chart's tooltip/axis name for a day: "Today", "Yesterday", "Tue 12 Aug". */
export function dayLabel(day: string, now: Date = new Date()): string {
  const date = parseDay(day);
  const midnight = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime();
  const days = Math.round((midnight(now) - midnight(date)) / 86_400_000);
  if (days === 0) return 'Today';
  if (days === 1) return 'Yesterday';
  return date.toLocaleDateString([], {
    weekday: 'short',
    day: 'numeric',
    month: 'short',
    ...(date.getFullYear() === now.getFullYear() ? {} : { year: 'numeric' })
  });
}

/** Totals over a daily series (the window the chart shows, not all time). */
export function sumDaily(daily: readonly ShareDailyPoint[]): { views: number; plays: number } {
  let views = 0;
  let plays = 0;
  for (const d of daily) {
    views += d.views;
    plays += d.plays;
  }
  return { views, plays };
}

/** The busiest day of a series, or null when nobody opened the link in it. */
export function peakDay(daily: readonly ShareDailyPoint[]): ShareDailyPoint | null {
  let best: ShareDailyPoint | null = null;
  for (const d of daily) {
    if (d.views > 0 && (best === null || d.views > best.views)) best = d;
  }
  return best;
}

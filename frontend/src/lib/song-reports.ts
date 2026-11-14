import type { SongReport } from '#lib/api-client.ts';
import { formatDuration } from '#lib/formatters.ts';

/** "Title — Artist", from whichever half is known. */
function line(title: string | null, artist: string | null): string | null {
  const t = title?.trim();
  const a = artist?.trim();
  if (t && a) return `${t} — ${a}`;
  return t || a || null;
}

/** What arrived: the song's own tags, else its file name. */
export function reportedTitle(r: SongReport): string {
  return line(r.title, r.artist) ?? r.fileName ?? `Song ${r.songId ?? r.id}`;
}

/**
 * What was asked for, when the wishlist knew and it reads differently from what arrived (the same
 * words twice tell a reader nothing; a wrong *version* usually has the same name and shows in the
 * duration or the note instead).
 */
export function requestedLine(r: SongReport): string | null {
  const asked = line(r.requestedTitle, r.requestedArtist);
  if (!asked) return null;
  return asked.toLocaleLowerCase() === line(r.title, r.artist)?.toLocaleLowerCase() ? null : asked;
}

/** "3:12, expected 3:45" when the two lengths are more than 5 s apart, else null. */
export function durationMismatch(r: SongReport): string | null {
  if (r.durationMs == null || r.requestedDurationMs == null) return null;
  if (Math.abs(r.durationMs - r.requestedDurationMs) <= 5000) return null;
  return `${formatDuration(r.durationMs / 1000)}, expected ${formatDuration(r.requestedDurationMs / 1000)}`;
}

/**
 * Where the audio came from, as a link: the exact URL the item asked for, or the YouTube video yt-dlp
 * took it from (its source id is the video id). Other providers' ids are not URLs.
 */
export function sourceLink(r: SongReport): string | null {
  if (r.sourceUrl && /^https?:\/\//i.test(r.sourceUrl)) return r.sourceUrl;
  if (
    r.downloadProvider === 'yt-dlp' &&
    r.downloadedSourceId &&
    /^[\w-]{6,64}$/.test(r.downloadedSourceId)
  )
    return `https://www.youtube.com/watch?v=${encodeURIComponent(r.downloadedSourceId)}`;
  return null;
}

/** Reports per download provider, most first — which source gets things wrong. */
export function reportsByProvider(
  reports: readonly SongReport[]
): { provider: string; count: number }[] {
  const counts = new Map<string, number>();
  for (const r of reports) {
    const key = r.downloadProvider ?? 'Unknown';
    counts.set(key, (counts.get(key) ?? 0) + 1);
  }
  return [...counts]
    .map(([provider, count]) => ({ provider, count }))
    .sort((a, b) => b.count - a.count || a.provider.localeCompare(b.provider));
}

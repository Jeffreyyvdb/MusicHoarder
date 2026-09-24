/** Formatters shared across the file-browser components. */

/** "3:58" or "1:02:14" — for individual tracks. */
export function formatDuration(seconds: number | null | undefined): string {
  if (!seconds || !Number.isFinite(seconds) || seconds <= 0) return '—';
  const total = Math.floor(seconds);
  const hrs = Math.floor(total / 3600);
  const mins = Math.floor((total % 3600) / 60);
  const secs = total % 60;
  if (hrs > 0) {
    return `${hrs}:${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
  }
  return `${mins}:${secs.toString().padStart(2, '0')}`;
}

/**
 * "4 h 21 min" / "21 min" / "48 sec" — for totals (an album, a list, a playlist). One style
 * everywhere: minutes are the finest unit once there is a minute to show (Apple Music's album
 * footer reads "21 minutes", never "20 min 56 sec"), rounded to the nearest.
 */
export function formatTotalDuration(seconds: number | null | undefined): string {
  if (!seconds || !Number.isFinite(seconds) || seconds <= 0) return '—';
  if (seconds < 60) return `${Math.round(seconds)} sec`;
  const totalMins = Math.round(seconds / 60);
  const hrs = Math.floor(totalMins / 60);
  const mins = totalMins % 60;
  if (hrs > 0) return mins > 0 ? `${hrs} h ${mins} min` : `${hrs} h`;
  return `${mins} min`;
}

/**
 * "Sep 30, 2026" — a calendar date in the one style the app shows: the month as a word, in the
 * viewer's locale order ("30 Sep 2026" in en-GB), never all-numeric ("9/30/2026") and never ISO
 * outside monospace identifier rows.
 */
export function formatDate(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' });
}

/**
 * A release date as tags carry it — "2021-01-10", "2021-01" or "2021" — in the same style as
 * {@link formatDate}: "Jan 10, 2021", "Jan 2021", "2021". Parsed as a calendar date (not an
 * instant), so it never shifts a day in a timezone west of UTC. Anything else passes through.
 */
export function formatReleaseDate(value: string | number | null | undefined): string {
  if (value == null || value === '') return '—';
  const text = String(value).trim();
  const full = /^(\d{4})-(\d{2})-(\d{2})/.exec(text);
  if (full) {
    const d = new Date(Number(full[1]), Number(full[2]) - 1, Number(full[3]));
    return d.toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' });
  }
  const month = /^(\d{4})-(\d{2})$/.exec(text);
  if (month) {
    const d = new Date(Number(month[1]), Number(month[2]) - 1, 1);
    return d.toLocaleDateString([], { year: 'numeric', month: 'short' });
  }
  return text;
}

/** "Sep 30, 2026, 6:51 AM" — a date with its time of day, to the minute (never the seconds). */
export function formatDateTime(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '—';
  return d.toLocaleString([], {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit'
  });
}

export function formatFileSize(bytes: number | null | undefined): string {
  if (!bytes || !Number.isFinite(bytes) || bytes <= 0) return '—';
  const tib = bytes / 1024 ** 4;
  if (tib >= 1) return `${tib.toFixed(2)} TB`;
  const gib = bytes / (1024 * 1024 * 1024);
  if (gib >= 1) return `${gib.toFixed(2)} GB`;
  const mib = bytes / (1024 * 1024);
  if (mib >= 1) return `${mib.toFixed(1)} MB`;
  const kib = bytes / 1024;
  return `${kib.toFixed(0)} KB`;
}

/**
 * "303 GB" / "1.9 TB" / "512 MB" — for chrome (the sidebar's storage line, a page subtitle) where
 * two decimals are noise. Nothing reads as "0 B", not a dash, because it sits next to a capacity.
 * Binary units labelled the way the rest of the app labels them.
 */
export function formatBytesShort(bytes: number | null | undefined): string {
  if (!bytes || !Number.isFinite(bytes) || bytes <= 0) return '0 B';
  const tib = bytes / 1024 ** 4;
  if (tib >= 1) return `${tib.toFixed(1)} TB`;
  const gib = bytes / 1024 ** 3;
  if (gib >= 1) return `${gib.toFixed(0)} GB`;
  const mib = bytes / 1024 ** 2;
  if (mib >= 1) return `${mib.toFixed(0)} MB`;
  return `${Math.max(1, Math.round(bytes / 1024))} KB`;
}

// Strips Unicode "Other" code points (control, format, surrogate, private-use,
// unassigned) for *display only* — these have no glyph and render as a .notdef
// "tofu" box. Folder/file names that carry a stray control char on disk (it stays
// in the stored path used for I/O) should still read cleanly in the UI.
const NON_PRINTABLE = /\p{C}/gu;

/** Display-safe folder/file name: drops non-rendering code points and trims. */
export function cleanDisplayName(name: string | null | undefined): string {
  return (name ?? '').replace(NON_PRINTABLE, '').trim();
}

/** Two-letter uppercase initials from a title. Falls back to first two chars. */
export function computeInitials(title: string | null | undefined): string {
  if (!title) return '??';
  const letters = title
    .split(/\s+/)
    .filter(Boolean)
    .filter((w) => /[a-z0-9]/i.test(w[0] ?? ''))
    .slice(0, 2)
    .map((w) => (w[0] ?? '').toUpperCase())
    .join('');
  return letters || title.slice(0, 2).toUpperCase() || '??';
}

/** "FLAC 1024kbps" — match the design's bitrate label. */
export function formatBitrate(bitRate: number | null | undefined, extension?: string | null): string {
  const ext = (extension ?? '').replace(/^\./, '').toUpperCase();
  if (!bitRate || bitRate <= 0) return ext || '—';
  return ext ? `${ext} ${bitRate}kbps` : `${bitRate} kbps`;
}

export type FormatFamily = 'FLAC' | 'MP3' | 'AAC' | 'WAV' | 'OGG' | 'OTHER';

/** Collapse a file extension into a broad format family for filtering/grouping. */
export function formatFamily(extension: string | null | undefined): FormatFamily {
  const ext = (extension ?? '').replace(/^\./, '').toLowerCase();
  switch (ext) {
    case 'flac':
      return 'FLAC';
    case 'mp3':
      return 'MP3';
    case 'm4a':
    case 'aac':
      return 'AAC';
    case 'wav':
      return 'WAV';
    case 'ogg':
    case 'opus':
      return 'OGG';
    default:
      return 'OTHER';
  }
}

/**
 * "just now" / "12 min ago" / "3h ago" / "4d ago", falling back to a short date past a month.
 * Lives here rather than on a page because three surfaces had grown near-identical private copies.
 */
export function formatRelativeTime(iso: string, now: number = Date.now()): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '';
  const secs = Math.round((now - then) / 1000);
  if (secs < 60) return 'just now';
  const mins = Math.round(secs / 60);
  if (mins < 60) return `${mins} min ago`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `${hours}h ago`;
  const days = Math.round(hours / 24);
  if (days < 30) return `${days}d ago`;
  return new Date(iso).toLocaleDateString([], { month: 'short', day: 'numeric' });
}

/**
 * The forward-looking twin of {@link formatRelativeTime}: "in a moment" / "in 12 min" / "in 3h",
 * falling back to a short date+time past a day. A timestamp already in the past reads "any moment now".
 */
export function formatRelativeFuture(iso: string, now: number = Date.now()): string {
  const then = new Date(iso).getTime();
  if (Number.isNaN(then)) return '';
  const secs = Math.round((then - now) / 1000);
  if (secs < 0) return 'any moment now';
  if (secs < 60) return 'in a moment';
  const mins = Math.round(secs / 60);
  if (mins < 60) return `in ${mins} min`;
  const hours = Math.round(mins / 60);
  if (hours < 24) return `in ${hours}h`;
  return new Date(iso).toLocaleString([], {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit'
  });
}

/** "Today" / "Yesterday" / "Tue 12 Aug" — the header a day-grouped list puts above its rows. */
export function formatDayLabel(iso: string, now: Date = new Date()): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return '';
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

/** The local calendar day an instant falls on — the key a day-grouped list groups by. */
export function localDayKey(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, '0')}-${String(d.getDate()).padStart(2, '0')}`;
}

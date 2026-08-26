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

/** "1 h 12 min" / "47 min 12 sec" — for album totals. */
export function formatTotalDuration(seconds: number | null | undefined): string {
  if (!seconds || !Number.isFinite(seconds) || seconds <= 0) return '—';
  const total = Math.floor(seconds);
  const hrs = Math.floor(total / 3600);
  const mins = Math.floor((total % 3600) / 60);
  const secs = total % 60;
  if (hrs > 0) {
    return mins > 0 ? `${hrs} h ${mins} min` : `${hrs} h`;
  }
  if (mins > 0) {
    return secs > 0 ? `${mins} min ${secs} sec` : `${mins} min`;
  }
  return `${secs} sec`;
}

export function formatFileSize(bytes: number | null | undefined): string {
  if (!bytes || !Number.isFinite(bytes) || bytes <= 0) return '—';
  const gib = bytes / (1024 * 1024 * 1024);
  if (gib >= 1) return `${gib.toFixed(2)} GB`;
  const mib = bytes / (1024 * 1024);
  if (mib >= 1) return `${mib.toFixed(1)} MB`;
  const kib = bytes / 1024;
  return `${kib.toFixed(0)} KB`;
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

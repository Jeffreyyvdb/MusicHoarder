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

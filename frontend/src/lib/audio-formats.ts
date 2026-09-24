/**
 * Whether this browser can play a song's file as it is, or needs the server to decode it
 * (`?format=wav` on any stream URL: PCM, decoded while it streams, so it starts at once).
 *
 * Only a browser that cannot play a file ever asks for the decoded stream, so a song is streamed as
 * the original bytes everywhere else. Two signals decide it:
 *
 *  • `canPlayType`, asked before loading: '' is the browser saying it certainly cannot play that
 *    type (Safari before iOS 18.4 about Ogg Opus, every browser about WMA);
 *  • a real failure, remembered: a browser can claim a type and still refuse the file, so the
 *    player falls back to the decoded stream when the original fails to load and records the
 *    format here, sending the rest of the queue straight to decoded streams.
 *
 * The record is kept per browser build (the user agent), so an OS update that learns the format
 * gets to try the original again.
 */

/** What to ask `canPlayType` about, by file format (the extension, lowercase, without the dot). */
const MEDIA_TYPES: Record<string, string> = {
  mp3: 'audio/mpeg',
  m4a: 'audio/mp4',
  aac: 'audio/aac',
  flac: 'audio/flac',
  wav: 'audio/wav',
  ogg: 'audio/ogg',
  opus: 'audio/ogg; codecs="opus"',
  wma: 'audio/x-ms-wma',
  aiff: 'audio/aiff',
  aif: 'audio/aiff'
};

const STORAGE_KEY = 'mh:unplayable-formats';

/** A file extension as a format: '.OPUS' → 'opus'. Null when there is none. */
export function formatOf(extension: string | null | undefined): string | null {
  const format = (extension ?? '').trim().replace(/^\./, '').toLowerCase();
  return format || null;
}

/** The decoded (WAV) version of a stream URL. */
export function convertedStreamUrl(streamUrl: string): string {
  return `${streamUrl}${streamUrl.includes('?') ? '&' : '?'}format=wav`;
}

/**
 * True when this browser needs the decoded stream for `format`. An unknown format, or a browser that
 * cannot be asked, gets the original: the player's fallback covers the case where that was wrong.
 */
export function needsConversion(
  format: string | null | undefined,
  canPlayType: ((mediaType: string) => string) | null,
  unplayable: { has(format: string): boolean }
): boolean {
  if (!format) return false;
  if (unplayable.has(format)) return true;
  const mediaType = MEDIA_TYPES[format];
  if (!mediaType || !canPlayType) return false;
  return canPlayType(mediaType) === '';
}

export interface UnplayableFormats {
  has(format: string): boolean;
  add(format: string): void;
}

/**
 * The formats this browser failed to play as they are. Read once; a stored record written by
 * another user agent is ignored. Storage failures leave it working for this page only.
 */
export function createUnplayableFormats(
  storage: Storage | null,
  userAgent: string
): UnplayableFormats {
  const formats = new Set<string>(readStored(storage, userAgent));

  return {
    has: (format) => formats.has(format),
    add(format) {
      if (formats.has(format)) return;
      formats.add(format);
      try {
        storage?.setItem(STORAGE_KEY, JSON.stringify({ userAgent, formats: [...formats] }));
      } catch {
        // Quota or privacy mode: remembered for this page only.
      }
    }
  };
}

function readStored(storage: Storage | null, userAgent: string): string[] {
  try {
    const raw = storage?.getItem(STORAGE_KEY);
    if (!raw) return [];
    const stored: unknown = JSON.parse(raw);
    if (typeof stored !== 'object' || stored === null) return [];
    const { userAgent: storedAgent, formats } = stored as Record<string, unknown>;
    if (storedAgent !== userAgent || !Array.isArray(formats)) return [];
    return formats.filter((f): f is string => typeof f === 'string');
  } catch {
    return [];
  }
}

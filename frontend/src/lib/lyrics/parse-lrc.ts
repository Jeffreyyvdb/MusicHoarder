export type LrcLine = { timeMs: number; text: string };

// Matches one LRC timestamp tag: [mm:ss], [mm:ss.xx] or [mm:ss:xx] (LRCLIB and some
// taggers use a colon before the fractional part). Minutes may run past 99 on long
// tracks, so allow 1–3 digits. The fractional part is centiseconds/milliseconds and is
// optional. Used with the `g` flag so a line carrying several timestamps (LRC repeats a
// lyric at multiple times) yields one entry per tag.
const TIMESTAMP = /\[(\d{1,3}):([0-5]?\d)(?:[.:](\d{1,3}))?\]/g;

/**
 * Parse LRC-format synced lyrics into time-ordered lines.
 *
 * Tolerant by design: handles CRLF (`\r\n`) input, `.`- or `:`-separated fractions,
 * 1–3 digit minutes, and multiple timestamps per line. Metadata-only tags such as
 * `[ar:Artist]` / `[ti:Title]` / `[length:3:21]` carry no numeric `mm:ss` timestamp and
 * are skipped. Returns `[]` when no line carries a parseable timestamp — callers must
 * treat an empty array as "not synced" and fall back to the raw/plain text rather than
 * rendering nothing.
 */
export function parseLrc(lrc: string): LrcLine[] {
  const lines: LrcLine[] = [];
  for (const raw of lrc.split(/\r?\n/)) {
    TIMESTAMP.lastIndex = 0;
    const stamps: number[] = [];
    let lastTagEnd = 0;
    let match: RegExpExecArray | null;
    while ((match = TIMESTAMP.exec(raw)) !== null) {
      const mins = parseInt(match[1], 10);
      const secs = parseInt(match[2], 10);
      // Normalise the fractional part to milliseconds: ".5" → 500ms, ".50" → 500ms,
      // ".500" → 500ms.
      const frac = match[3] ? parseInt(match[3].padEnd(3, '0'), 10) : 0;
      stamps.push(mins * 60000 + secs * 1000 + frac);
      lastTagEnd = match.index + match[0].length;
    }
    if (stamps.length === 0) continue;
    const text = raw.slice(lastTagEnd).trim();
    for (const timeMs of stamps) lines.push({ timeMs, text });
  }
  lines.sort((a, b) => a.timeMs - b.timeMs);
  return lines;
}

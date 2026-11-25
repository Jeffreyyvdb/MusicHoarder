import type { BackgroundJobState, BackgroundTone } from './types.ts';

// The one place a BackgroundTone becomes a colour. The tokens are in app.css (`--viz-*`, light,
// dark and Increase Contrast), so a chart never names a hex and a failure is the same red on every
// chart.

const TONE_VARIABLE: Record<BackgroundTone, string> = {
  good: '--viz-good',
  ai: '--viz-ai',
  neutral: '--viz-neutral',
  pending: '--viz-pending',
  warning: '--viz-warning',
  bad: '--viz-bad',
  muted: '--viz-muted'
};

/** The fill of a mark that carries this tone. */
export function toneColor(tone: BackgroundTone): string {
  return `var(${TONE_VARIABLE[tone]})`;
}

/**
 * Fills for a run of marks, in drawing order. A tone used again in the same chart (AI-timed and
 * AI-written lyrics are both `ai`) is stepped toward the card on each repeat, so two neighbours of
 * one tone still read as two parts; the legend names them.
 */
export function toneFills(tones: readonly BackgroundTone[]): string[] {
  const seen = new Map<BackgroundTone, number>();
  return tones.map((tone) => {
    const repeat = seen.get(tone) ?? 0;
    seen.set(tone, repeat + 1);
    const base = toneColor(tone);
    if (repeat === 0) return base;
    const keep = Math.max(30, 64 - (repeat - 1) * 22);
    return `color-mix(in oklab, ${base} ${keep}%, var(--card))`;
  });
}

/** Text that states a tone wears the text token, never the mark's colour. Only the two that mean trouble differ. */
export function toneTextClass(tone: BackgroundTone): string {
  if (tone === 'bad') return 'text-destructive-text';
  if (tone === 'warning') return 'text-warning-text';
  return 'text-foreground';
}

/** A tone that means trouble also gets a glyph, so it never relies on colour alone. */
export function toneIsTrouble(tone: BackgroundTone): boolean {
  return tone === 'bad' || tone === 'warning';
}

export const STATE_WORD: Record<BackgroundJobState, string> = {
  working: 'Working',
  idle: 'Idle',
  waiting: 'Waiting',
  failing: 'Failing',
  off: 'Off',
  unknown: 'No report yet'
};

/** The state dot: a filled dot in its tone, a ring for a job that is not running at all. */
export const STATE_TONE: Record<BackgroundJobState, BackgroundTone> = {
  working: 'good',
  idle: 'pending',
  waiting: 'warning',
  failing: 'bad',
  off: 'muted',
  unknown: 'muted'
};

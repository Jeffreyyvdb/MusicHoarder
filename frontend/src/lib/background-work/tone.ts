import type { BackgroundJobState, BackgroundTone } from './types.ts';

const TONE_VARIABLE: Record<BackgroundTone, string> = {
  good: '--viz-good',
  ai: '--viz-ai',
  neutral: '--viz-neutral',
  pending: '--viz-pending',
  warning: '--viz-warning',
  bad: '--viz-bad',
  muted: '--viz-muted'
};

export function toneColor(tone: BackgroundTone): string {
  return `var(${TONE_VARIABLE[tone]})`;
}

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

export function toneTextClass(tone: BackgroundTone): string {
  if (tone === 'bad') return 'text-destructive-text';
  if (tone === 'warning') return 'text-warning-text';
  return 'text-foreground';
}

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

export const STATE_TONE: Record<BackgroundJobState, BackgroundTone> = {
  working: 'good',
  idle: 'pending',
  waiting: 'warning',
  failing: 'bad',
  off: 'muted',
  unknown: 'muted'
};

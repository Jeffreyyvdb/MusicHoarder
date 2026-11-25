import { describe, expect, it } from 'vitest';
import { STATE_TONE, STATE_WORD, toneColor, toneFills, toneIsTrouble, toneTextClass } from './tone.ts';
import type { BackgroundJobState, BackgroundTone } from './types.ts';

const TONES: BackgroundTone[] = ['good', 'ai', 'neutral', 'pending', 'warning', 'bad', 'muted'];
const STATES: BackgroundJobState[] = ['off', 'idle', 'working', 'waiting', 'failing', 'unknown'];

describe('tone colours', () => {
  it('names a token for every tone', () => {
    for (const tone of TONES) expect(toneColor(tone)).toMatch(/^var\(--viz-[a-z]+\)$/);
    expect(new Set(TONES.map(toneColor)).size).toBe(TONES.length);
  });

  it('steps a repeated tone toward the card and leaves the first alone', () => {
    const fills = toneFills(['good', 'ai', 'ai', 'ai', 'bad']);
    expect(fills[0]).toBe('var(--viz-good)');
    expect(fills[1]).toBe('var(--viz-ai)');
    expect(fills[2]).toContain('color-mix');
    expect(fills[3]).toContain('color-mix');
    expect(fills[2]).not.toBe(fills[3]);
    expect(fills[4]).toBe('var(--viz-bad)');
  });

  it('writes trouble in the destructive and warning text tokens, everything else in plain ink', () => {
    expect(toneTextClass('bad')).toBe('text-destructive-text');
    expect(toneTextClass('warning')).toBe('text-warning-text');
    expect(toneTextClass('good')).toBe('text-foreground');
    expect(TONES.filter(toneIsTrouble)).toEqual(['warning', 'bad']);
  });

  it('describes every state with a word and a tone', () => {
    for (const state of STATES) {
      expect(STATE_WORD[state]).toBeTruthy();
      expect(TONES).toContain(STATE_TONE[state]);
    }
    expect(STATE_TONE.failing).toBe('bad');
  });
});

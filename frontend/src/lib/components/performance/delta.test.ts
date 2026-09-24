import { describe, expect, it } from 'vitest';
import { describeDelta, describeSeriesDelta } from './delta';

const count = (v: number) => String(Math.round(v));
const pct = (v: number) => `${Math.round(v)}%`;

describe('describeDelta', () => {
  it('calls a rise good when higher is better', () => {
    expect(describeDelta(84, 81, { format: pct, higherIsBetter: true })).toEqual({
      tone: 'better',
      direction: 'up',
      text: 'Up 3% · better'
    });
  });

  it('calls a rise bad when lower is better (more failures is a regression)', () => {
    expect(describeDelta(6, 4, { format: count, higherIsBetter: false })).toEqual({
      tone: 'worse',
      direction: 'up',
      text: 'Up 2 · worse'
    });
  });

  it('calls a drop good when lower is better', () => {
    expect(describeDelta(2, 7, { format: count, higherIsBetter: false })).toMatchObject({
      tone: 'better',
      direction: 'down',
      text: 'Down 5 · better'
    });
  });

  it('calls a drop bad when higher is better', () => {
    expect(describeDelta(70, 75, { format: pct, higherIsBetter: true })?.tone).toBe('worse');
  });

  it('treats rounding noise as no change', () => {
    expect(describeDelta(50.004, 50, { format: pct, higherIsBetter: true })).toEqual({
      tone: 'same',
      direction: 'flat',
      text: 'No change'
    });
  });

  it('calls a change no change when both figures display the same', () => {
    // 84.2% and 84.4% both show as "84%" on the card.
    expect(describeDelta(84.4, 84.2, { format: pct, higherIsBetter: true })).toEqual({
      tone: 'same',
      direction: 'flat',
      text: 'No change'
    });
  });

  it('says "slightly" when the figures differ but the change rounds to zero', () => {
    // 84.4% → 84.6% shows as 84% → 85%, while the 0.2 delta itself would print as "0%".
    expect(describeDelta(84.6, 84.4, { format: pct, higherIsBetter: true })).toEqual({
      tone: 'better',
      direction: 'up',
      text: 'Up slightly · better'
    });
  });

  it('has nothing to say without two values', () => {
    expect(describeDelta(5, null, { format: count, higherIsBetter: true })).toBeNull();
    expect(describeDelta(undefined, 5, { format: count, higherIsBetter: true })).toBeNull();
  });
});

describe('describeSeriesDelta', () => {
  const opts = { format: count, higherIsBetter: true };

  it('is a first capture only when there is a single snapshot', () => {
    expect(describeSeriesDelta([], opts).text).toBe('First capture');
    expect(describeSeriesDelta([4], opts).text).toBe('First capture');
  });

  it('does not claim a first capture when the newest value is missing', () => {
    // Avg AI score with grading unconfigured: five versions, no score on the newest.
    expect(describeSeriesDelta([80, 82, 81, 83, null], opts)).toEqual({
      tone: 'same',
      direction: 'flat',
      text: 'No data'
    });
  });

  it('says there is nothing to compare when only the previous value is missing', () => {
    expect(describeSeriesDelta([80, null, 83], opts).text).toBe('Nothing to compare');
  });

  it('describes the last two points otherwise', () => {
    expect(describeSeriesDelta([1, 4, 6], opts)).toEqual({
      tone: 'better',
      direction: 'up',
      text: 'Up 2 · better'
    });
  });
});

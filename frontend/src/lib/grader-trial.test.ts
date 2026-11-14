import { describe, expect, it } from 'vitest';
import { checkWord, ms, num, pct, ratio, recommendationText, shortModel, usd } from './grader-trial.ts';

describe('grader trial formatting', () => {
  it('formats shares as percentages', () => {
    expect(pct(0.5)).toBe('50%');
    expect(pct(0.034)).toBe('3.4%');
    expect(pct(0)).toBe('0%');
    expect(pct(null)).toBe('—');
  });

  it('formats latency in ms below a second, else seconds', () => {
    expect(ms(180.4)).toBe('180 ms');
    expect(ms(12_400)).toBe('12.4 s');
    expect(ms(null)).toBe('—');
  });

  it('keeps fractions of a cent readable', () => {
    expect(usd(0.0003)).toBe('$0.00030');
    expect(usd(0.002)).toBe('$0.0020');
    expect(usd(0.3)).toBe('$0.300');
    expect(usd(2)).toBe('$2.00');
    expect(usd(0)).toBe('$0');
  });

  it('trims trailing zeros from plain numbers', () => {
    expect(num(0.4)).toBe('0.4');
    expect(num(0.12345, 3)).toBe('0.123');
  });

  it('states how many times smaller the second value is', () => {
    expect(ratio(0.002, 0.0003)).toBe('6.7×');
    expect(ratio(12_000, 200)).toBe('60×');
    expect(ratio(null, 1)).toBeNull();
    expect(ratio(1, 0)).toBeNull();
  });

  it('shortens dated model ids', () => {
    expect(shortModel('typesafe/jev-1.14-20261014')).toBe('jev-1.14');
    expect(shortModel('deepseek/deepseek-v4-flash')).toBe('deepseek-v4-flash');
  });

  it('names every recommendation and check status', () => {
    expect(recommendationText('switch').title).toBe('Switch to this model');
    expect(checkWord('insufficient')).toBe('Too few');
  });
});

import { describe, expect, it } from 'vitest';
import {
  formatCount,
  formatDayBucket,
  formatEta,
  formatFraction,
  formatHourBucket,
  formatMoment,
  formatMonthYear,
  formatSeconds,
  formatShare,
  formatValue
} from './format.ts';

const AMS = { locale: 'en-GB', timeZone: 'Europe/Amsterdam' };
const NOW = Date.parse('2026-11-25T10:30:00Z');

describe('formatSeconds', () => {
  it.each([
    [0, '0 s'],
    [40, '40 s'],
    [59.6, '1 min'],
    [90, '1 min 30 s'],
    [600, '10 min'],
    [3600, '1 h'],
    [3599.7, '1 h'],
    [20_000, '5 h 33 min'],
    [7200, '2 h'],
    [172_800, '2 d'],
    [200_000, '2 d 8 h'],
    [-5, '0 s']
  ])('%s s reads %s', (seconds, text) => {
    expect(formatSeconds(seconds)).toBe(text);
  });
});

describe('formatFraction and formatShare', () => {
  it('keeps a decimal under 10% so a small share is not zero', () => {
    expect(formatFraction(0.042)).toBe('4.2%');
    expect(formatFraction(0.734)).toBe('73%');
    expect(formatFraction(0)).toBe('0%');
    expect(formatFraction(0.0004)).toBe('<0.1%');
  });

  it('never rounds a part that exists down to 0%', () => {
    expect(formatShare(122, 21_006)).toBe('<1%');
    expect(formatShare(13_195, 21_006)).toBe('63%');
    expect(formatShare(0, 21_006)).toBe('0%');
    expect(formatShare(5, 0)).toBe('0%');
  });
});

describe('formatValue', () => {
  it('dispatches on the wire format', () => {
    expect(formatValue('count', 13_195, 'en-GB')).toBe('13,195');
    expect(formatValue('duration', 6000)).toBe('1 h 40 min');
    expect(formatValue('percent', 0.5)).toBe('50%');
    expect(formatValue('bytes', 5 * 1024 ** 3)).toBe('5 GB');
    expect(formatCount(1234.6, 'en-GB')).toBe('1,235');
  });
});

describe('moments in the viewer zone', () => {
  it('says today, tomorrow and yesterday around now', () => {
    expect(formatMoment('2026-11-25T14:00:00Z', { ...AMS, now: NOW })).toBe('today 15:00');
    expect(formatMoment('2026-11-26T02:00:00Z', { ...AMS, now: NOW })).toBe('tomorrow 03:00');
    expect(formatMoment('2026-11-24T22:30:00Z', { ...AMS, now: NOW })).toBe('yesterday 23:30');
  });

  it('counts days by the local calendar, not by 24 hours', () => {
    // 23:30 UTC on the 25th is already the 26th in Amsterdam, so "tomorrow" from a morning "now".
    expect(formatMoment('2026-11-25T23:30:00Z', { ...AMS, now: NOW })).toBe('tomorrow 00:30');
  });

  it('gives a date past a day, with the year only when it differs', () => {
    expect(formatMoment('2026-12-28T03:00:00Z', { ...AMS, now: NOW })).toBe('28 Dec, 04:00');
    expect(formatMoment('2027-01-02T03:00:00Z', { ...AMS, now: NOW })).toBe('2 Jan 2027, 04:00');
  });

  it('returns nothing for a value it cannot read', () => {
    expect(formatMoment('not a date')).toBe('');
    expect(formatEta('nope')).toBe('');
  });

  it('labels buckets and library positions', () => {
    expect(formatDayBucket('2026-11-25T00:00:00Z', AMS)).toBe('25 Nov');
    expect(formatHourBucket('2026-11-25T13:00:00Z', AMS)).toBe('Wed 14:00');
    expect(formatMonthYear('2019-03-14T00:00:00Z', AMS)).toBe('Mar 2019');
  });
});

describe('formatEta', () => {
  const at = (ms: number) => new Date(NOW + ms).toISOString();
  it('says hours for a short wait, with the time', () => {
    expect(formatEta(at(5 * 3_600_000), { ...AMS, now: NOW })).toBe('about 5 hours, around 16:30');
    expect(formatEta(at(30 * 60_000), { ...AMS, now: NOW })).toBe('within the hour');
    expect(formatEta(at(-1000), { ...AMS, now: NOW })).toBe('within the hour');
  });

  it('says days with the date, as the section shows it', () => {
    expect(formatEta('2026-12-28T10:30:00Z', { ...AMS, now: NOW })).toBe('about 33 days, around 28 Dec');
    expect(formatEta(at(2 * 86_400_000), { ...AMS, now: NOW })).toBe('about 2 days, around 27 Nov');
  });

  it('moves to months, then gives up on a date', () => {
    expect(formatEta('2027-04-25T10:30:00Z', { ...AMS, now: NOW })).toBe('about 5 months, around Apr 2027');
    expect(formatEta('2029-06-01T00:00:00Z', { ...AMS, now: NOW })).toBe('more than 2 years away');
  });
});

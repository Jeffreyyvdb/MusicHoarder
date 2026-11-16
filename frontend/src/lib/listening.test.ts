import { describe, expect, it } from 'vitest';
import type { ListeningRecentStream } from '#lib/api-client.ts';
import {
  activityBuckets,
  clockArcs,
  dayKeyAt,
  formatHours,
  groupRecentByDay,
  parseListeningRange
} from './listening';

const stream = (songId: number, playedAtUtc: string): ListeningRecentStream => ({
  songId,
  title: `Song ${songId}`,
  playedAtUtc
});

describe('parseListeningRange', () => {
  it('keeps a known range and falls back to 4 weeks for anything else', () => {
    expect(parseListeningRange('1y')).toBe('1y');
    expect(parseListeningRange('all')).toBe('all');
    expect(parseListeningRange('forever')).toBe('4w');
    expect(parseListeningRange(null)).toBe('4w');
  });
});

describe('dayKeyAt', () => {
  it('moves an instant across midnight by the offset', () => {
    expect(dayKeyAt('2026-11-14T23:30:00Z', 0)).toBe('2026-11-14');
    expect(dayKeyAt('2026-11-14T23:30:00Z', 120)).toBe('2026-11-15');
    expect(dayKeyAt('2026-11-14T00:30:00Z', -60)).toBe('2026-11-13');
  });
});

describe('groupRecentByDay', () => {
  const now = Date.parse('2026-11-17T12:00:00Z');

  it('puts one header over each local day, newest first', () => {
    const groups = groupRecentByDay(
      [
        stream(1, '2026-11-17T09:00:00Z'),
        stream(2, '2026-11-17T01:00:00Z'),
        stream(3, '2026-11-16T22:00:00Z'),
        stream(4, '2026-11-10T22:00:00Z')
      ],
      0,
      now
    );
    expect(groups.map((g) => [g.label, g.items.length])).toEqual([
      ['Today', 2],
      ['Yesterday', 1],
      [expect.stringContaining('10'), 1]
    ]);
  });

  it('groups by the caller’s local day, not the UTC one', () => {
    const groups = groupRecentByDay(
      [stream(1, '2026-11-17T12:00:00Z'), stream(2, '2026-11-16T22:30:00Z')],
      120,
      now
    );
    expect(groups.map((g) => g.label)).toEqual(['Today']);
    expect(groups[0].key).toBe('2026-11-17');
  });
});

describe('activityBuckets', () => {
  it('gives 4 weeks a bar per day, empty days kept', () => {
    const buckets = activityBuckets(
      [{ date: '2026-11-17', streams: 3, minutes: 9 }],
      '4w',
      '2026-11-17'
    );
    expect(buckets).toHaveLength(28);
    expect(buckets.at(-1)).toMatchObject({ key: '2026-11-17', streams: 3 });
    expect(buckets[0].key).toBe('2026-10-21');
  });

  it('sums 6 months by Monday-first week', () => {
    const buckets = activityBuckets(
      [
        { date: '2026-11-16', streams: 2, minutes: 1 },
        { date: '2026-11-17', streams: 3, minutes: 1 }
      ],
      '6m',
      '2026-11-17'
    );
    expect(buckets).toHaveLength(26);
    expect(buckets.at(-1)).toMatchObject({ key: '2026-11-16', streams: 5 });
  });

  it('gives a year twelve months ending this one', () => {
    const buckets = activityBuckets(
      [{ date: '2026-03-02', streams: 4, minutes: 1 }],
      '1y',
      '2026-11-17'
    );
    expect(buckets).toHaveLength(12);
    expect(buckets.at(-1)?.key).toBe('2026-11');
    expect(buckets.find((b) => b.key === '2026-03')?.streams).toBe(4);
    expect(buckets[0].key).toBe('2025-12');
  });

  it('switches lifetime to years once it spans more than three', () => {
    const buckets = activityBuckets(
      [
        { date: '2021-05-01', streams: 1, minutes: 1 },
        { date: '2026-01-01', streams: 2, minutes: 1 }
      ],
      'all',
      '2026-11-17'
    );
    expect(buckets.map((b) => b.key)).toEqual(['2021', '2022', '2023', '2024', '2025', '2026']);
    expect(buckets[0].streams).toBe(1);
  });
});

describe('clockArcs', () => {
  it('makes 24 arcs and scales the level to the busiest hour', () => {
    const clock = Array.from({ length: 24 }, (_, h) => (h === 5 ? 10 : h === 6 ? 5 : 0));
    const arcs = clockArcs(clock, 50, 50, 20, 40);
    expect(arcs).toHaveLength(24);
    expect(arcs[5].level).toBe(1);
    expect(arcs[6].level).toBe(0.5);
    expect(arcs[0].level).toBe(0);
  });

  it('is all zero for an empty clock', () => {
    expect(clockArcs(new Array(24).fill(0), 0, 0, 1, 2).every((a) => a.level === 0)).toBe(true);
  });
});

describe('formatHours', () => {
  it('keeps a decimal under ten hours', () => {
    expect(formatHours(90)).toBe('1.5');
    expect(formatHours(60 * 25)).toBe('25');
  });
});

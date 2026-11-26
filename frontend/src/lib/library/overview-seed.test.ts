import { describe, expect, it } from 'vitest';
import { seededOrder } from '#lib/api-client.ts';
import { overviewDailySeed } from './overview-seed.ts';

describe('overviewDailySeed', () => {
  it('returns the same seed throughout the same local calendar day', () => {
    const morning = new Date(2026, 10, 25, 8, 15);
    const evening = new Date(2026, 10, 25, 23, 45);

    expect(overviewDailySeed('user-1', morning)).toBe('user-1:2026-11-25');
    expect(overviewDailySeed('user-1', evening)).toBe('user-1:2026-11-25');
  });

  it('scopes seeds per account', () => {
    const today = new Date(2026, 10, 25, 13, 0);

    expect(overviewDailySeed('admin-1', today)).not.toBe(overviewDailySeed('member-1', today));
    expect(overviewDailySeed(null, today)).toBe('2026-11-25');
  });

  it('rotates on the next day and reorders shelves', () => {
    const dayOne = overviewDailySeed('user-1', new Date(2026, 10, 25, 23, 55));
    const dayTwo = overviewDailySeed('user-1', new Date(2026, 10, 26, 0, 5));
    expect(dayTwo).toBe('user-1:2026-11-26');

    const albums = Array.from({ length: 30 }, (_, i) => `artist-${i}::album-${i}`);
    const first = seededOrder(albums, (k) => k, `${dayOne}:new-to-you`);
    const next = seededOrder(albums, (k) => k, `${dayTwo}:new-to-you`);
    expect(next).not.toEqual(first);
  });
});

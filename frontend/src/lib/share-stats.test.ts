import { describe, expect, it } from 'vitest';
import {
  countLabel,
  dayLabel,
  niceMax,
  parseDay,
  peakDay,
  shareDetailId,
  shareKindLine,
  shareListHref,
  shareListView,
  shareTitle,
  sourceLabel,
  sumDaily
} from './share-stats';

const url = (path: string) => new URL(path, 'https://musichoarder.test');

describe('share links URLs', () => {
  it('reads the list view, defaulting to the live links', () => {
    expect(shareListView(url('/shares'))).toBe('active');
    expect(shareListView(url('/shares?view=revoked'))).toBe('revoked');
    expect(shareListView(url('/shares?view=nonsense'))).toBe('active');
  });

  it('reads a pushed link id and ignores anything that is not one', () => {
    expect(shareDetailId(url('/shares?link=12'))).toBe(12);
    expect(shareDetailId(url('/shares'))).toBeNull();
    for (const bad of ['0', '-3', '1.5', 'abc', '']) {
      expect(shareDetailId(url(`/shares?link=${bad}`)), bad).toBeNull();
    }
  });

  it('round-trips a view through its list href', () => {
    for (const view of ['active', 'revoked'] as const) {
      expect(shareListView(url(shareListHref(view)))).toBe(view);
    }
  });
});

describe('share link words', () => {
  it('names an album link by its album and a song link by its song', () => {
    expect(shareTitle({ scope: 'Album', title: 'One More Time', album: 'Discovery' })).toBe(
      'Discovery'
    );
    expect(shareTitle({ scope: 'Album', title: 'One More Time', album: '  ' })).toBe(
      'One More Time'
    );
    expect(shareTitle({ scope: 'Song', title: 'One More Time', album: 'Discovery' })).toBe(
      'One More Time'
    );
  });

  it('says what kind of link it is and whose', () => {
    expect(shareKindLine({ scope: 'Song', artist: 'Daft Punk' })).toBe('Song · Daft Punk');
    expect(shareKindLine({ scope: 'Album', artist: null })).toBe('Album');
  });

  it('pluralises counts', () => {
    expect(countLabel(1, 'open', 'opens')).toBe('1 open');
    expect(countLabel(0, 'open', 'opens')).toBe('0 opens');
    expect(countLabel(1204, 'open', 'opens')).toBe(`${(1204).toLocaleString()} opens`);
  });

  it('names an unknown source', () => {
    expect(sourceLabel('TikTok')).toBe('TikTok');
    expect(sourceLabel(null)).toBe('Direct or unknown');
    expect(sourceLabel(undefined)).toBe('Direct or unknown');
  });
});

describe('opens chart', () => {
  it('rounds the axis up to 1, 2 or 5 × 10ⁿ', () => {
    expect(niceMax(0)).toBe(1);
    expect(niceMax(1)).toBe(1);
    expect(niceMax(2)).toBe(2);
    expect(niceMax(3)).toBe(5);
    expect(niceMax(6)).toBe(10);
    expect(niceMax(11)).toBe(20);
    expect(niceMax(40)).toBe(50);
    expect(niceMax(501)).toBe(1000);
  });

  it('reads a day as local midnight, not UTC', () => {
    const d = parseDay('2026-09-24');
    expect([d.getFullYear(), d.getMonth(), d.getDate(), d.getHours()]).toEqual([2026, 8, 24, 0]);
  });

  it('names today and yesterday', () => {
    const now = new Date(2026, 8, 24, 15, 0);
    expect(dayLabel('2026-09-24', now)).toBe('Today');
    expect(dayLabel('2026-09-23', now)).toBe('Yesterday');
    expect(dayLabel('2026-09-01', now)).not.toMatch(/Today|Yesterday/);
  });

  it('totals a window and finds its busiest day', () => {
    const daily = [
      { date: '2026-09-22', views: 0, visitors: 0, plays: 0 },
      { date: '2026-09-23', views: 7, visitors: 5, plays: 2 },
      { date: '2026-09-24', views: 3, visitors: 3, plays: 1 }
    ];
    expect(sumDaily(daily)).toEqual({ views: 10, plays: 3 });
    expect(peakDay(daily)?.date).toBe('2026-09-23');
    expect(peakDay(daily.slice(0, 1))).toBeNull();
  });
});

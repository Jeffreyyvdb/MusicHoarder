import { describe, expect, it } from 'vitest';
import {
  axisScale,
  binShares,
  coverage,
  formatTick,
  heightPct,
  mapTicks,
  placeLimitLabels,
  niceTicks,
  placeMarkers,
  positionOnMap,
  seriesMax,
  sparkHeights,
  stackSegments,
  stretches,
  timeTicks
} from './chart-math.ts';

const AMS = { locale: 'en-GB', timeZone: 'Europe/Amsterdam' };

describe('niceTicks', () => {
  it('rounds a top to a clean step', () => {
    expect(niceTicks(3700)).toEqual({ top: 4000, ticks: [0, 1000, 2000, 3000, 4000] });
    expect(niceTicks(7200)).toEqual({ top: 8000, ticks: [0, 2000, 4000, 6000, 8000] });
    expect(niceTicks(0.4).top).toBeCloseTo(0.4);
  });

  it('survives an empty series', () => {
    expect(niceTicks(0)).toEqual({ top: 1, ticks: [0, 1] });
    expect(niceTicks(Number.NaN)).toEqual({ top: 1, ticks: [0, 1] });
  });

  it('never leaves the max above the top', () => {
    for (const max of [1, 7, 99, 101, 6000, 7200, 12_345, 20_000, 28_800]) {
      const { top, ticks } = niceTicks(max);
      expect(top).toBeGreaterThanOrEqual(max);
      expect(ticks[0]).toBe(0);
      expect(ticks.at(-1)).toBe(top);
    }
  });
});

describe('axisScale', () => {
  it('steps a duration axis in minutes or hours, not in thousands of seconds', () => {
    const scale = axisScale('duration', 7920);
    expect(scale.ticks.map((t) => t / 3600)).toEqual([0, 1, 2, 3]);
    expect(scale.top).toBe(10_800);
    expect(axisScale('duration', 90).ticks.at(-1)).toBe(100);
  });

  it('keeps a percent axis within a fraction of one', () => {
    expect(axisScale('percent', 0.73).top).toBeCloseTo(0.8);
    expect(axisScale('percent', 3).top).toBeCloseTo(1);
  });
});

describe('formatTick', () => {
  it('writes counts in full under 10,000 and compact above', () => {
    expect(formatTick('count', 0)).toBe('0');
    expect(formatTick('count', 2000)).toBe('2,000');
    expect(formatTick('count', 20_000)).toBe('20K');
  });

  it('writes durations in the unit the tick lives in', () => {
    expect(formatTick('duration', 1800)).toBe('30 min');
    expect(formatTick('duration', 5400)).toBe('1.5 h');
    expect(formatTick('duration', 7200)).toBe('2 h');
    expect(formatTick('percent', 0.25)).toBe('25%');
  });
});

describe('series geometry', () => {
  const panel = {
    stacked: true,
    points: [
      { startUtc: '2026-11-25T00:00:00Z', values: [100, 50] },
      { startUtc: '2026-11-25T01:00:00Z', values: [300, 100] }
    ],
    limits: [{ label: 'ceiling', value: 350, tone: 'warning' as const }]
  };

  it('uses the stack total, and the limit when it is higher', () => {
    expect(seriesMax(panel)).toBe(400);
    expect(seriesMax({ ...panel, limits: [{ label: 'x', value: 900, tone: 'bad' }] })).toBe(900);
    expect(seriesMax({ ...panel, stacked: false })).toBe(350);
  });

  it('stacks from the baseline, drops zeros and caps the highest part', () => {
    const segments = stackSegments([100, 0, 300], 400);
    expect(segments.map((s) => [s.series, s.heightPct, s.top])).toEqual([
      [0, 25, false],
      [2, 75, true]
    ]);
    expect(stackSegments([0, 0], 400)).toEqual([]);
  });

  it('keeps a small real value visible', () => {
    expect(heightPct(1, 1000, 2)).toBe(2);
    expect(heightPct(0, 1000, 2)).toBe(0);
    expect(heightPct(2000, 1000)).toBe(100);
  });
});

describe('timeTicks', () => {
  const hours = Array.from({ length: 72 }, (_, i) =>
    new Date(Date.UTC(2026, 10, 23, 0, 0) + i * 3_600_000).toISOString()
  );

  it('labels each local midnight on an hourly axis', () => {
    const ticks = timeTicks(hours, 'hour', 8, AMS);
    expect(ticks.map((t) => t.index)).toEqual([23, 47, 71]);
    expect(ticks[0].label).toBe('Tue 24');
  });

  it('labels every day and thins to the room it has', () => {
    const days = Array.from({ length: 14 }, (_, i) =>
      new Date(Date.UTC(2026, 10, 12 + i)).toISOString()
    );
    expect(timeTicks(days, 'day', 20, AMS)).toHaveLength(14);
    const thin = timeTicks(days, 'day', 4, AMS);
    expect(thin.length).toBeLessThanOrEqual(4);
    expect(thin[0].index).toBe(0);
  });

  it('falls back to six-hourly marks inside one day', () => {
    const day = hours.slice(0, 20);
    const ticks = timeTicks(day, 'hour', 8, AMS);
    expect(ticks.length).toBeGreaterThan(0);
    expect(ticks.every((t) => /^\d\d:00$/.test(t.label))).toBe(true);
  });
});

describe('library map', () => {
  const bins = [
    {
      fromUtc: '2010-01-01T00:00:00Z',
      toUtc: '2012-01-01T00:00:00Z',
      done: 100,
      waiting: 0,
      excluded: 0
    },
    {
      fromUtc: '2012-01-01T00:00:00Z',
      toUtc: '2018-01-01T00:00:00Z',
      done: 20,
      waiting: 70,
      excluded: 10
    },
    {
      fromUtc: '2018-01-01T00:00:00Z',
      toUtc: '2019-01-01T00:00:00Z',
      done: 0,
      waiting: 80,
      excluded: 20
    },
    {
      fromUtc: '2019-01-01T00:00:00Z',
      toUtc: '2026-01-01T00:00:00Z',
      done: 90,
      waiting: 5,
      excluded: 5
    }
  ];

  it('splits a bin into fractions that sum to one', () => {
    expect(binShares(bins[1])).toEqual({ total: 100, done: 0.2, waiting: 0.7, excluded: 0.1 });
    expect(binShares({ ...bins[0], done: 0 }).total).toBe(0);
  });

  it('places a date inside its own bin, because bins are equal in songs not in time', () => {
    expect(positionOnMap(bins, Date.parse('2009-01-01'))).toBe(0);
    expect(positionOnMap(bins, Date.parse('2012-01-01T00:00:00Z'))).toBe(0.25);
    expect(positionOnMap(bins, Date.parse('2015-01-01T00:00:00Z'))).toBeCloseTo(0.375);
    expect(positionOnMap(bins, Date.parse('2031-01-01'))).toBe(1);
    expect(positionOnMap([], 0)).toBe(0);
  });

  it('reads the library back as stretches, merging neighbours of one kind', () => {
    const runs = stretches(bins);
    expect(runs.map((r) => [r.state, r.from, r.to])).toEqual([
      ['done', 0, 0],
      ['waiting', 1, 2],
      ['done', 3, 3]
    ]);
    expect(runs[1].songs).toBe(200);
    expect(runs[1].fromUtc).toBe('2012-01-01T00:00:00Z');
    expect(runs[1].toUtc).toBe('2019-01-01T00:00:00Z');
  });

  it('labels the axis from the bins, ending on the newest bin end', () => {
    const ticks = mapTicks(bins, 3, AMS);
    expect(ticks.map((t) => [t.position, t.label])).toEqual([
      [0, 'Jan 2010'],
      [0.5, 'Jan 2018'],
      [1, 'Jan 2026']
    ]);
    expect(mapTicks([], 3)).toEqual([]);
  });
});

describe('placeMarkers', () => {
  it('stacks labels that would collide into lanes and keeps edge labels inside', () => {
    const placed = placeMarkers([
      { position: 0.02, width: 0.3 },
      { position: 0.1, width: 0.3 },
      { position: 0.5, width: 0.2 },
      { position: 0.98, width: 0.25 }
    ]);
    expect(placed.map((p) => p.align)).toEqual(['start', 'start', 'center', 'end']);
    expect(placed[0].lane).toBe(0);
    expect(placed[1].lane).toBe(1);
    expect(placed[2].lane).toBe(0);
    expect(placed[3].lane).toBe(0);
  });

  it('answers in the order it was given', () => {
    const placed = placeMarkers([
      { position: 0.9, width: 0.1 },
      { position: 0.1, width: 0.1 }
    ]);
    expect(placed[0].position).toBe(0.9);
    expect(placed[1].position).toBe(0.1);
  });
});

describe('sparkline and coverage', () => {
  it('scales to the busiest day and keeps a quiet day visible', () => {
    expect(sparkHeights([0, 1, 100])).toEqual([0, 8, 100]);
    expect(sparkHeights([0, 0])).toEqual([0, 0]);
  });

  it('clamps a coverage fraction and has none without a whole', () => {
    expect(coverage(13_195, 21_006)).toBeCloseTo(0.628, 3);
    expect(coverage(5, 0)).toBeNull();
    expect(coverage(5, null)).toBeNull();
    expect(coverage(30, 10)).toBe(1);
  });
});

describe('placeLimitLabels', () => {
  it('puts a label above its line when there is room', () => {
    expect(placeLimitLabels([40])).toEqual([{ above: true }]);
  });

  it('puts a label under a line that is too near the top', () => {
    expect(placeLimitLabels([97])).toEqual([{ above: false }]);
  });

  it('moves the lower of two close limits under its line so the labels do not overlap', () => {
    expect(placeLimitLabels([83, 100])).toEqual([{ above: false }, { above: false }]);
    expect(placeLimitLabels([30, 60])).toEqual([{ above: true }, { above: true }]);
  });
});

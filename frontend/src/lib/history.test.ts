import { describe, expect, it } from 'vitest';
import {
  HISTORY_TINT_BADGE,
  HISTORY_TINT_EDGE,
  historyFilterSummary,
  isDefaultHistoryFilter,
  isProblem,
  type HistoryFilterState
} from './history';

const base: HistoryFilterState = {
  range: '7',
  customFrom: '',
  customTo: '',
  problemsOnly: false,
  categories: []
};

describe('historyFilterSummary', () => {
  it('names the preset range', () => {
    expect(historyFilterSummary(base)).toBe('Last 7 days');
    expect(historyFilterSummary({ ...base, range: '1' })).toBe('Today');
    expect(historyFilterSummary({ ...base, range: '30' })).toBe('Last 30 days');
  });

  it('asks for the dates of a half-filled custom range', () => {
    expect(historyFilterSummary({ ...base, range: 'custom', customFrom: '2026-08-01' })).toBe(
      'Custom range'
    );
  });

  it('shows a filled custom range as two short dates', () => {
    const text = historyFilterSummary({
      ...base,
      range: 'custom',
      customFrom: '2026-08-01',
      customTo: '2026-08-12'
    });
    expect(text).toContain('–');
    expect(text).not.toContain('Custom');
  });

  it('adds Problems and up to two category names', () => {
    expect(
      historyFilterSummary({
        ...base,
        problemsOnly: true,
        categories: new Set(['lyrics', 'video'])
      })
    ).toBe('Last 7 days · Problems · Lyrics, Videos');
  });

  it('collapses three or more categories to a count', () => {
    expect(historyFilterSummary({ ...base, categories: ['lyrics', 'video', 'sync'] })).toBe(
      'Last 7 days · 3 categories'
    );
  });
});

describe('isDefaultHistoryFilter', () => {
  it('is only the fresh-visit state', () => {
    expect(isDefaultHistoryFilter(base)).toBe(true);
    expect(isDefaultHistoryFilter({ ...base, range: '30' })).toBe(false);
    expect(isDefaultHistoryFilter({ ...base, problemsOnly: true })).toBe(false);
    expect(isDefaultHistoryFilter({ ...base, categories: ['sync'] })).toBe(false);
  });
});

describe('tints', () => {
  it('marks only warnings and errors as problems, and only they get an edge', () => {
    expect(isProblem('warn')).toBe(true);
    expect(isProblem('err')).toBe(true);
    expect(isProblem('ok')).toBe(false);
    expect(HISTORY_TINT_EDGE.ok).toBe('');
    expect(HISTORY_TINT_EDGE.err).not.toBe('');
  });

  it('uses tokens, never raw palette hues', () => {
    for (const cls of [...Object.values(HISTORY_TINT_BADGE), ...Object.values(HISTORY_TINT_EDGE)]) {
      expect(cls).not.toMatch(/(amber|red|emerald|sky|#)/);
    }
  });
});

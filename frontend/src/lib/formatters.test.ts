import { describe, expect, it } from 'vitest';
import {
  formatBytesShort,
  formatDate,
  formatDateTime,
  formatFileSize,
  formatReleaseDate,
  formatTotalDuration
} from './formatters';

describe('formatFileSize', () => {
  it('adds a TB tier above 1024 GB', () => {
    expect(formatFileSize(2 * 1024 ** 4)).toBe('2.00 TB');
    expect(formatFileSize(1023 * 1024 ** 3)).toBe('1023.00 GB');
  });

  it('keeps the smaller tiers unchanged', () => {
    expect(formatFileSize(1.5 * 1024 ** 3)).toBe('1.50 GB');
    expect(formatFileSize(1.5 * 1024 ** 2)).toBe('1.5 MB');
    expect(formatFileSize(0)).toBe('—');
  });
});

describe('formatBytesShort', () => {
  it('rounds to whole GB and one-decimal TB for chrome', () => {
    expect(formatBytesShort(303.4 * 1024 ** 3)).toBe('303 GB');
    expect(formatBytesShort(1.85 * 1024 ** 4)).toBe('1.9 TB');
    expect(formatBytesShort(512 * 1024 ** 2)).toBe('512 MB');
    expect(formatBytesShort(3 * 1024)).toBe('3 KB');
  });

  it('reads nothing as 0 B', () => {
    expect(formatBytesShort(0)).toBe('0 B');
    expect(formatBytesShort(null)).toBe('0 B');
    expect(formatBytesShort(undefined)).toBe('0 B');
  });
});

describe('formatTotalDuration', () => {
  it('rounds to minutes once there is a minute to show', () => {
    expect(formatTotalDuration(1256)).toBe('21 min');
    expect(formatTotalDuration(15660)).toBe('4 h 21 min');
    expect(formatTotalDuration(7200)).toBe('2 h');
    expect(formatTotalDuration(48)).toBe('48 sec');
    expect(formatTotalDuration(0)).toBe('—');
  });
});

describe('formatDate / formatDateTime', () => {
  it('writes the month as a word and never the seconds', () => {
    const iso = '2026-09-30T06:51:28Z';
    expect(formatDate(iso)).toMatch(/Sep/);
    expect(formatDate(iso)).toMatch(/2026/);
    expect(formatDateTime(iso)).not.toMatch(/:28/);
    expect(formatDate(null)).toBe('—');
    expect(formatDateTime('not a date')).toBe('—');
  });
});

describe('formatReleaseDate', () => {
  it('reads a tag date as a calendar date', () => {
    expect(formatReleaseDate('2021-01-10')).toMatch(/Jan/);
    expect(formatReleaseDate('2021-01-10')).toMatch(/10/);
    expect(formatReleaseDate('2021-01')).toMatch(/Jan/);
    expect(formatReleaseDate('2021')).toBe('2021');
    expect(formatReleaseDate(2021)).toBe('2021');
    expect(formatReleaseDate(null)).toBe('—');
  });
});

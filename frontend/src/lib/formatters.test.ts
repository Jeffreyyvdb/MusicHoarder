import { describe, expect, it } from 'vitest';
import { formatBytesShort, formatFileSize } from './formatters';

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

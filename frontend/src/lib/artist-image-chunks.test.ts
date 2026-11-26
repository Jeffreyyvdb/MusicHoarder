import { describe, expect, it } from 'vitest';
import { chunkArtistNames } from './api-client.ts';

const encodedLength = (chunk: string[]) =>
  chunk.map((n) => encodeURIComponent(n)).join('%0A').length;

describe('chunkArtistNames', () => {
  it('splits short names by count', () => {
    const names = Array.from({ length: 250 }, (_, i) => `Artist ${i}`);
    expect(chunkArtistNames(names).map((c) => c.length)).toEqual([120, 120, 10]);
  });

  it('splits long non-Latin names by encoded length, keeping every request line under 8 KB', () => {
    // 20 CJK characters encode to 180 bytes each: 120 of them would be a 22 KB query.
    const names = Array.from(
      { length: 120 },
      (_, i) => `${'坂本龍一交響楽団'.repeat(2)}${'名'.repeat(4)}${i}`
    );
    const chunks = chunkArtistNames(names);
    expect(chunks.flat()).toEqual(names);
    for (const chunk of chunks) expect(encodedLength(chunk)).toBeLessThanOrEqual(6000);
  });

  it('drops duplicates', () => {
    expect(chunkArtistNames(['A', 'B', 'A'])).toEqual([['A', 'B']]);
  });
});

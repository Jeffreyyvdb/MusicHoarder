import { describe, expect, it } from 'vitest';
import { isConfirmedInstrumental } from './instrumental.ts';

describe('isConfirmedInstrumental', () => {
  it('holds for a song LRCLIB calls instrumental', () => {
    expect(isConfirmedInstrumental({ isInstrumental: true, lyricsStatus: 'Instrumental' })).toBe(
      true
    );
  });

  it('does not hold for a song only the AI heard no singing in', () => {
    expect(isConfirmedInstrumental({ isInstrumental: true, lyricsStatus: 'NotFound' })).toBe(false);
  });

  it('does not hold for a song with lyrics', () => {
    expect(isConfirmedInstrumental({ isInstrumental: false, lyricsStatus: 'Fetched' })).toBe(false);
  });
});

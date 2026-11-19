import { describe, expect, it } from 'vitest';
import { lyricsAvailability, videoAvailability } from './media-availability.ts';

const noLyrics = {
  hasLyrics: false,
  busy: false,
  instrumental: false,
  canRecheck: true,
  canEnhance: true
};

describe('lyricsAvailability', () => {
  it('opens the mode when the song has lyrics, whatever the account can do', () => {
    expect(
      lyricsAvailability({ ...noLyrics, hasLyrics: true, canRecheck: false, canEnhance: false })
    ).toBe('ready');
  });

  it('opens the mode while an AI run is in progress so its progress shows', () => {
    expect(lyricsAvailability({ ...noLyrics, busy: true })).toBe('ready');
  });

  it('offers to fetch or generate when there are none and the account can do either', () => {
    expect(lyricsAvailability(noLyrics)).toBe('offer');
    expect(lyricsAvailability({ ...noLyrics, canEnhance: false })).toBe('offer');
    expect(lyricsAvailability({ ...noLyrics, canRecheck: false })).toBe('offer');
  });

  it('is disabled when the account can do neither', () => {
    expect(lyricsAvailability({ ...noLyrics, canRecheck: false, canEnhance: false })).toBe('none');
  });

  it('is disabled for an instrumental, even for an account that could fetch', () => {
    expect(lyricsAvailability({ ...noLyrics, instrumental: true })).toBe('none');
  });
});

describe('videoAvailability', () => {
  it('opens the mode when a clip is playable', () => {
    expect(videoAvailability({ playable: true, settled: true, canManage: false })).toBe('ready');
  });

  it('offers to find one once the server has answered and the account can manage videos', () => {
    expect(videoAvailability({ playable: false, settled: true, canManage: true })).toBe('offer');
  });

  it('is disabled until the server has answered for this song', () => {
    expect(videoAvailability({ playable: false, settled: false, canManage: true })).toBe('none');
  });

  it('is disabled for an account that cannot manage videos', () => {
    expect(videoAvailability({ playable: false, settled: true, canManage: false })).toBe('none');
  });
});

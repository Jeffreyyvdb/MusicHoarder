import { describe, expect, it } from 'vitest';
import type { SongReport } from '#lib/api-client.ts';
import {
  durationMismatch,
  reportedTitle,
  reportsByProvider,
  requestedLine,
  sourceLink
} from '#lib/song-reports.ts';

function report(over: Partial<SongReport> = {}): SongReport {
  return {
    id: 1,
    songId: 10,
    reason: 'WrongSong',
    note: null,
    title: 'Got',
    artist: 'Someone',
    album: null,
    durationMs: 180_000,
    fileName: 'got.opus',
    matchedBy: null,
    matchConfidence: null,
    wishlistItemId: 5,
    origin: 'UserRequested',
    requestedTitle: 'Wanted',
    requestedArtist: 'Someone',
    requestedAlbum: null,
    requestedDurationMs: 181_000,
    spotifyTrackId: null,
    deezerTrackId: null,
    isrc: null,
    downloadProvider: 'yt-dlp',
    downloadedSourceId: 'dQw4w9WgXcQ',
    sourceUrl: null,
    removedAtUtc: '2026-11-14T10:00:00Z',
    createdAtUtc: '2026-11-14T10:00:00Z',
    ...over
  };
}

describe('song reports', () => {
  it('names what arrived, falling back to the file name', () => {
    expect(reportedTitle(report())).toBe('Got — Someone');
    expect(reportedTitle(report({ title: null, artist: null }))).toBe('got.opus');
  });

  it('shows what was asked for only when it reads differently', () => {
    expect(requestedLine(report())).toBe('Wanted — Someone');
    expect(requestedLine(report({ requestedTitle: 'got' }))).toBeNull();
    expect(requestedLine(report({ requestedTitle: null, requestedArtist: null }))).toBeNull();
  });

  it('flags lengths more than five seconds apart', () => {
    expect(durationMismatch(report())).toBeNull();
    expect(durationMismatch(report({ durationMs: 300_000 }))).toBe('5:00, expected 3:01');
    expect(durationMismatch(report({ requestedDurationMs: null }))).toBeNull();
  });

  it('links the YouTube video yt-dlp used, or the URL that was asked for', () => {
    expect(sourceLink(report())).toBe('https://www.youtube.com/watch?v=dQw4w9WgXcQ');
    expect(sourceLink(report({ sourceUrl: 'https://youtu.be/abc' }))).toBe('https://youtu.be/abc');
    expect(
      sourceLink(report({ downloadProvider: 'slskd', downloadedSourceId: 'user/file' }))
    ).toBeNull();
    expect(
      sourceLink(report({ sourceUrl: 'javascript:alert(1)', downloadedSourceId: null }))
    ).toBeNull();
  });

  it('counts reports per provider, most first', () => {
    expect(
      reportsByProvider([
        report(),
        report({ downloadProvider: 'spotiflac' }),
        report(),
        report({ downloadProvider: null })
      ])
    ).toEqual([
      { provider: 'yt-dlp', count: 2 },
      { provider: 'spotiflac', count: 1 },
      { provider: 'Unknown', count: 1 }
    ]);
  });
});

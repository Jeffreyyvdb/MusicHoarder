import { describe, expect, it } from 'vitest';
import {
  externalReferrer,
  shareOpenEventData,
  sharePlayEventData,
  type SharePayload,
  type ShareTrack
} from './share-client';

const track = (id: number, title: string, artist: string | null = 'Aurora Vale'): ShareTrack => ({
  id,
  title,
  artist,
  hasCoverArt: false,
  hasSyncedLyrics: false,
  hasPlainLyrics: false,
  isInstrumental: false,
  hasVideo: false
});

const songShare: SharePayload = {
  scope: 'Song',
  sharedSongId: 7,
  album: { title: 'Night Drive', artist: 'Aurora Vale' },
  tracks: [track(7, 'Night Drive (unreleased)')]
};

const albumShare: SharePayload = {
  scope: 'Album',
  sharedSongId: 2,
  album: { title: 'Late Signals', artist: 'Aurora Vale', year: 2026 },
  tracks: [track(1, 'Glasshouse'), track(2, 'Static Bloom', 'Aurora Vale & Friend')]
};

describe('externalReferrer', () => {
  const origin = 'https://music.example';

  it('keeps another site or app', () => {
    expect(externalReferrer('https://www.tiktok.com/', origin)).toBe('https://www.tiktok.com/');
    expect(externalReferrer('android-app://com.whatsapp/', origin)).toBe(
      'android-app://com.whatsapp/'
    );
  });

  it('drops this origin, nothing, and junk', () => {
    expect(externalReferrer('https://music.example/share/abc', origin)).toBeNull();
    expect(externalReferrer('', origin)).toBeNull();
    expect(externalReferrer('not a url', origin)).toBeNull();
  });
});

describe('Umami share events', () => {
  it('names a song link by its song', () => {
    expect(shareOpenEventData(songShare)).toEqual({
      scope: 'song',
      title: 'Night Drive (unreleased)',
      artist: 'Aurora Vale'
    });
  });

  it('names an album link by its album', () => {
    expect(shareOpenEventData(albumShare)).toEqual({
      scope: 'album',
      title: 'Late Signals',
      artist: 'Aurora Vale'
    });
  });

  it('names the played track, and its album on an album link', () => {
    expect(sharePlayEventData(albumShare, albumShare.tracks[1])).toEqual({
      scope: 'album',
      title: 'Static Bloom',
      artist: 'Aurora Vale & Friend',
      album: 'Late Signals'
    });
    expect(sharePlayEventData(songShare, songShare.tracks[0])).toEqual({
      scope: 'song',
      title: 'Night Drive (unreleased)',
      artist: 'Aurora Vale'
    });
  });

  it('leaves out blanks and clips long text to what Umami accepts', () => {
    const long = 'x'.repeat(800);
    const data = sharePlayEventData(
      { ...songShare, album: { title: null, artist: null } },
      track(7, long, '  ')
    );
    expect(data).toEqual({ scope: 'song', title: 'x'.repeat(500) });
  });
});

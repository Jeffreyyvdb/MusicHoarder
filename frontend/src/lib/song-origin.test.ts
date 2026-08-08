import { describe, expect, it } from 'vitest';
import {
  isMyMusic,
  isSpotifySourced,
  songOriginLabel,
  spotifyAddedTime,
  type ApiSong
} from './api-client';

function song(over: Partial<ApiSong>): ApiSong {
  return { id: 1, fileName: 'track.flac', ...over } as ApiSong;
}

describe('isSpotifySourced', () => {
  it('covers liked songs and playlists, whether or not the file was downloaded', () => {
    expect(isSpotifySourced(song({ originSource: 'SpotifyLiked', originKind: 'Downloaded' }))).toBe(true);
    expect(isSpotifySourced(song({ originSource: 'SpotifyPlaylist', originKind: 'Scanned' }))).toBe(true);
  });

  it('excludes every other origin', () => {
    expect(isSpotifySourced(song({ originSource: 'DeezerPlaylist' }))).toBe(false);
    expect(isSpotifySourced(song({ originSource: 'DirectUrl' }))).toBe(false);
    expect(isSpotifySourced(song({ originSource: 'None', originKind: 'Scanned' }))).toBe(false);
    expect(isSpotifySourced(song({}))).toBe(false);
  });
});

describe('spotifyAddedTime', () => {
  it('reads Spotify\'s save date, not the local like', () => {
    const s = song({ spotifyAddedAtUtc: '2024-03-04T00:00:00Z', likedAtUtc: '2026-01-01T00:00:00Z' });
    expect(spotifyAddedTime(s)).toBe(Date.parse('2024-03-04T00:00:00Z'));
  });

  it('is 0 when unknown, so those rows sort last under a descending sort', () => {
    expect(spotifyAddedTime(song({}))).toBe(0);
    expect(spotifyAddedTime(song({ spotifyAddedAtUtc: 'not-a-date' }))).toBe(0);
  });
});

describe('songOriginLabel', () => {
  it('names the collection in the tooltip and stays terse in the pill', () => {
    const s = song({ originSource: 'SpotifyPlaylist', originDetail: 'Late Night', originKind: 'Downloaded' });
    const label = songOriginLabel(s);
    expect(label?.label).toBe('Spotify');
    expect(label?.title).toContain('Late Night');
    expect(label?.title).toContain('downloaded by MusicHoarder');
  });

  it('distinguishes an already-owned Spotify track from a downloaded one', () => {
    const owned = songOriginLabel(song({ originSource: 'SpotifyLiked', originKind: 'Scanned' }));
    expect(owned?.title).toContain('already in your library');
  });

  it('falls back to how the file arrived when no collection asked for it', () => {
    expect(songOriginLabel(song({ originKind: 'Scanned', originSource: 'None' }))?.label).toBe('Library');
    expect(songOriginLabel(song({ originKind: 'Downloaded', originSource: 'None' }))?.label).toBe('Download');
    expect(songOriginLabel(song({ originKind: 'Synced', originSource: 'None' }))?.label).toBe('Synced');
  });

  it('returns null for a row the API never annotated', () => {
    expect(songOriginLabel(song({}))).toBeNull();
  });

  it('names the album an album-fill track was completing', () => {
    const label = songOriginLabel(
      song({ originSource: 'AlbumCompletion', originDetail: 'Discovery', originKind: 'Downloaded' })
    );
    expect(label?.label).toBe('Album fill');
    expect(label?.title).toContain('Discovery');
  });
});

describe('isMyMusic', () => {
  it('claims everything that is not album fill', () => {
    expect(isMyMusic(song({ acquisitionIntent: 'Explicit' }))).toBe(true);
    expect(isMyMusic(song({ acquisitionIntent: 'Explicit', originKind: 'Scanned' }))).toBe(true);
  });

  it('excludes album fill you have not liked', () => {
    expect(isMyMusic(song({ acquisitionIntent: 'AlbumFill' }))).toBe(false);
  });

  // The escape hatch, and the whole payoff of the feature: discover it, heart it, it's yours.
  it('promotes album fill the moment you like it', () => {
    expect(isMyMusic(song({ acquisitionIntent: 'AlbumFill', likedAtUtc: '2026-01-01T00:00:00Z' }))).toBe(true);
  });

  // An API too old to send the field, or any row that never went through the wishlist, must read as
  // yours rather than quietly disappearing from the view.
  it('defaults to yours when the field is absent', () => {
    expect(isMyMusic(song({}))).toBe(true);
    expect(isMyMusic(song({ acquisitionIntent: null }))).toBe(true);
  });
});

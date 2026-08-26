import { describe, expect, it } from 'vitest';
import {
  hasMusicVideo,
  isAddedByLink,
  isLocalFile,
  isMyMusic,
  isSpotifyLiked,
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

  // For a track that is both saved and in a collected playlist, the save is the meaningful moment —
  // otherwise the Spotify Liked list orders by when a playlist happened to pick the track up.
  it('prefers the Liked Songs date over a playlist add', () => {
    const s = song({
      spotifyAddedAtUtc: '2020-01-01T00:00:00Z',
      spotifyLikedAtUtc: '2025-06-06T00:00:00Z'
    });
    expect(spotifyAddedTime(s)).toBe(Date.parse('2025-06-06T00:00:00Z'));
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

describe('isSpotifyLiked', () => {
  it('reads the Liked Songs date, not the wishlist origin', () => {
    // The point of the separate field: a track already in the library when you liked it has no
    // download behind it, so its originSource says nothing — but the match sweep still dated it.
    expect(
      isSpotifyLiked(song({ originKind: 'Scanned', spotifyLikedAtUtc: '2025-02-02T00:00:00Z' }))
    ).toBe(true);
  });

  // The regression this field exists to prevent. A collected playlist stamps spotifyAddedAtUtc with
  // the date the track entered *that playlist*, which is not a save — filtering on it would drag in
  // every playlist track you never liked.
  it('does not claim a playlist track you never saved', () => {
    expect(
      isSpotifyLiked(
        song({ originSource: 'SpotifyPlaylist', spotifyAddedAtUtc: '2025-02-02T00:00:00Z' })
      )
    ).toBe(false);
  });

  it('is false for a row with no Spotify dates at all', () => {
    expect(isSpotifyLiked(song({}))).toBe(false);
  });
});

describe('isLocalFile', () => {
  it('claims only what a scan found already on the source share', () => {
    expect(isLocalFile(song({ originKind: 'Scanned' }))).toBe(true);
    expect(isLocalFile(song({ originKind: 'Downloaded' }))).toBe(false);
    expect(isLocalFile(song({ originKind: 'Synced' }))).toBe(false);
  });

  // Origin is derived server-side from the file's root, so an API too old to send it must not make
  // every row read as local.
  it('is false when the API sent no origin', () => {
    expect(isLocalFile(song({}))).toBe(false);
  });
});

describe('isAddedByLink', () => {
  it('claims URL imports and nothing else', () => {
    expect(isAddedByLink(song({ originSource: 'DirectUrl' }))).toBe(true);
    expect(isAddedByLink(song({ originSource: 'SpotifyLiked' }))).toBe(false);
    expect(isAddedByLink(song({ originSource: 'AlbumCompletion' }))).toBe(false);
    expect(isAddedByLink(song({}))).toBe(false);
  });
});

describe('hasMusicVideo', () => {
  it('is a plain boolean read that treats a missing field as no video', () => {
    expect(hasMusicVideo(song({ hasMusicVideo: true }))).toBe(true);
    expect(hasMusicVideo(song({ hasMusicVideo: false }))).toBe(false);
    expect(hasMusicVideo(song({}))).toBe(false);
  });
});

describe('isMyMusic', () => {
  it('claims everything you asked for, and nothing album completion added', () => {
    expect(isMyMusic(song({ acquisitionIntent: 'Explicit' }))).toBe(true);
    expect(isMyMusic(song({ acquisitionIntent: 'AlbumFill' }))).toBe(false);
  });

  // The promotion rule, and the whole payoff of liking a filled track: it joins your music.
  it('promotes an album-fill track once you like it', () => {
    expect(
      isMyMusic(song({ acquisitionIntent: 'AlbumFill', likedAtUtc: '2026-08-26T00:00:00Z' }))
    ).toBe(true);
  });

  // Playing is not choosing — shuffling a filled album through once must not adopt every track.
  it('is not promoted by plays alone', () => {
    expect(isMyMusic(song({ acquisitionIntent: 'AlbumFill', playCount: 12 }))).toBe(false);
  });

  // The stored column is the authority, so a row that predates it — or a shared row, whose DTO
  // deliberately omits it — has to degrade to "shown", never to an empty list.
  it('treats a row with no intent as yours', () => {
    expect(isMyMusic(song({}))).toBe(true);
  });

  // The server decides the fill half now. The enum name is only the fallback, for a server older
  // than the boolean — so when both are present the boolean wins.
  it('prefers the server flag over the enum name', () => {
    expect(isMyMusic(song({ isAlbumFill: true, acquisitionIntent: 'Explicit' }))).toBe(false);
    expect(isMyMusic(song({ isAlbumFill: false, acquisitionIntent: 'AlbumFill' }))).toBe(true);
  });

  it('falls back to the enum name when the server sent no flag', () => {
    expect(isMyMusic(song({ acquisitionIntent: 'AlbumFill' }))).toBe(false);
  });
});

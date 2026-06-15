import { describe, expect, it } from 'vitest';
import { buildAlbumsFromSongs, type ApiSong } from './api-client';

// Minimal ApiSong factory — only the fields buildAlbumsFromSongs reads.
function song(over: Partial<ApiSong>): ApiSong {
  return {
    id: 0,
    fileName: 'track.flac',
    artist: 'Kanye West',
    albumArtist: 'Kanye West',
    album: 'My Beautiful Dark Twisted Fantasy',
    ...over
  } as ApiSong;
}

describe('buildAlbumsFromSongs', () => {
  it('splits one album name across destination folders (mirrors the player)', () => {
    // Same artist + album name, but two different release folders (Navidrome shows two albums).
    const songs = [
      song({ id: 1, trackNumber: 1, destinationPath: '/dest/Kanye West/2010 - MBDTF/01 - Dark Fantasy.flac' }),
      song({ id: 2, trackNumber: 2, destinationPath: '/dest/Kanye West/2010 - MBDTF/02 - Gorgeous.flac' }),
      song({ id: 3, trackNumber: 1, destinationPath: "/dest/Kanye West/2013 - MBDTF/01 - Mama's Boy.flac" })
    ];

    const albums = buildAlbumsFromSongs(songs);

    expect(albums).toHaveLength(2);
    const counts = albums.map((a) => a.trackCount).sort();
    expect(counts).toEqual([1, 2]);
    // Keys are the destination folder directories, not the (shared) album name.
    expect(new Set(albums.map((a) => a.key)).size).toBe(2);
  });

  it('keeps a multi-disc album (same folder) as one card', () => {
    const songs = [
      song({ id: 1, trackNumber: 1, destinationPath: '/dest/A/2000 - X/1-01 - a.flac' }),
      song({ id: 2, trackNumber: 1, destinationPath: '/dest/A/2000 - X/2-01 - b.flac' })
    ];

    const albums = buildAlbumsFromSongs(songs);

    expect(albums).toHaveLength(1);
    expect(albums[0].trackCount).toBe(2);
  });

  it('falls back to artist::album name grouping when songs are not built', () => {
    const songs = [
      song({ id: 1, destinationPath: null }),
      song({ id: 2, destinationPath: undefined })
    ];

    const albums = buildAlbumsFromSongs(songs);

    expect(albums).toHaveLength(1);
    expect(albums[0].key).toBe('kanye west::my beautiful dark twisted fantasy');
  });
});

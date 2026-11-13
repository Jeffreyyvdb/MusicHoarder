import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  coverUrlForSong,
  electFolderCovers,
  fetchSongs,
  hydrateAlbums,
  resetFolderCovers,
  rowCoverFallbackForSong,
  rowCoverUrlForSong,
  toPlayerSong,
  type AlbumSummaryDto,
  type ApiSong
} from './api-client';

/*
 * Track rows share one cover per album folder.
 *
 * The server answers a built track's cover from its destination folder first (CoverArtResolver),
 * so the rows of one such folder are one picture under as many URLs as it has tracks. These pin
 * when a row may borrow another track's URL — and, more to the point, every case where it must
 * keep its own, because a wrong picture on a row is worse than a slow one.
 */

const ROOT = '/music/library';

/** A built, released track with art, as the library builder leaves one. */
function built(id: number, folder: string, over: Partial<ApiSong> = {}): ApiSong {
  return {
    id,
    sourcePath: `/music/source/${id}.flac`,
    destinationPath: `${ROOT}/${folder}/${String(id).padStart(2, '0')} - Track.flac`,
    fileName: `${id}.flac`,
    fileSizeBytes: 1,
    isBuilt: true,
    hasCoverArt: true,
    releaseClassification: 'Released',
    ...over
  } as ApiSong;
}

const pairs = (map: Map<number, number>) => [...map.entries()];

describe('electFolderCovers', () => {
  it('lets the tracks of one album folder show the first track’s cover', () => {
    const map = electFolderCovers([
      built(11, 'Kanye West/2010 - MBDTF'),
      built(12, 'Kanye West/2010 - MBDTF'),
      built(13, 'Kanye West/2010 - MBDTF')
    ]);
    // The stand-in itself is absent: it asks for its own cover.
    expect(pairs(map)).toEqual([
      [12, 11],
      [13, 11]
    ]);
  });

  it('never shares across folders, and a folder of one shares nothing', () => {
    const map = electFolderCovers([
      built(1, 'Kanye West/2010 - MBDTF'),
      built(2, 'Kanye West/2010 - MBDTF (Deluxe)'),
      built(3, 'Kanye West/2013 - Yeezus'),
      built(4, 'Kanye West/2013 - Yeezus')
    ]);
    expect(pairs(map)).toEqual([[4, 3]]);
  });

  it('follows list order, so the stand-in is the same on every load', () => {
    const map = electFolderCovers([
      built(30, 'A/Album'),
      built(10, 'A/Album'),
      built(20, 'A/Album')
    ]);
    expect(map.get(10)).toBe(30);
    expect(map.get(20)).toBe(30);
  });

  // The cover endpoint falls back to a music video's thumbnail for a track with no art of its own;
  // as the stand-in, that could put a video frame on every row of the album.
  it('never lets a track with a music video stand in for its album', () => {
    const map = electFolderCovers([
      built(1, 'A/Album', { hasMusicVideo: true }),
      built(2, 'A/Album'),
      built(3, 'A/Album')
    ]);
    expect(pairs(map)).toEqual([
      [1, 2],
      [3, 2]
    ]);

    const onlyVideos = electFolderCovers([
      built(4, 'B/Single', { hasMusicVideo: true }),
      built(5, 'B/Single', { hasMusicVideo: true })
    ]);
    expect(onlyVideos.size).toBe(0);
  });

  // Writing a folder cover flags every track in the folder, so an unflagged one says there is no
  // folder image — and then each track answers with its own embedded picture.
  it('shares nothing in a folder where a built track has no art flag', () => {
    const map = electFolderCovers([
      built(1, 'A/Album'),
      built(2, 'A/Album', { hasCoverArt: false }),
      built(3, 'A/Album')
    ]);
    expect(map.size).toBe(0);
  });

  // The builder files an artist's unreleased tracks in one folder and writes no cover there:
  // unrelated recordings, each with its own embedded picture.
  it('shares nothing in a folder of unreleased material', () => {
    const map = electFolderCovers([
      built(1, 'Kanye West/Unreleased', { releaseClassification: 'Unreleased' }),
      built(2, 'Kanye West/Unreleased', { releaseClassification: 'Unreleased' }),
      built(3, 'Kanye West/Unreleased', { releaseClassification: 'Unreleased' })
    ]);
    expect(map.size).toBe(0);
  });

  it('still shares in an album folder that holds one unreleased bonus track', () => {
    const map = electFolderCovers([
      built(1, 'A/Album'),
      built(2, 'A/Album', { releaseClassification: 'Unreleased' }),
      built(3, 'A/Album', { releaseClassification: 'LikelyUnreleased' })
    ]);
    expect(pairs(map)).toEqual([
      [2, 1],
      [3, 1]
    ]);
  });

  it('leaves out rows the builder did not put in a folder, without letting them block it', () => {
    const map = electFolderCovers([
      // Shared with you: no destination path is published, and a path would not make it yours.
      built(1, 'A/Album', { sharedByUserId: 'grantor', destinationPath: null }),
      built(2, 'A/Album', { sharedByUserId: 'grantor' }),
      // Not built, by flag or because the server predates the flag.
      built(3, 'A/Album', { isBuilt: false, hasCoverArt: false }),
      built(4, 'A/Album', { isBuilt: undefined }),
      // Soft-deleted: its cover would answer 404 for everyone borrowing it.
      built(5, 'A/Album', { deletedAtUtc: '2026-01-01T00:00:00Z' }),
      built(6, 'A/Album'),
      built(7, 'A/Album')
    ]);
    expect(pairs(map)).toEqual([[7, 6]]);
  });

  // The demo library plays straight off its source files: "built", but no builder wrote a cover
  // into those folders.
  it('shares nothing between rows built in place', () => {
    const inPlace = (id: number): ApiSong => {
      const path = `/demo/Artist/Album/${id}.flac`;
      return built(id, 'ignored', { sourcePath: path, destinationPath: path });
    };
    expect(electFolderCovers([inPlace(1), inPlace(2)]).size).toBe(0);
  });

  it('splits a Windows path on either separator, and a POSIX one only on "/"', () => {
    const windows = electFolderCovers([
      built(1, '', { destinationPath: 'D:\\Music\\A\\Album\\01 - One.flac' }),
      built(2, '', { destinationPath: 'D:\\Music\\A\\Album\\02 - Two.flac' }),
      built(3, '', { destinationPath: 'D:\\Music\\A\\Other\\01 - One.flac' })
    ]);
    expect(pairs(windows)).toEqual([[2, 1]]);

    const posix = electFolderCovers([
      built(1, '', { destinationPath: '/music/A/Album/AC\\DC one.flac' }),
      built(2, '', { destinationPath: '/music/A/Album/AC\\DC two.flac' }),
      built(3, '', { destinationPath: '/music/A/Album/Plain.flac' })
    ]);
    expect(pairs(posix)).toEqual([
      [2, 1],
      [3, 1]
    ]);
  });
});

describe('track row covers after a library load', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    resetFolderCovers();
  });

  async function load(songs: ApiSong[]): Promise<ApiSong[]> {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => new Response(JSON.stringify({ count: songs.length, songs, grantors: [] })))
    );
    return fetchSongs();
  }

  const album = () => [built(11, 'A/Album'), built(12, 'A/Album'), built(13, 'A/Album')];

  it('gives every row of an album folder one URL, with its own as the second chance', async () => {
    const [first, second, third] = await load(album());

    expect(rowCoverUrlForSong(first)).toBe('/api/mh/songs/11/cover');
    expect(rowCoverUrlForSong(second)).toBe('/api/mh/songs/11/cover');
    expect(rowCoverUrlForSong(third)).toBe('/api/mh/songs/11/cover');

    // If the borrowed cover fails to load, the row falls back to exactly what it showed before.
    expect(rowCoverFallbackForSong(second)).toBe('/api/mh/songs/12/cover');
    expect(rowCoverFallbackForSong(third)).toBe('/api/mh/songs/13/cover');
    // Nothing borrowed, nothing to fall back to: an own-URL row fails as it always did.
    expect(rowCoverFallbackForSong(first)).toBeNull();
  });

  it('keeps a row on its own cover where the folder’s picture is not known to be its own', async () => {
    const [unbuilt, granted, leakA, leakB] = await load([
      built(1, 'A/Album', { isBuilt: false, destinationPath: null }),
      built(2, 'A/Album', { sharedByUserId: 'grantor', destinationPath: null }),
      built(3, 'A/Unreleased', { releaseClassification: 'Unreleased' }),
      built(4, 'A/Unreleased', { releaseClassification: 'Unreleased' })
    ]);
    for (const song of [unbuilt, granted, leakA, leakB]) {
      expect(rowCoverUrlForSong(song)).toBe(`/api/mh/songs/${song.id}/cover`);
      expect(rowCoverFallbackForSong(song)).toBeNull();
    }
  });

  it('still shows no cover, or an explicit one, exactly as coverUrlForSong does', async () => {
    const [, second] = await load(album());

    const artless = { ...second, hasCoverArt: false };
    expect(rowCoverUrlForSong(artless)).toBeNull();
    expect(rowCoverFallbackForSong(artless)).toBeNull();

    const external = { ...second, albumArt: 'https://i.scdn.co/image/abc' };
    expect(rowCoverUrlForSong(external)).toBe('https://i.scdn.co/image/abc');
    expect(rowCoverFallbackForSong(external)).toBeNull();
  });

  // A heart tap or a play swaps one row for a copy of itself; the election is by id and is only
  // redone when the whole list is replaced.
  it('holds for a row swapped in by a single-row change', async () => {
    const [, second] = await load(album());
    const liked = { ...second, likedAtUtc: '2026-02-02T00:00:00Z' };
    expect(rowCoverUrlForSong(liked)).toBe('/api/mh/songs/11/cover');
  });

  it('is redone from each list that replaces the last one, and cleared on sign-out', async () => {
    const [, second] = await load(album());
    expect(rowCoverUrlForSong(second)).toBe('/api/mh/songs/11/cover');

    // Track 11 was deleted before this load: 12 now stands in for the folder.
    const [nowFirst, nowSecond] = await load([built(12, 'A/Album'), built(13, 'A/Album')]);
    expect(rowCoverUrlForSong(nowFirst)).toBe('/api/mh/songs/12/cover');
    expect(rowCoverUrlForSong(nowSecond)).toBe('/api/mh/songs/12/cover');

    resetFolderCovers();
    expect(rowCoverUrlForSong(nowSecond)).toBe('/api/mh/songs/13/cover');
    expect(rowCoverFallbackForSong(nowSecond)).toBeNull();
  });

  // Everything that shows ONE cover — the player and mini player, Now Playing, the lock-screen
  // artwork, an album card — keeps naming the song it is about.
  it('leaves the player’s cover and the album cards on their own song', async () => {
    const songs = await load(album());
    const second = songs[1];

    expect(coverUrlForSong(second)).toBe('/api/mh/songs/12/cover');
    expect(toPlayerSong(second, 'A').coverUrl).toBe('/api/mh/songs/12/cover');

    const card = { key: 'k', coverSongId: 13, trackIds: [11, 12, 13] } as AlbumSummaryDto;
    const [hydrated] = hydrateAlbums([card], new Map(songs.map((s) => [s.id, s])));
    expect(hydrated.coverUrl).toBe('/api/mh/songs/13/cover');
  });
});

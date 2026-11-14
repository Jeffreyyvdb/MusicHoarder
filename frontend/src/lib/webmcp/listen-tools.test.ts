import { describe, expect, it, vi } from 'vitest';
import type { AlbumSummary, ApiSong, GroupSummary, LibraryPlaylist } from '#lib/api-client.ts';
import type { PlayerSong } from '#lib/stores/player.svelte.ts';
import { createListenTools, type ListenDeps, type PlayerPort } from './listen-tools.ts';
import type { ModelContextTool } from './model-context.ts';

function song(
  id: number,
  title: string,
  artist: string,
  album: string,
  extra: Partial<ApiSong> = {}
) {
  return {
    id,
    title,
    artist,
    albumArtist: artist,
    album,
    fileName: `${title}.flac`,
    sourcePath: `/src/${title}.flac`,
    fileSizeBytes: 1,
    year: 2017,
    trackNumber: id,
    durationSeconds: 180,
    ...extra
  } as ApiSong;
}

const humble = song(1, 'HUMBLE.', 'Kendrick Lamar', 'DAMN.', { trackNumber: 8 });
const dna = song(2, 'DNA.', 'Kendrick Lamar', 'DAMN.', {
  trackNumber: 2,
  likedAtUtc: '2026-01-01'
});
const alright = song(3, 'Alright', 'Kendrick Lamar', 'To Pimp a Butterfly', {
  year: 2015,
  trackNumber: 7
});
const bestFriend = song(4, 'Best Friend (with Fall Out Boy)', 'Juice WRLD', 'Fighting Demons', {
  year: 2021
});
const songs = [humble, dna, alright, bestFriend];

const damn = {
  key: 'Kendrick Lamar/DAMN.',
  folderKeys: ['Kendrick Lamar/DAMN.', 'Kendrick Lamar/DAMN. (Collectors)'],
  nameKey: 'kendrick lamar::damn.',
  title: 'DAMN.',
  artist: 'Kendrick Lamar',
  year: 2017,
  trackCount: 2,
  trackIds: [2, 1],
  songs: [dna, humble]
} as unknown as AlbumSummary;

const artists = [
  { key: 'Kendrick Lamar', label: 'Kendrick Lamar', albumCount: 2, trackCount: 3 },
  { key: 'Juice WRLD', label: 'Juice WRLD', albumCount: 1, trackCount: 1 }
] as unknown as GroupSummary[];

const mix = {
  id: 7,
  name: 'Gym',
  source: null,
  songIds: [4, 99, 1],
  addedSongIds: [4, 99, 1],
  missingCount: 0
} as unknown as LibraryPlaylist;

function fakePlayer(startsPlaying = true) {
  const state = {
    currentSong: null as PlayerSong | null,
    isPlaying: false,
    queue: [] as PlayerSong[],
    index: -1
  };
  const player: PlayerPort & { state: typeof state } = {
    state,
    get currentSong() {
      return state.currentSong;
    },
    get isPlaying() {
      return state.isPlaying;
    },
    currentTime: 42.4,
    duration: 180,
    get hasNext() {
      return state.index < state.queue.length - 1;
    },
    get upNext() {
      return state.queue.slice(state.index + 1);
    },
    startQueue: vi.fn(async (queue: PlayerSong[], index: number) => {
      state.queue = queue;
      state.index = index;
      state.currentSong = queue[index];
      state.isPlaying = startsPlaying;
    }),
    pause: vi.fn(() => {
      state.isPlaying = false;
    }),
    resume: vi.fn(() => {
      state.isPlaying = startsPlaying;
    }),
    next: vi.fn(),
    previous: vi.fn(),
    seek: vi.fn()
  };
  return player;
}

function setup(overrides: Partial<ListenDeps> = {}) {
  const player = fakePlayer();
  const deps: ListenDeps = {
    library: async () => ({
      songs,
      songsById: new Map(songs.map((s) => [s.id, s])),
      albums: [damn],
      artists
    }),
    player,
    playlists: {
      list: async () => [mix],
      create: vi.fn(async (name: string, songIds: number[]) => ({ ...mix, id: 8, name, songIds })),
      addSongs: vi.fn(async () => ({ added: 1, alreadyPresent: 0 }))
    },
    setLiked: vi.fn(async (_id: number, liked: boolean) => liked),
    toPlayerSong: (s, fallback) => ({
      id: s.id,
      title: s.title ?? s.fileName,
      artist: s.artist ?? fallback,
      album: s.album,
      streamUrl: `/stream/${s.id}`
    }),
    navigate: vi.fn(async () => {}),
    waitUntilPlaying: async () => player.isPlaying,
    shuffle: (items) => [...items].reverse(),
    canLike: true,
    canEditPlaylists: true,
    ...overrides
  };
  const tools = createListenTools(deps);
  const call = (name: string, input: Record<string, unknown> = {}) => {
    const tool = tools.find((t) => t.name === name);
    if (!tool) throw new Error(`no tool ${name}`);
    return tool.execute(input, { signal: new AbortController().signal }) as Promise<
      Record<string, unknown>
    >;
  };
  return { deps, tools, call, player: deps.player as ReturnType<typeof fakePlayer> };
}

const names = (tools: ModelContextTool[]) => tools.map((t) => t.name);

describe('which tools an account gets', () => {
  it('offers the write tools only where the server would accept them', () => {
    expect(names(setup().tools)).toEqual(
      expect.arrayContaining(['set_song_liked', 'create_playlist', 'add_to_playlist'])
    );
    const demo = names(setup({ canLike: false, canEditPlaylists: false }).tools);
    expect(demo).not.toContain('set_song_liked');
    expect(demo).not.toContain('create_playlist');
    expect(demo).toContain('play_music');
    const memberWithoutListening = names(setup({ canLike: false }).tools);
    expect(memberWithoutListening).not.toContain('set_song_liked');
    expect(memberWithoutListening).toContain('add_to_playlist');
  });

  it('marks every read tool read-only, and every tool that returns tags untrusted', () => {
    for (const tool of setup().tools) {
      const reads =
        tool.name.startsWith('get_') ||
        tool.name.startsWith('list_') ||
        tool.name === 'search_library';
      expect(Boolean(tool.annotations?.readOnlyHint), tool.name).toBe(reads);
      if (reads) expect(tool.annotations?.untrustedContentHint, tool.name).toBe(true);
    }
  });

  it('uses only names the API accepts', () => {
    for (const tool of setup().tools) expect(tool.name).toMatch(/^[A-Za-z0-9_.-]{1,128}$/);
  });
});

describe('search_library', () => {
  it('matches every word across fields, as the app search does', async () => {
    const { call } = setup();
    const result = await call('search_library', {
      query: 'best friend with fall out',
      kind: 'songs'
    });
    expect((result.songs as { id: number }[]).map((s) => s.id)).toEqual([4]);
    const byArtist = await call('search_library', { query: 'kendrick humble' });
    expect((byArtist.songs as { id: number }[])[0].id).toBe(1);
  });

  it('returns albums with the key the other tools take, and artists by name', async () => {
    const { call } = setup();
    const result = await call('search_library', { query: 'damn' });
    expect(result.albums).toEqual([
      {
        albumKey: 'Kendrick Lamar/DAMN.',
        title: 'DAMN.',
        artist: 'Kendrick Lamar',
        year: 2017,
        trackCount: 2
      }
    ]);
    const people = await call('search_library', { query: 'juice', kind: 'artists' });
    expect(people).toEqual({ artists: [{ name: 'Juice WRLD', trackCount: 1, albumCount: 1 }] });
  });

  it('caps the results and explains a bad query instead of throwing', async () => {
    const { call } = setup();
    const capped = await call('search_library', { query: 'kendrick', kind: 'songs', limit: 1 });
    expect(capped.songs).toHaveLength(1);
    expect(await call('search_library', { query: '  ' })).toEqual({
      error: '"query" is required.'
    });
    expect(await call('search_library', { query: '!!' })).toEqual({
      error: '"query" needs at least one word or number.'
    });
    expect(await call('search_library', { query: 'x', kind: 'videos' })).toHaveProperty('error');
  });
});

describe('play_music', () => {
  it('plays an album in its own order', async () => {
    const { call, player } = setup();
    const result = await call('play_music', { albumKey: 'Kendrick Lamar/DAMN.' });
    expect(player.startQueue).toHaveBeenCalledOnce();
    const [queue, index] = vi.mocked(player.startQueue).mock.calls[0];
    expect(queue.map((s) => s.id)).toEqual([2, 1]);
    expect(index).toBe(0);
    expect(result).toMatchObject({ started: true, queueLength: 2, nowPlaying: { id: 2 } });
  });

  it('finds an album by a merged folder key too', async () => {
    const { call } = setup();
    const result = await call('play_music', { albumKey: 'Kendrick Lamar/DAMN. (Collectors)' });
    expect(result).toMatchObject({ started: true, queueLength: 2 });
  });

  it('starts at a chosen song, and shuffles when asked', async () => {
    const { call, player } = setup();
    await call('play_music', { albumKey: damn.key, startAtSongId: 1 });
    expect(vi.mocked(player.startQueue).mock.calls[0][1]).toBe(1);
    await call('play_music', { albumKey: damn.key, shuffle: true });
    expect(vi.mocked(player.startQueue).mock.calls[1][0].map((s) => s.id)).toEqual([1, 2]);
  });

  it('plays an artist album by album, in release order', async () => {
    const { call, player } = setup();
    await call('play_music', { artist: 'kendrick lamar' });
    expect(vi.mocked(player.startQueue).mock.calls[0][0].map((s) => s.id)).toEqual([3, 2, 1]);
  });

  it('plays a playlist, skipping ids the library no longer holds', async () => {
    const { call, player } = setup();
    await call('play_music', { playlistId: 7 });
    expect(vi.mocked(player.startQueue).mock.calls[0][0].map((s) => s.id)).toEqual([4, 1]);
  });

  it('wants exactly one source, and refuses unknown song ids by name', async () => {
    const { call, player } = setup();
    expect(await call('play_music', {})).toHaveProperty('error');
    expect(await call('play_music', { songIds: [1], albumKey: damn.key })).toHaveProperty('error');
    expect(await call('play_music', { songIds: [1, 1234] })).toEqual({
      error: 'These song ids are not in the library: 1234.'
    });
    expect(await call('play_music', { albumKey: 'nope' })).toHaveProperty('error');
    expect(player.startQueue).not.toHaveBeenCalled();
  });

  it('says so when the browser blocks autoplay, instead of claiming success', async () => {
    const player = fakePlayer(false);
    const { call } = setup({ player, waitUntilPlaying: async () => false });
    const result = await call('play_music', { songIds: ['1'] });
    expect(result.started).toBe(false);
    expect(result.note).toMatch(/autoplay/);
  });
});

describe('playback control', () => {
  it('reports the state with the next songs', async () => {
    const { call } = setup();
    await call('play_music', { albumKey: damn.key });
    const state = await call('get_playback_state');
    expect(state).toMatchObject({
      nowPlaying: { id: 2, title: 'DNA.' },
      isPlaying: true,
      positionSeconds: 42,
      upNext: [{ id: 1 }]
    });
  });

  it('steers the player, and refuses what cannot happen', async () => {
    const { call, player } = setup();
    expect(await call('control_playback', { action: 'pause' })).toHaveProperty('error');
    await call('play_music', { albumKey: damn.key });
    await call('control_playback', { action: 'pause' });
    expect(player.pause).toHaveBeenCalled();
    await call('control_playback', { action: 'seek', seconds: 90 });
    expect(player.seek).toHaveBeenCalledWith(90);
    expect(await call('control_playback', { action: 'seek' })).toHaveProperty('error');
    await call('control_playback', { action: 'next' });
    expect(player.next).toHaveBeenCalled();
    player.state.index = 1;
    expect(await call('control_playback', { action: 'next' })).toEqual({
      error: 'There is no next song.'
    });
  });

  it('starts a station from one song', async () => {
    const { call, player } = setup();
    await call('start_radio', { songId: 3 });
    expect(vi.mocked(player.startQueue).mock.calls[0][0].map((s) => s.id)).toEqual([3]);
    expect(await call('start_radio', { songId: 404 })).toHaveProperty('error');
  });
});

describe('likes and playlists', () => {
  it('sets a like to the asked state', async () => {
    const { call, deps } = setup();
    expect(await call('set_song_liked', { songId: 1, liked: true })).toEqual({
      songId: 1,
      liked: true
    });
    expect(deps.setLiked).toHaveBeenCalledWith(1, true);
    expect(await call('set_song_liked', { songId: 1 })).toEqual({ error: '"liked" is required.' });
  });

  it('lists playlists and reads one with its songs', async () => {
    const { call } = setup();
    expect(await call('list_playlists')).toEqual({
      playlists: [{ playlistId: 7, name: 'Gym', trackCount: 3, syncedFrom: null, missingTracks: 0 }]
    });
    const one = await call('get_playlist', { playlistId: 7, limit: 1 });
    expect(one).toMatchObject({ tracks: [{ id: 4 }], more: 1 });
  });

  it('creates and fills playlists with library songs only', async () => {
    const { call, deps } = setup();
    expect(await call('create_playlist', { name: 'Rainy', songIds: [1, 2, 2] })).toMatchObject({
      created: { playlistId: 8, name: 'Rainy', trackCount: 2 }
    });
    expect(deps.playlists.create).toHaveBeenCalledWith('Rainy', [1, 2]);
    expect(await call('add_to_playlist', { playlistId: 7, songIds: [3] })).toEqual({
      playlistId: 7,
      added: 1,
      alreadyPresent: 0
    });
    expect(await call('add_to_playlist', { playlistId: 70, songIds: [3] })).toHaveProperty('error');
    expect(await call('add_to_playlist', { playlistId: 7, songIds: [3000] })).toHaveProperty(
      'error'
    );
  });
});

describe('open_in_app', () => {
  it('navigates to the page the user would open', async () => {
    const { call, deps } = setup();
    await call('open_in_app', { page: 'album', albumKey: damn.key });
    await call('open_in_app', { page: 'artist', artist: 'Juice WRLD' });
    await call('open_in_app', { page: 'playlist', playlistId: 7 });
    await call('open_in_app', { page: 'song', songId: 4 });
    expect(vi.mocked(deps.navigate).mock.calls.map((c) => c[0])).toEqual([
      '/library?album=Kendrick%20Lamar%2FDAMN.',
      '/library?artist=Juice%20WRLD',
      '/playlists/7',
      '/track/4'
    ]);
    expect(await call('open_in_app', { page: 'song', songId: 5 })).toHaveProperty('error');
  });
});

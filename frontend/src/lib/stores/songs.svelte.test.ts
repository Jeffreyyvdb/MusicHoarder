import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The detail cards' load state is what lets Now Playing tell "this song is not in your library
 * view" from "the cards have not loaded" and "they failed to load" — the three used to share one
 * spinner-then-guess. Only the api-client is faked; each test gets a fresh store.
 */

const fetchAlbums = vi.fn();
const fetchSongs = vi.fn(async (): Promise<unknown[]> => []);
// The store's own loads ask conditionally. By default the server answers with the rows and no
// validator — an API that has none to give — so the tests that are not about revalidation read
// as they always did, through `fetchSongs` and `fetchAlbums`. The ones that are script these two.
const fetchSongsIfChanged = vi.fn();
const fetchAlbumsIfChanged = vi.fn();
const likeSong = vi.fn();
const closeStream = vi.fn();
const openProgressStream = vi.fn(() => closeStream);
// The network calls are stubbed; the pure helpers (the filters and grouping behind the store's
// shared cuts) stay real.
vi.mock('#lib/api-client.ts', async (importOriginal) => ({
  ...(await importOriginal<typeof import('#lib/api-client.ts')>()),
  currentGrantors: () => [],
  fetchAlbums: (...args: unknown[]) => fetchAlbums(...args),
  fetchAlbumsIfChanged: (...args: unknown[]) => fetchAlbumsIfChanged(...args),
  fetchSongs: () => fetchSongs(),
  fetchSongsIfChanged: (...args: unknown[]) => fetchSongsIfChanged(...args),
  hydrateAlbums: (dtos: unknown[]) => dtos,
  likeSong: (id: number) => likeSong(id),
  openProgressStream: () => openProgressStream(),
  unlikeSong: vi.fn()
}));

beforeEach(() => {
  vi.resetModules();
  fetchAlbums.mockReset();
  fetchSongs.mockReset();
  fetchSongs.mockResolvedValue([]);
  fetchSongsIfChanged.mockReset();
  fetchSongsIfChanged.mockImplementation(async () => ({
    notModified: false,
    data: await fetchSongs(),
    etag: null
  }));
  fetchAlbumsIfChanged.mockReset();
  fetchAlbumsIfChanged.mockImplementation(async (query: unknown) => ({
    notModified: false,
    data: await fetchAlbums(query),
    etag: null
  }));
  likeSong.mockReset();
  openProgressStream.mockClear();
  closeStream.mockClear();
});

async function freshStore() {
  const { songsStore } = await import('./songs.svelte');
  return songsStore;
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('detailAlbumsState', () => {
  it('is idle until the panel asks, then loading, then loaded', async () => {
    let resolve: (v: unknown[]) => void = () => {};
    fetchAlbums.mockReturnValue(new Promise((r) => (resolve = r)));
    const store = await freshStore();
    expect(store.detailAlbumsState).toBe('idle');
    store.ensureDetailAlbums();
    expect(store.detailAlbumsState).toBe('loading');
    resolve([{ key: 'a' }]);
    await flush();
    expect(store.detailAlbumsState).toBe('loaded');
    expect(store.detailAlbums).toHaveLength(1);
  });

  it('reports a failed first load as an error, and a retry recovers it', async () => {
    fetchAlbums.mockRejectedValueOnce(new Error('offline'));
    const store = await freshStore();
    store.ensureDetailAlbums();
    await flush();
    expect(store.detailAlbumsState).toBe('error');

    fetchAlbums.mockResolvedValueOnce([{ key: 'a' }]);
    await store.reloadDetailAlbums();
    expect(store.detailAlbumsState).toBe('loaded');
  });

  it('stays loaded through a background refresh, even a failed one', async () => {
    fetchAlbums.mockResolvedValueOnce([{ key: 'a' }]);
    const store = await freshStore();
    store.ensureDetailAlbums();
    await flush();
    fetchAlbums.mockRejectedValueOnce(new Error('blip'));
    await store.reloadDetailAlbums();
    expect(store.detailAlbumsState).toBe('loaded');
    expect(store.detailAlbums).toHaveLength(1);
  });

  it('goes back to idle on sign-out', async () => {
    fetchAlbums.mockResolvedValueOnce([{ key: 'a' }]);
    const store = await freshStore();
    store.ensureDetailAlbums();
    await flush();
    store.reset();
    expect(store.detailAlbumsState).toBe('idle');
  });
});

describe('shared cuts', () => {
  const song = (id: number, extra: Record<string, unknown>) => ({
    id,
    fileName: `${id}.mp3`,
    sourcePath: `/src/${id}.mp3`,
    fileSizeBytes: 1,
    title: `Song ${id}`,
    album: 'Album',
    ...extra
  });

  it('keeps built songs, the Tracks base and the lead artists in step with the rows', async () => {
    fetchSongs.mockResolvedValue([
      song(1, { isBuilt: true, artist: 'Nina' }),
      song(2, { isBuilt: false, artist: 'Nina' }),
      // Album completion filled this one in: built, but not yours until you like it.
      song(3, { isBuilt: true, artist: 'Otis', acquisitionIntent: 'AlbumFill' })
    ]);
    fetchAlbums.mockResolvedValue([]);
    const store = await freshStore();
    const release = store.retainViews();
    await store.loadSongs();
    await flush();

    expect(store.builtSongs.map((s) => s.id)).toEqual([1, 3]);
    expect(store.trackListSongs.map((s) => s.id)).toEqual([1]);
    expect(store.leadArtistGroups.map((g) => g.label)).toEqual(['Nina', 'Otis']);

    // A like promotes the album-fill track into the Tracks list, without a refetch.
    likeSong.mockResolvedValue({ likedAtUtc: '2026-09-24T10:00:00Z' });
    await store.toggleLike(3);
    await flush();
    expect(store.trackListSongs.map((s) => s.id)).toEqual([1, 3]);

    release();
    release(); // a second release is a no-op, not a stolen hold
    expect(store.builtSongs.map((s) => s.id)).toEqual([1, 3]);
  });
});

describe('a play', () => {
  // The player notes a play right after `play()`. Whatever that changes re-derives in the same
  // task, before the browser asks for the audio — and a change to the rows re-derives the whole
  // library. So the rows stay as they are, and the play shows a moment later, beside them.
  const NOW = new Date('2026-09-24T10:00:00.000Z');
  const row = (id: number, extra: Record<string, unknown> = {}) => ({
    id,
    fileName: `${id}.mp3`,
    sourcePath: `/src/${id}.mp3`,
    fileSizeBytes: 1,
    title: `Song ${id}`,
    album: 'Album',
    artist: 'Nina',
    isBuilt: true,
    ...extra
  });

  beforeEach(() => {
    vi.useFakeTimers();
    vi.setSystemTime(NOW);
    fetchAlbums.mockResolvedValue([]);
  });
  afterEach(() => vi.useRealTimers());

  async function loaded(rows: unknown[]) {
    fetchSongs.mockResolvedValue(rows);
    const store = await freshStore();
    await store.loadSongs();
    return store;
  }

  it('is counted beside the rows: the library itself is not replaced, so nothing made from it is made again', async () => {
    const store = await loaded([row(1, { playCount: 4 }), row(2)]);
    const rows = store.songs;

    store.notePlayed(1);
    vi.advanceTimersByTime(1000);

    expect(store.playStatsOf(rows[0])).toEqual({
      playCount: 5,
      lastPlayedAtUtc: NOW.toISOString()
    });
    // The same array of the same rows: every cut, group and sorted list derived from it stands.
    expect(store.songs).toBe(rows);
    expect(store.songs[0]).toBe(rows[0]);
    expect(rows[0].playCount).toBe(4);
  });

  it('shows once the track is under way, not in the task that started it', async () => {
    const store = await loaded([
      row(1),
      row(2, { playCount: 2, lastPlayedAtUtc: '2026-01-01T00:00:00Z' })
    ]);
    const [one, two] = store.songs;

    store.notePlayed(1);
    expect(store.playStatsOf(one).playCount).toBeUndefined();
    vi.advanceTimersByTime(999);
    expect(store.playStatsOf(one).playCount).toBeUndefined();

    // A second later it shows, stamped with when the play happened rather than when it showed.
    vi.advanceTimersByTime(1);
    expect(store.playStatsOf(one)).toEqual({ playCount: 1, lastPlayedAtUtc: NOW.toISOString() });
    // A song nobody played reads as its row says.
    expect(store.playStatsOf(two)).toMatchObject({
      playCount: 2,
      lastPlayedAtUtc: '2026-01-01T00:00:00Z'
    });
  });

  it('counts every play, of one song twice and of two songs skipped through', async () => {
    const store = await loaded([row(1, { playCount: 1 }), row(2)]);
    store.notePlayed(1);
    vi.advanceTimersByTime(400);
    store.notePlayed(2);
    vi.advanceTimersByTime(600);
    expect(store.playStatsOf(store.songs[0]).playCount).toBe(2);
    expect(store.playStatsOf(store.songs[1]).playCount).toBe(1);

    vi.setSystemTime(new Date('2026-09-24T10:05:00.000Z'));
    store.notePlayed(1);
    vi.advanceTimersByTime(1000);
    expect(store.playStatsOf(store.songs[0])).toEqual({
      playCount: 3,
      lastPlayedAtUtc: '2026-09-24T10:05:00.000Z'
    });
  });

  it('survives a like, which does replace the row', async () => {
    likeSong.mockResolvedValue({ likedAtUtc: '2026-09-24T10:00:00Z' });
    const store = await loaded([row(1, { playCount: 4 })]);
    store.notePlayed(1);
    vi.advanceTimersByTime(1000);
    await store.toggleLike(1);
    expect(store.songs[0].likedAtUtc).toBe('2026-09-24T10:00:00Z');
    expect(store.playStatsOf(store.songs[0]).playCount).toBe(5);
  });

  it("gives way to the server's count when the library is fetched again", async () => {
    const store = await loaded([row(1, { playCount: 4 })]);
    store.notePlayed(1);
    vi.advanceTimersByTime(1000);
    store.notePlayed(1); // still waiting to be shown when the fresh rows land

    fetchSongs.mockResolvedValue([
      row(1, { playCount: 6, lastPlayedAtUtc: '2026-09-24T10:00:01Z' })
    ]);
    await store.loadSongs({ silent: true });
    vi.advanceTimersByTime(60_000);
    expect(store.playStatsOf(store.songs[0])).toMatchObject({
      playCount: 6,
      lastPlayedAtUtc: '2026-09-24T10:00:01Z'
    });
  });

  it('is nothing to show for a song the library does not hold, and is forgotten on sign-out', async () => {
    const store = await loaded([row(1)]);
    store.notePlayed(99); // a share link's track
    vi.advanceTimersByTime(1000);
    expect(store.playStatsOf({ id: 99 })).toEqual({ id: 99 });

    store.notePlayed(1);
    store.reset();
    vi.advanceTimersByTime(60_000);
    expect(store.playStatsOf(row(1)).playCount).toBeUndefined();
    expect(vi.getTimerCount()).toBe(0);
  });
});

describe('revalidate', () => {
  afterEach(() => vi.useRealTimers());

  it('loads once, reuses a fresh copy, and refreshes a stale one in the background', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-09-24T10:00:00Z'));
    fetchAlbums.mockResolvedValue([]);
    const store = await freshStore();

    store.revalidate();
    // A second page opening mid-load does not start another.
    store.revalidate();
    expect(fetchSongs).toHaveBeenCalledOnce();
    expect(store.isLoading).toBe(true);
    await flush();
    expect(store.isLoading).toBe(false);

    // Moving between pages inside the window uses what is held.
    vi.setSystemTime(new Date('2026-09-24T10:00:29Z'));
    store.revalidate();
    expect(fetchSongs).toHaveBeenCalledOnce();

    // Past it, the page still opens on the held copy while a refresh runs silently.
    vi.setSystemTime(new Date('2026-09-24T10:00:30Z'));
    store.revalidate();
    expect(fetchSongs).toHaveBeenCalledTimes(2);
    expect(store.isLoading).toBe(false);
    await flush();
  });

  it('retries a first load that failed', async () => {
    fetchSongs.mockRejectedValueOnce(new Error('offline'));
    fetchAlbums.mockResolvedValue([]);
    const store = await freshStore();
    store.revalidate();
    await flush();
    expect(store.error).toBe('offline');
    store.revalidate();
    await flush();
    expect(fetchSongs).toHaveBeenCalledTimes(2);
    expect(store.error).toBeNull();
  });
});

describe('live stream', () => {
  afterEach(() => vi.useRealTimers());

  it('stays open while pages hand it on, and closes once nothing has held it for a while', async () => {
    vi.useFakeTimers();
    const store = await freshStore();
    store.startLive();
    expect(openProgressStream).toHaveBeenCalledOnce();

    // Leaving one library page for another: released, then taken straight back.
    store.stopLive();
    store.startLive();
    vi.advanceTimersByTime(60_000);
    expect(openProgressStream).toHaveBeenCalledOnce();
    expect(closeStream).not.toHaveBeenCalled();

    store.stopLive();
    vi.advanceTimersByTime(9_999);
    expect(closeStream).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(closeStream).toHaveBeenCalledOnce();
  });

  it('closes at once on sign-out', async () => {
    vi.useFakeTimers();
    const store = await freshStore();
    store.startLive();
    store.stopLive();
    store.reset();
    expect(closeStream).toHaveBeenCalledOnce();
    vi.advanceTimersByTime(60_000);
    expect(closeStream).toHaveBeenCalledOnce();
  });
});

/**
 * The store offers the server the validator each list arrived with, and on "unchanged" keeps the
 * arrays it holds. The danger is the other direction — taking "unchanged" at its word when the
 * rows it was about are gone — so most of these are about when the store asks again instead.
 */
describe('revalidating against the server', () => {
  afterEach(() => vi.useRealTimers());

  const song = (id: number, extra: Record<string, unknown> = {}) => ({
    id,
    fileName: `${id}.mp3`,
    sourcePath: `/src/${id}.mp3`,
    fileSizeBytes: 1,
    title: `Song ${id}`,
    album: 'Album',
    isBuilt: true,
    ...extra
  });
  const rows = <T>(data: T, etag: string | null) => ({ notModified: false as const, data, etag });
  const unchanged = (etag: string) => ({ notModified: true as const, etag });

  /** A store that has loaded `songs` and `cards`, holding the validators `s1` and `a1`. */
  async function loadedStore(
    songs: unknown[] = [song(1), song(2)],
    cards: unknown[] = [{ key: 'a' }]
  ) {
    fetchSongsIfChanged.mockResolvedValueOnce(rows(songs, 's1'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(rows(cards, 'a1'));
    const store = await freshStore();
    await store.loadSongs();
    return { store, songs, cards };
  }

  it('asks outright the first time, then offers what it holds and keeps the same rows', async () => {
    const { store, songs, cards } = await loadedStore();
    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith(null);
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({}, null);

    fetchSongsIfChanged.mockResolvedValueOnce(unchanged('s1'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('a1'));
    await store.loadSongs({ silent: true });

    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith('s1');
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({}, 'a1');
    // The very arrays, not equal copies: a new array would re-derive every view and re-render.
    expect(store.songs).toBe(songs);
    expect(store.albums).toBe(cards);
    expect(store.error).toBeNull();
  });

  it('counts "unchanged" as a load: the copy is fresh again and an old error is cleared', async () => {
    vi.useFakeTimers({ toFake: ['Date'] });
    vi.setSystemTime(new Date('2026-09-24T10:00:00Z'));
    const { store, songs } = await loadedStore();

    // A refresh that could not reach the server leaves the rows, and says so.
    vi.setSystemTime(new Date('2026-09-24T10:00:30Z'));
    fetchSongsIfChanged.mockRejectedValueOnce(new Error('offline'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('a1'));
    store.revalidate();
    await flush();
    expect(store.error).toBe('offline');
    expect(store.songs).toBe(songs);

    fetchSongsIfChanged.mockResolvedValueOnce(unchanged('s1'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('a1'));
    store.revalidate();
    await flush();
    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith('s1');
    expect(store.error).toBeNull();
    expect(store.songs).toBe(songs);

    // Fresh for another half minute, exactly as after a download.
    vi.setSystemTime(new Date('2026-09-24T10:00:59Z'));
    store.revalidate();
    expect(fetchSongsIfChanged).toHaveBeenCalledTimes(3);
    vi.setSystemTime(new Date('2026-09-24T10:01:00Z'));
    store.revalidate();
    expect(fetchSongsIfChanged).toHaveBeenCalledTimes(4);
    await flush();
  });

  it('replaces only the list that changed, and holds its new validator', async () => {
    const { store, songs } = await loadedStore();
    const regrouped = [{ key: 'a' }, { key: 'b' }];

    fetchSongsIfChanged.mockResolvedValueOnce(unchanged('s1'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(rows(regrouped, 'a2'));
    await store.loadSongs({ silent: true });
    expect(store.songs).toBe(songs);
    expect(store.albums).toBe(regrouped);

    await store.loadSongs({ silent: true });
    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith('s1');
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({}, 'a2');
  });

  it('asks outright again when the server gave no validator', async () => {
    fetchSongs.mockResolvedValue([song(1)]);
    fetchAlbums.mockResolvedValue([]);
    const store = await freshStore();
    await store.loadSongs();
    await store.loadSongs({ silent: true });
    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith(null);
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({}, null);
  });

  it('forgets the validators on sign-out', async () => {
    const { store } = await loadedStore();
    store.reset();
    expect(store.songs).toEqual([]);

    fetchSongs.mockResolvedValue([song(9)]);
    fetchAlbums.mockResolvedValue([]);
    await store.loadSongs();
    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith(null);
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({}, null);
    expect(store.songs.map((s) => s.id)).toEqual([9]);
  });

  it('asks again when a sign-out emptied the store while "unchanged" was on its way', async () => {
    const { store } = await loadedStore();
    let answer: (v: unknown) => void = () => {};
    fetchSongsIfChanged.mockReturnValueOnce(new Promise((r) => (answer = r)));
    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('a1'));
    const refresh = store.loadSongs({ silent: true });

    store.reset();
    const sentAgain = [song(7)];
    fetchSongsIfChanged.mockResolvedValueOnce(rows(sentAgain, 's2'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(rows([{ key: 'z' }], 'a2'));
    answer(unchanged('s1'));
    await refresh;

    // Not "loaded, with nothing in it": the lists themselves were asked for, and these are they.
    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith(null);
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({}, null);
    expect(store.songs).toBe(sentAgain);
    expect(store.albums).toEqual([{ key: 'z' }]);
  });

  it('stays unloaded when that second request fails, so the next page visit loads again', async () => {
    const { store } = await loadedStore();
    let answer: (v: unknown) => void = () => {};
    fetchSongsIfChanged.mockReturnValueOnce(new Promise((r) => (answer = r)));
    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('a1'));
    const refresh = store.loadSongs({ silent: true });

    store.reset();
    fetchSongsIfChanged.mockRejectedValueOnce(new Error('signed out'));
    fetchAlbumsIfChanged.mockRejectedValueOnce(new Error('signed out'));
    answer(unchanged('s1'));
    await refresh;
    expect(store.songs).toEqual([]);
    expect(store.error).toBe('signed out');

    // A first load, spinner and all — the store does not believe it holds a library.
    fetchSongs.mockResolvedValue([song(3)]);
    fetchAlbums.mockResolvedValue([]);
    store.revalidate();
    expect(store.isLoading).toBe(true);
    await flush();
    expect(store.songs.map((s) => s.id)).toEqual([3]);
  });

  it('drops the songs validator when a play is counted here, so a refresh still overwrites it', async () => {
    const { store } = await loadedStore();
    store.notePlayed(1);

    // The server never heard of that play (a demo account's report is refused): the next refresh
    // must bring its own count back, which "unchanged" would not.
    fetchSongsIfChanged.mockResolvedValueOnce(rows([song(1, { playCount: 0 }), song(2)], 's1'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('a1'));
    await store.loadSongs({ silent: true });
    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith(null);
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({}, 'a1');
    expect(store.playStatsOf(store.songs[0]).playCount).toBe(0);
  });

  it('keeps the songs validator when the track played is not one of its rows', async () => {
    const { store, songs } = await loadedStore();
    // A track streamed through a share link (one sent in a chat): no row here counts its plays,
    // so the rows are still exactly what the server sent and a refresh may be told "unchanged".
    store.notePlayed(99);

    fetchSongsIfChanged.mockResolvedValueOnce(unchanged('s1'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('a1'));
    await store.loadSongs({ silent: true });
    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith('s1');
    expect(store.songs).toBe(songs);
  });

  it('asks again when a like landed while "unchanged" was on its way', async () => {
    const { store } = await loadedStore();
    let answer: (v: unknown) => void = () => {};
    fetchSongsIfChanged.mockReturnValueOnce(new Promise((r) => (answer = r)));
    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('a1'));
    const refresh = store.loadSongs({ silent: true });

    likeSong.mockResolvedValue({ likedAtUtc: '2026-09-24T10:00:00Z' });
    await store.toggleLike(2);
    const sentAgain = [song(1), song(2, { likedAtUtc: '2026-09-24T10:00:00Z' })];
    fetchSongsIfChanged.mockResolvedValueOnce(rows(sentAgain, 's2'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(rows([{ key: 'a' }], 'a2'));
    answer(unchanged('s1'));
    await refresh;

    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith(null);
    expect(store.songs).toBe(sentAgain);
  });

  it('asks again when another load replaced the rows while "unchanged" was on its way', async () => {
    const { store } = await loadedStore();
    let answer: (v: unknown) => void = () => {};
    fetchSongsIfChanged.mockReturnValueOnce(new Promise((r) => (answer = r)));
    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('a1'));
    const early = store.loadSongs({ silent: true });

    // A second load, started later, finds the library changed and lands first.
    const newer = [song(1), song(2), song(3)];
    fetchSongsIfChanged.mockResolvedValueOnce(rows(newer, 's2'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('a1'));
    await store.loadSongs({ silent: true });
    expect(store.songs).toBe(newer);

    // The early one's "unchanged" was about the rows that load just replaced.
    const newest = [song(1), song(2), song(3), song(4)];
    fetchSongsIfChanged.mockResolvedValueOnce(rows(newest, 's3'));
    fetchAlbumsIfChanged.mockResolvedValueOnce(rows([{ key: 'a' }], 'a3'));
    answer(unchanged('s1'));
    await early;
    expect(fetchSongsIfChanged).toHaveBeenLastCalledWith(null);
    expect(store.songs).toBe(newest);
  });

  it('drops the cards validator when a like re-fetches the cards on their own', async () => {
    const { store } = await loadedStore([song(1, { acquisitionIntent: 'AlbumFill' })]);
    likeSong.mockResolvedValue({ likedAtUtc: '2026-09-24T10:00:00Z' });
    fetchAlbums.mockResolvedValue([{ key: 'moved' }]);
    await store.toggleLike(1);
    await flush();
    expect(store.albums).toEqual([{ key: 'moved' }]);

    fetchSongs.mockResolvedValue([song(1)]);
    await store.loadSongs({ silent: true });
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({}, null);
  });

  it('revalidates the detail cards with their own validator', async () => {
    const detail = [{ key: 'a' }, { key: 'a2' }];
    fetchAlbumsIfChanged.mockResolvedValueOnce(rows(detail, 'd1'));
    const store = await freshStore();
    store.ensureDetailAlbums();
    await flush();
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({ builtOnly: false, merge: false }, null);

    fetchAlbumsIfChanged.mockResolvedValueOnce(unchanged('d1'));
    await store.reloadDetailAlbums();
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({ builtOnly: false, merge: false }, 'd1');
    expect(store.detailAlbumsState).toBe('loaded');
    expect(store.detailAlbums).toBe(detail);
  });

  it('leaves no detail cards behind when a sign-out crosses their "unchanged"', async () => {
    fetchAlbumsIfChanged.mockResolvedValueOnce(rows([{ key: 'a' }], 'd1'));
    const store = await freshStore();
    store.ensureDetailAlbums();
    await flush();

    let answer: (v: unknown) => void = () => {};
    fetchAlbumsIfChanged.mockReturnValueOnce(new Promise((r) => (answer = r)));
    const refresh = store.reloadDetailAlbums();
    store.reset();
    answer(unchanged('d1'));
    await refresh;

    // Idle and empty — not "loaded" over nothing, which the panel would read as "not in your library".
    expect(store.detailAlbumsState).toBe('idle');
    expect(store.detailAlbums).toEqual([]);

    store.ensureDetailAlbums();
    expect(fetchAlbumsIfChanged).toHaveBeenLastCalledWith({ builtOnly: false, merge: false }, null);
    await flush();
  });
});

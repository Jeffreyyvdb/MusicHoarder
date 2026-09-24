import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The detail cards' load state is what lets Now Playing tell "this song is not in your library
 * view" from "the cards have not loaded" and "they failed to load" — the three used to share one
 * spinner-then-guess. Only the api-client is faked; each test gets a fresh store.
 */

const fetchAlbums = vi.fn();
const fetchSongs = vi.fn(async (): Promise<unknown[]> => []);
const likeSong = vi.fn();
const closeStream = vi.fn();
const openProgressStream = vi.fn(() => closeStream);
// The network calls are stubbed; the pure helpers (the filters and grouping behind the store's
// shared cuts) stay real.
vi.mock('$lib/api-client', async (importOriginal) => ({
  ...(await importOriginal<typeof import('$lib/api-client')>()),
  currentGrantors: () => [],
  fetchAlbums: (...args: unknown[]) => fetchAlbums(...args),
  fetchSongs: () => fetchSongs(),
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

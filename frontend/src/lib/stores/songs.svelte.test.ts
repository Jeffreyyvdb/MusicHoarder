import { beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The detail cards' load state is what lets Now Playing tell "this song is not in your library
 * view" from "the cards have not loaded" and "they failed to load" — the three used to share one
 * spinner-then-guess. Only the api-client is faked; each test gets a fresh store.
 */

const fetchAlbums = vi.fn();
vi.mock('$lib/api-client', () => ({
  currentGrantors: () => [],
  fetchAlbums: (...args: unknown[]) => fetchAlbums(...args),
  fetchSongs: vi.fn(async () => []),
  hydrateAlbums: (dtos: unknown[]) => dtos,
  likeSong: vi.fn(),
  openProgressStream: vi.fn(() => () => {}),
  unlikeSong: vi.fn()
}));

beforeEach(() => {
  vi.resetModules();
  fetchAlbums.mockReset();
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

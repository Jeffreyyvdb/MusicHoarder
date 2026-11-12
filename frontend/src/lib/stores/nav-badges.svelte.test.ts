import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The shell shows two numbers for the Inbox's name queues. It used to fetch the three reports
 * behind them and count the rows; it now asks the server for the two numbers. Only the api-client
 * and the songs store are faked; each test gets a fresh store.
 */

const fetchInboxNameCounts = vi.fn();
const fetchArtistDuplicates = vi.fn();
const fetchAlbumDuplicates = vi.fn();
const fetchSplitAlbums = vi.fn();
vi.mock('#lib/api-client.ts', async (importOriginal) => ({
  ...(await importOriginal<typeof import('#lib/api-client.ts')>()),
  fetchInboxNameCounts: () => fetchInboxNameCounts(),
  fetchArtistDuplicates: () => fetchArtistDuplicates(),
  fetchAlbumDuplicates: () => fetchAlbumDuplicates(),
  fetchSplitAlbums: () => fetchSplitAlbums()
}));
vi.mock('#lib/stores/songs.svelte.ts', () => ({ songsStore: { songs: [], isLoading: false } }));

beforeEach(() => {
  vi.resetModules();
  // The store does nothing during server rendering; any window will do.
  vi.stubGlobal('window', {});
  for (const mock of [fetchInboxNameCounts, fetchArtistDuplicates, fetchAlbumDuplicates, fetchSplitAlbums])
    mock.mockReset();
});

afterEach(() => {
  vi.unstubAllGlobals();
});

const freshStore = () => import('./nav-badges.svelte');

describe('refreshInboxNameCounts', () => {
  it('fills both name rows from one request, and never asks for a report', async () => {
    fetchInboxNameCounts.mockResolvedValue({ artists: 6, albums: 825 });
    const store = await freshStore();
    expect(store.inboxCounts()).toMatchObject({ artists: null, albums: null });

    await store.refreshInboxNameCounts(true);

    expect(store.inboxCounts()).toMatchObject({ artists: 6, albums: 825 });
    expect(fetchInboxNameCounts).toHaveBeenCalledTimes(1);
    expect(fetchArtistDuplicates).not.toHaveBeenCalled();
    expect(fetchAlbumDuplicates).not.toHaveBeenCalled();
    expect(fetchSplitAlbums).not.toHaveBeenCalled();
  });

  it('leaves the name rows out of the badge', async () => {
    fetchInboxNameCounts.mockResolvedValue({ artists: 6, albums: 825 });
    const store = await freshStore();

    await store.refreshInboxNameCounts(true);

    expect(store.inboxBadgeCount()).toBeNull();
  });

  it('keeps the figures it has when the request fails', async () => {
    fetchInboxNameCounts.mockResolvedValueOnce({ artists: 6, albums: 825 });
    const store = await freshStore();
    await store.refreshInboxNameCounts(true);

    fetchInboxNameCounts.mockRejectedValueOnce(new Error('offline'));
    await store.refreshInboxNameCounts(true);

    expect(store.inboxCounts()).toMatchObject({ artists: 6, albums: 825 });
  });

  it('asks at most once a minute unless forced, and shares a request already in flight', async () => {
    fetchInboxNameCounts.mockResolvedValue({ artists: 1, albums: 2 });
    const store = await freshStore();

    await Promise.all([store.refreshInboxNameCounts(true), store.refreshInboxNameCounts(true)]);
    expect(fetchInboxNameCounts).toHaveBeenCalledTimes(1);

    await store.refreshInboxNameCounts();
    expect(fetchInboxNameCounts).toHaveBeenCalledTimes(1);

    await store.refreshInboxNameCounts(true);
    expect(fetchInboxNameCounts).toHaveBeenCalledTimes(2);
  });

  it('lets a queue that has just loaded overrule the fetched figure', async () => {
    fetchInboxNameCounts.mockResolvedValue({ artists: 6, albums: 825 });
    const store = await freshStore();
    await store.refreshInboxNameCounts(true);

    store.publishInboxQueueCount('albums', 824);

    expect(store.inboxCounts()).toMatchObject({ artists: 6, albums: 824 });
  });
});

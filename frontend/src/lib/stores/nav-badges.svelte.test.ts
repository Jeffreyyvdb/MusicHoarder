import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The shell shows two numbers for the Inbox's name queues. It used to fetch the three reports
 * behind them and count the rows; it now asks the server for the two numbers. Only the api-client
 * and the songs store are faked; each test gets a fresh store.
 */

const fetchInboxDecisionCounts = vi.fn();
const fetchDuplicates = vi.fn();
const fetchQualityOverview = vi.fn();
const fetchInboxNameCounts = vi.fn();
const fetchArtistDuplicates = vi.fn();
const fetchAlbumDuplicates = vi.fn();
const fetchSplitAlbums = vi.fn();
vi.mock('#lib/api-client.ts', async (importOriginal) => ({
  ...(await importOriginal<typeof import('#lib/api-client.ts')>()),
  fetchInboxDecisionCounts: () => fetchInboxDecisionCounts(),
  fetchDuplicates: () => fetchDuplicates(),
  fetchQualityOverview: () => fetchQualityOverview(),
  fetchInboxNameCounts: () => fetchInboxNameCounts(),
  fetchArtistDuplicates: () => fetchArtistDuplicates(),
  fetchAlbumDuplicates: () => fetchAlbumDuplicates(),
  fetchSplitAlbums: () => fetchSplitAlbums()
}));
const songsFake = vi.hoisted(() => ({
  songs: [] as unknown[],
  isLoading: false,
  enrichmentStatusCounts: {} as Record<string, number>
}));
vi.mock('#lib/stores/songs.svelte.ts', () => ({ songsStore: songsFake }));

beforeEach(() => {
  vi.resetModules();
  songsFake.songs = [];
  songsFake.enrichmentStatusCounts = {};
  // The store does nothing during server rendering; any window will do.
  vi.stubGlobal('window', {});
  for (const mock of [
    fetchInboxDecisionCounts,
    fetchDuplicates,
    fetchQualityOverview,
    fetchInboxNameCounts,
    fetchArtistDuplicates,
    fetchAlbumDuplicates,
    fetchSplitAlbums
  ])
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

describe('refreshInboxQueueCounts', () => {
  it('fills both decision rows from one lightweight request, without fetching duplicates or quality overview', async () => {
    fetchInboxDecisionCounts.mockResolvedValue({ dupes: 4, ai: 9 });
    const store = await freshStore();

    await store.refreshInboxQueueCounts(true);

    expect(store.inboxCounts()).toMatchObject({ dupes: 4, ai: 9, total: 13 });
    expect(store.inboxBadgeCount()).toBe(13);
    expect(fetchInboxDecisionCounts).toHaveBeenCalledTimes(1);
    expect(fetchDuplicates).not.toHaveBeenCalled();
    expect(fetchQualityOverview).not.toHaveBeenCalled();
  });
});

describe('inboxCounts', () => {
  it("reads Tag review and failed from the store's one tally, not by filtering the rows", async () => {
    // Rows whose own statuses disagree with the tally: a count taken by filtering them would say 0.
    songsFake.songs = [{ id: 1 }, { id: 2 }];
    songsFake.enrichmentStatusCounts = { needsreview: 7, failed: 3 };
    const store = await freshStore();
    publishDecisionCounts(store);

    expect(store.inboxCounts()).toMatchObject({ review: 7, failed: 3, total: 7 + 2 + 1 });
    expect(store.tagReviewCount()).toBe(7);
  });

  it('knows no song figure before the library has loaded', async () => {
    const store = await freshStore();
    expect(store.inboxCounts()).toMatchObject({ review: null, failed: null, total: null });
  });
});

function publishDecisionCounts(store: Awaited<ReturnType<typeof freshStore>>) {
  store.publishInboxQueueCount('dupes', 2);
  store.publishInboxQueueCount('ai', 1);
}

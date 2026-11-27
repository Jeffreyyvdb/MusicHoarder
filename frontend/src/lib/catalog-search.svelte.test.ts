import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createCatalogSearch } from '#lib/catalog-search.svelte.ts';

const api = vi.hoisted(() => ({ search: vi.fn() }));
vi.mock('#lib/api-client.ts', () => ({ searchCatalogTracks: api.search }));

const track = (id: string) => ({
  deezerId: id,
  title: id,
  artist: 'A',
  album: null,
  durationMs: 1000,
  coverUrl: null,
  explicit: false,
  hasPreview: true,
  wishlistStatus: null
});

/** A search that stays open until the test settles it, and rejects when aborted like fetch. */
function deferred(signal: AbortSignal | undefined) {
  let settle!: (tracks: unknown[]) => void;
  const promise = new Promise((resolve, reject) => {
    settle = resolve;
    signal?.addEventListener('abort', () => reject(new DOMException('aborted', 'AbortError')));
  });
  return { promise, settle };
}

beforeEach(() => {
  vi.useFakeTimers();
  api.search.mockReset();
});
afterEach(() => vi.useRealTimers());

describe('createCatalogSearch', () => {
  it('waits out the typing pause and ignores queries under two characters', async () => {
    api.search.mockResolvedValue([track('x')]);
    const s = createCatalogSearch();
    s.set('a');
    await vi.advanceTimersByTimeAsync(1000);
    expect(api.search).not.toHaveBeenCalled();
    expect(s.status).toBe('idle');

    s.set('ab');
    expect(s.status).toBe('loading');
    await vi.advanceTimersByTimeAsync(299);
    expect(api.search).not.toHaveBeenCalled();
    await vi.advanceTimersByTimeAsync(1);
    expect(api.search).toHaveBeenCalledWith('ab', expect.any(AbortSignal));
    expect(s.status).toBe('ready');
    expect(s.results.map((t) => t.deezerId)).toEqual(['x']);
  });

  it('aborts the stale request and keeps only the newest answer', async () => {
    const slow = deferred(undefined);
    api.search.mockImplementationOnce((_q: string, signal: AbortSignal) => {
      const d = deferred(signal);
      slow.settle = d.settle;
      return d.promise;
    });
    api.search.mockResolvedValueOnce([track('new')]);
    const s = createCatalogSearch();

    s.set('old');
    await vi.advanceTimersByTimeAsync(300);
    const firstSignal = api.search.mock.calls[0][1] as AbortSignal;
    s.set('newer');
    expect(firstSignal.aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(300);
    slow.settle([track('old')]);
    await vi.advanceTimersByTimeAsync(0);

    expect(s.results.map((t) => t.deezerId)).toEqual(['new']);
    expect(s.status).toBe('ready');
  });

  it('reports the server message on failure and retries the same query', async () => {
    api.search.mockRejectedValueOnce(new Error('Deezer is down'));
    api.search.mockResolvedValueOnce([track('y')]);
    const s = createCatalogSearch();
    s.set('query');
    await vi.advanceTimersByTimeAsync(300);
    expect(s.status).toBe('error');
    expect(s.error).toBe('Deezer is down');

    s.retry();
    await vi.advanceTimersByTimeAsync(0);
    expect(api.search).toHaveBeenLastCalledWith('query', expect.any(AbortSignal));
    expect(s.status).toBe('ready');
    expect(s.error).toBeNull();
  });

  it('clearing the query drops results and cancels a pending request', async () => {
    api.search.mockResolvedValue([track('z')]);
    const s = createCatalogSearch();
    s.set('hello');
    await vi.advanceTimersByTimeAsync(300);
    s.set('');
    expect(s.status).toBe('idle');
    expect(s.results).toEqual([]);
  });
});

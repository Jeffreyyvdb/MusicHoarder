import { afterEach, describe, expect, it, vi } from 'vitest';
import { collectPages, fetchAllQualitySongs, type QualitySongRow } from './api-client';

/*
 * The Inbox's AI flagged queue is a whole quality category, paged in full. It used to read the
 * overview's top-50 worst offenders, which Ungradeable grades had crowded out entirely.
 */

type Row = { id: number };

/** A skip/take endpoint over `rows`, reporting `total` (defaults to the real length). */
function server(rows: Row[], opts: { total?: number; ignoreSkip?: boolean } = {}) {
  return vi.fn(async (skip: number, take: number) => ({
    total: opts.total ?? rows.length,
    items: rows.slice(opts.ignoreSkip ? 0 : skip, (opts.ignoreSkip ? 0 : skip) + take)
  }));
}

const rows = (n: number): Row[] => Array.from({ length: n }, (_, i) => ({ id: i + 1 }));
const byId = (r: Row) => r.id;

describe('collectPages', () => {
  it('walks the pages until it has the total', async () => {
    const page = server(rows(715));
    const all = await collectPages(page, 500, byId);
    expect(all.map(byId)).toEqual(rows(715).map(byId));
    expect(page.mock.calls).toEqual([
      [0, 500],
      [500, 500]
    ]);
  });

  it('asks once for an empty list', async () => {
    const page = server([]);
    await expect(collectPages(page, 500, byId)).resolves.toEqual([]);
    expect(page).toHaveBeenCalledTimes(1);
  });

  it('stops on an empty page when the total overstates the list', async () => {
    const page = server(rows(3), { total: 10 });
    const all = await collectPages(page, 2, byId);
    expect(all.map(byId)).toEqual([1, 2, 3]);
    expect(page).toHaveBeenCalledTimes(3);
  });

  it('still ends when the server ignores skip, and keeps each row once', async () => {
    const page = server(rows(5), { total: 5, ignoreSkip: true });
    const all = await collectPages(page, 2, byId);
    expect(all.map(byId)).toEqual([1, 2]);
    expect(page).toHaveBeenCalledTimes(3);
  });

  it('keeps the first copy of a row that a mid-walk shift hands back again', async () => {
    const shifting = vi
      .fn()
      .mockResolvedValueOnce({ total: 4, items: [{ id: 1 }, { id: 2 }] })
      // A new grade landed ahead of the second page: row 2 slides onto it.
      .mockResolvedValueOnce({ total: 5, items: [{ id: 2 }, { id: 3 }] })
      .mockResolvedValueOnce({ total: 5, items: [{ id: 4 }] });
    const all = await collectPages(shifting, 2, byId);
    expect(all.map(byId)).toEqual([1, 2, 3, 4]);
  });
});

describe('fetchAllQualitySongs', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('pages the category at the server cap of 500', async () => {
    const row = (songId: number) => ({ songId, verdict: 'Wrong' }) as QualitySongRow;
    const fetchMock = vi.fn(async (url: string) => {
      const skip = Number(new URL(url, 'http://x').searchParams.get('skip'));
      const items = skip === 0 ? Array.from({ length: 500 }, (_, i) => row(i + 1)) : [row(501)];
      return new Response(JSON.stringify({ total: 501, skip, take: 500, items }));
    });
    vi.stubGlobal('fetch', fetchMock);

    const all = await fetchAllQualitySongs('wrong-or-questionable');

    expect(all).toHaveLength(501);
    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
      '/api/mh/api/quality/songs?category=wrong-or-questionable&skip=0&take=500',
      '/api/mh/api/quality/songs?category=wrong-or-questionable&skip=500&take=500'
    ]);
  });
});

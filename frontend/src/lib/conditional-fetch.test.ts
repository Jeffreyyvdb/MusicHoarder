import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  currentGrantors,
  fetchAlbumsIfChanged,
  fetchSongs,
  fetchSongsIfChanged,
  resetGrantors
} from '#lib/api-client.ts';

/**
 * The two library lists can be asked for conditionally: "send it only if it is not the copy I
 * hold". These pin what goes on the wire and what each answer turns into — in particular that a
 * 304 is only ever "keep what you have" for a caller that said it has something.
 */

const fetchMock = vi.fn();

const json = (body: unknown, init: ResponseInit = {}) =>
  new Response(JSON.stringify(body), {
    ...init,
    headers: { 'content-type': 'application/json', ...init.headers }
  });
const notModified = (etag: string) => new Response(null, { status: 304, headers: { etag } });

/** The headers of the request the client last made, lower-cased. */
function sentHeaders(): Record<string, string> {
  const init = fetchMock.mock.lastCall?.[1] as RequestInit | undefined;
  return Object.fromEntries(new Headers(init?.headers).entries());
}

beforeEach(() => {
  fetchMock.mockReset();
  vi.stubGlobal('fetch', fetchMock);
  resetGrantors();
});

afterEach(() => vi.unstubAllGlobals());

describe('fetchSongsIfChanged', () => {
  it('asks outright when no copy is held, and hands back the validator with the rows', async () => {
    fetchMock.mockResolvedValue(
      json({ songs: [{ id: 1 }], grantors: [] }, { headers: { etag: 'W/"s1"' } })
    );

    const answer = await fetchSongsIfChanged(null);

    expect(fetchMock.mock.lastCall?.[0]).toBe('/api/mh/songs?includeDeleted=false');
    expect(sentHeaders()).not.toHaveProperty('if-none-match');
    expect(answer).toEqual({ notModified: false, data: [{ id: 1 }], etag: 'W/"s1"' });
  });

  it('asks for the same list fetchSongs does, so a validator from one describes the other', async () => {
    fetchMock.mockImplementation(async () => json({ songs: [] }));
    await fetchSongs();
    const plain = fetchMock.mock.lastCall?.[0];
    await fetchSongsIfChanged(null);
    expect(fetchMock.mock.lastCall?.[0]).toBe(plain);
  });

  it('offers the validator, and never lets the browser answer from a cache of its own', async () => {
    fetchMock.mockResolvedValue(notModified('W/"s1"'));

    const answer = await fetchSongsIfChanged('W/"s1"');

    expect(sentHeaders()['if-none-match']).toBe('W/"s1"');
    // no-store is what makes the browser hand over the bare 304: the rows live in the store only.
    expect((fetchMock.mock.lastCall?.[1] as RequestInit).cache).toBe('no-store');
    expect(answer).toEqual({ notModified: true, etag: 'W/"s1"' });
  });

  it('leaves the grantors alone on "unchanged", and replaces them with new rows', async () => {
    const shared = [{ userId: 'u1', displayName: 'Nina', songCount: 2 }];
    fetchMock.mockResolvedValueOnce(
      json({ songs: [{ id: 1 }], grantors: shared }, { headers: { etag: 'W/"s1"' } })
    );
    await fetchSongsIfChanged(null);
    expect(currentGrantors()).toEqual(shared);

    fetchMock.mockResolvedValueOnce(notModified('W/"s1"'));
    await fetchSongsIfChanged('W/"s1"');
    expect(currentGrantors()).toEqual(shared);

    // The grant was revoked: the list comes back whole, without them.
    fetchMock.mockResolvedValueOnce(
      json({ songs: [], grantors: [] }, { headers: { etag: 'W/"s2"' } })
    );
    const answer = await fetchSongsIfChanged('W/"s1"');
    expect(answer).toEqual({ notModified: false, data: [], etag: 'W/"s2"' });
    expect(currentGrantors()).toEqual([]);
  });

  it('reports no validator when the server sent none', async () => {
    fetchMock.mockResolvedValue(json({ songs: [{ id: 1 }] }));
    expect(await fetchSongsIfChanged(null)).toEqual({
      notModified: false,
      data: [{ id: 1 }],
      etag: null
    });
  });

  it('refuses a 304 to a request that named no copy', async () => {
    // There would be nothing to keep. It fails like any other bad answer, so the caller shows an
    // error and tries again, rather than treating an empty store as a loaded library.
    fetchMock.mockResolvedValue(notModified('W/"s1"'));

    const failure = await fetchSongsIfChanged(null).catch((err: unknown) => err);

    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).status).toBe(304);
  });

  it('fails like any other request when the server says no', async () => {
    fetchMock.mockResolvedValue(json({ error: 'unauthenticated' }, { status: 401 }));

    const failure = await fetchSongsIfChanged('W/"s1"').catch((err: unknown) => err);

    expect(failure).toBeInstanceOf(ApiError);
    expect((failure as ApiError).status).toBe(401);
    expect((failure as ApiError).code).toBe('unauthenticated');
  });
});

describe('fetchAlbumsIfChanged', () => {
  it('builds the same request per query, with the validator alongside', async () => {
    fetchMock.mockImplementation(async () =>
      json({ albums: [{ key: 'a' }] }, { headers: { etag: 'W/"a1"' } })
    );

    const grid = await fetchAlbumsIfChanged({}, null);
    expect(fetchMock.mock.lastCall?.[0]).toBe('/api/mh/api/albums');
    expect(grid).toEqual({ notModified: false, data: [{ key: 'a' }], etag: 'W/"a1"' });

    await fetchAlbumsIfChanged({ builtOnly: false, merge: false }, 'W/"d1"');
    expect(fetchMock.mock.lastCall?.[0]).toBe('/api/mh/api/albums?builtOnly=false&merge=false');
    expect(sentHeaders()['if-none-match']).toBe('W/"d1"');
  });

  it('passes "unchanged" through for the cards it was asked about', async () => {
    fetchMock.mockResolvedValue(notModified('W/"a1"'));
    expect(await fetchAlbumsIfChanged({}, 'W/"a1"')).toEqual({ notModified: true, etag: 'W/"a1"' });
  });
});

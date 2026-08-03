import { afterEach, describe, expect, it, vi } from 'vitest';
import { probeSession } from './session';

vi.mock('$lib/server/api-target', () => ({
  getApiBaseUrl: () => 'http://api.test'
}));

function stubFetch(impl: () => Promise<Response>) {
  vi.stubGlobal('fetch', vi.fn(impl));
  return globalThis.fetch as unknown as ReturnType<typeof vi.fn>;
}

const OWNER = {
  id: 'owner-id',
  email: 'owner@example.com',
  role: 'Owner' as const,
  displayName: 'Owner'
};

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('probeSession', () => {
  it('is anonymous without calling the API when no cookie header is present', async () => {
    const fetchMock = stubFetch(async () => new Response(null, { status: 500 }));

    await expect(probeSession(null)).resolves.toEqual({ status: 'anonymous' });
    await expect(probeSession('')).resolves.toEqual({ status: 'anonymous' });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('is anonymous when the header carries other cookies only', async () => {
    const fetchMock = stubFetch(async () => new Response(null, { status: 200 }));

    await expect(probeSession('theme=dark; other_mh_session=x')).resolves.toEqual({
      status: 'anonymous'
    });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('forwards the cookie header and returns the user on 200', async () => {
    const fetchMock = stubFetch(async () => Response.json(OWNER));

    await expect(probeSession('theme=dark; mh_session=abc')).resolves.toEqual({
      status: 'authenticated',
      user: OWNER
    });

    const [url, init] = fetchMock.mock.calls[0] as [string, RequestInit];
    expect(url).toBe('http://api.test/api/auth/me');
    expect((init.headers as Record<string, string>).cookie).toBe('theme=dark; mh_session=abc');
  });

  it('treats 401 as genuinely signed out', async () => {
    stubFetch(async () => new Response(null, { status: 401 }));

    await expect(probeSession('mh_session=abc')).resolves.toEqual({ status: 'anonymous' });
  });

  // The regression this whole change is about: a broken API must never look like a signed-out
  // user, or the caller destroys a perfectly valid session.
  it('reports unavailable — not anonymous — when the API errors', async () => {
    stubFetch(async () => new Response(null, { status: 502 }));

    await expect(probeSession('mh_session=abc')).resolves.toEqual({ status: 'unavailable' });
  });

  it('reports unavailable when the request throws or times out', async () => {
    stubFetch(async () => {
      throw new Error('timed out');
    });

    await expect(probeSession('mh_session=abc')).resolves.toEqual({ status: 'unavailable' });
  });

  it('reports unavailable when the body is not the expected JSON', async () => {
    stubFetch(async () => new Response('<html>gateway</html>', { status: 200 }));

    await expect(probeSession('mh_session=abc')).resolves.toEqual({ status: 'unavailable' });
  });
});

import { describe, expect, it, vi } from 'vitest';
import type { RequestEvent } from '@sveltejs/kit';
import { handle } from './hooks.server';

const SITE = 'https://musichoarder.app';

/** Minimal stand-in for the parts of `RequestEvent` the hook actually reads. */
function makeEvent(path: string, accept?: string | null): RequestEvent {
  const headers = new Headers();
  if (accept) headers.set('accept', accept);
  return {
    url: new URL(`${SITE}${path}`),
    request: new Request(`${SITE}${path}`, { headers })
  } as unknown as RequestEvent;
}

function htmlResponse(status = 200, headers: Record<string, string> = {}) {
  return new Response('<!doctype html><title>page</title>', {
    status,
    headers: { 'Content-Type': 'text/html', ...headers }
  });
}

async function run(
  path: string,
  accept?: string | null,
  resolved: Response = htmlResponse()
): Promise<{ response: Response; resolve: ReturnType<typeof vi.fn> }> {
  const resolve = vi.fn(async () => resolved);
  const response = await handle({
    event: makeEvent(path, accept),
    // eslint-disable-next-line @typescript-eslint/no-explicit-any
    resolve: resolve as any
  });
  return { response, resolve };
}

describe('markdown content negotiation', () => {
  it('serves markdown from the canonical URL when the agent asks for it', async () => {
    const { response, resolve } = await run('/', 'text/markdown');

    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toBe('text/markdown; charset=utf-8');
    expect(response.headers.get('vary')).toBe('Accept');
    await expect(response.text()).resolves.toContain('# MusicHoarder');
    // Short-circuited: SvelteKit never rendered the page.
    expect(resolve).not.toHaveBeenCalled();
  });

  it.each(['/about', '/contact', '/privacy'])('serves markdown for %s', async (path) => {
    const { response } = await run(path, 'text/markdown');
    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toBe('text/markdown; charset=utf-8');
    await expect(response.text()).resolves.toContain('# ');
  });

  it('serves markdown from the .md sibling whatever the Accept header says', async () => {
    const { response, resolve } = await run('/about.md', 'text/html');

    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toBe('text/markdown; charset=utf-8');
    expect(response.headers.get('vary')).toBe('Accept');
    expect(resolve).not.toHaveBeenCalled();
  });

  it('keeps HTML for browsers, and advertises the markdown sibling', async () => {
    const { response } = await run(
      '/about',
      'text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8'
    );

    expect(response.status).toBe(200);
    expect(response.headers.get('content-type')).toBe('text/html');
    expect(response.headers.get('vary')).toBe('Accept');
    expect(response.headers.get('link')).toBe('</about.md>; rel="alternate"; type="text/markdown"');
  });

  it('appends to the preload Link header SvelteKit already set instead of replacing it', async () => {
    const { response } = await run(
      '/',
      'text/html',
      htmlResponse(200, { Link: '<x.css>; rel="preload"' })
    );

    expect(response.headers.get('link')).toBe(
      '<x.css>; rel="preload", </index.md>; rel="alternate"; type="text/markdown"'
    );
  });

  it('honours q-values when picking a representation', async () => {
    const markdownWins = await run('/about', 'text/html;q=0.3, text/markdown;q=0.9');
    expect(markdownWins.response.headers.get('content-type')).toBe('text/markdown; charset=utf-8');

    const htmlWins = await run('/about', 'text/html;q=0.9, text/markdown;q=0.3');
    expect(htmlWins.response.headers.get('content-type')).toBe('text/html');
  });

  it('answers 406 when the client accepts neither representation', async () => {
    const { response, resolve } = await run('/about', 'application/pdf');

    expect(response.status).toBe(406);
    expect(response.headers.get('vary')).toBe('Accept');
    expect(resolve).not.toHaveBeenCalled();
  });

  it('leaves the API proxy alone — no negotiation, no 406, no Vary', async () => {
    const json = new Response('{"error":"not_found"}', {
      status: 404,
      headers: { 'Content-Type': 'application/json' }
    });
    const { response } = await run('/api/mh/songs/unknown', 'application/json', json);

    expect(response.status).toBe(404);
    expect(response.headers.get('content-type')).toBe('application/json');
    expect(response.headers.get('vary')).toBeNull();
    await expect(response.text()).resolves.toBe('{"error":"not_found"}');
  });

  it('does not negotiate on application routes that have no markdown twin', async () => {
    const { response, resolve } = await run('/library', 'application/pdf');

    expect(response.status).toBe(200);
    expect(resolve).toHaveBeenCalled();
  });
});

describe('agent-friendly 404', () => {
  it('returns a real 404 with a markdown map for a client that sends a bare wildcard', async () => {
    const { response } = await run('/no-such-page', '*/*', htmlResponse(404));

    expect(response.status).toBe(404);
    expect(response.headers.get('content-type')).toBe('text/markdown; charset=utf-8');
    expect(response.headers.get('vary')).toBe('Accept');

    const body = await response.text();
    expect(body).toContain('# 404');
    expect(body).toContain('/no-such-page');
    expect(body).toContain(`${SITE}/sitemap.xml`);
    expect(body).toContain(`${SITE}/llms.txt`);
  });

  it('returns the markdown map when no Accept header is sent at all', async () => {
    const { response } = await run('/no-such-page', null, htmlResponse(404));
    expect(response.status).toBe(404);
    expect(response.headers.get('content-type')).toBe('text/markdown; charset=utf-8');
  });

  it('keeps the styled error page for a browser', async () => {
    const { response } = await run(
      '/no-such-page',
      'text/html,application/xhtml+xml,*/*;q=0.8',
      htmlResponse(404)
    );

    expect(response.status).toBe(404);
    expect(response.headers.get('content-type')).toBe('text/html');
    await expect(response.text()).resolves.toContain('<!doctype html>');
  });

  it('never rewrites a non-HTML 404 produced by a route handler', async () => {
    const json = new Response('null', {
      status: 404,
      headers: { 'Content-Type': 'application/json' }
    });
    const { response } = await run('/some/endpoint', '*/*', json);

    expect(response.headers.get('content-type')).toBe('application/json');
    await expect(response.text()).resolves.toBe('null');
  });

  it('404s an unknown .md path instead of inventing a markdown body', async () => {
    const { response } = await run('/nope.md', '*/*', htmlResponse(404));

    expect(response.status).toBe(404);
    await expect(response.text()).resolves.toContain('# 404');
  });
});

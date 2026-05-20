import type { RequestHandler } from './$types';
import { getUmamiOrigin } from '$lib/server/api-target';

// Headers we forward upstream. Kept to a minimal allowlist so we don't leak the
// site's own cookies / auth headers to the analytics host. x-forwarded-for and
// user-agent are load-bearing: without them Umami attributes every event to this
// server's IP, breaking unique-visitor counts and geo.
const FORWARDED_REQUEST_HEADERS = ['content-type', 'user-agent', 'accept-language'];

async function proxy(
  request: Request,
  origin: string,
  pathSegments: string,
  search: string,
  clientAddress: string
): Promise<Response> {
  const base = origin.replace(/\/$/, '');
  const target = `${base}/${pathSegments}${search}`;
  const method = request.method.toUpperCase();
  const shouldForwardBody = method !== 'GET' && method !== 'HEAD';

  const headers = new Headers();
  for (const name of FORWARDED_REQUEST_HEADERS) {
    const value = request.headers.get(name);
    if (value) headers.set(name, value);
  }
  headers.set('x-forwarded-for', request.headers.get('x-forwarded-for') ?? clientAddress);

  const response = await fetch(target, {
    method,
    headers,
    body: shouldForwardBody ? await request.arrayBuffer() : undefined,
    cache: 'no-store',
    redirect: 'follow'
  });

  const responseHeaders = new Headers();
  const contentType = response.headers.get('content-type');
  if (contentType) responseHeaders.set('content-type', contentType);
  const cacheControl = response.headers.get('cache-control');
  if (cacheControl) responseHeaders.set('cache-control', cacheControl);

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers: responseHeaders
  });
}

const handler: RequestHandler = ({ request, params, url, getClientAddress }) => {
  const origin = getUmamiOrigin();
  if (!origin) {
    return new Response('Umami proxy not configured', { status: 503 });
  }
  return proxy(request, origin, params.path ?? '', url.search, getClientAddress());
};

export const GET = handler;
export const POST = handler;

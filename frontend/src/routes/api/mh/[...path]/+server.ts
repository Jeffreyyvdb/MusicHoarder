import type { RequestHandler } from './$types';
import { getApiBaseUrl } from '$lib/server/api-target';

function buildTargetUrl(pathSegments: string, search: string): string {
  const base = getApiBaseUrl().replace(/\/$/, '');
  return `${base}/${pathSegments}${search}`;
}

async function proxy(request: Request, pathSegments: string, search: string): Promise<Response> {
  const target = buildTargetUrl(pathSegments, search);
  const method = request.method.toUpperCase();
  const shouldForwardBody = method !== 'GET' && method !== 'HEAD';

  const headers = new Headers(request.headers);
  headers.delete('host');

  // Bound the time-to-headers, not the body stream: abort if the API hasn't returned
  // response headers within the window, then clear the timer the moment it does. This keeps
  // the long-lived SSE progress feed (/api/enrichment/progress) streaming after headers
  // arrive, while preventing a busy API from making the proxy pend indefinitely.
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 10_000);

  let response: Response;
  try {
    response = await fetch(target, {
      method,
      headers,
      body: shouldForwardBody ? await request.arrayBuffer() : undefined,
      cache: 'no-store',
      redirect: 'follow',
      signal: controller.signal
    });
  } catch {
    return new Response(null, { status: 504 });
  } finally {
    clearTimeout(timeout);
  }

  const responseHeaders = new Headers(response.headers);
  responseHeaders.delete('content-encoding');
  responseHeaders.delete('content-length');
  // Hop-by-hop / connection-specific headers — forbidden under HTTP/2, which the
  // dev server now uses since Aspire serves the frontend over HTTPS.
  responseHeaders.delete('transfer-encoding');
  responseHeaders.delete('connection');
  responseHeaders.delete('keep-alive');
  responseHeaders.delete('proxy-connection');
  responseHeaders.delete('upgrade');

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers: responseHeaders
  });
}

const handler: RequestHandler = ({ request, params, url }) => {
  return proxy(request, params.path ?? '', url.search);
};

export const GET = handler;
export const POST = handler;
export const PUT = handler;
export const PATCH = handler;
export const DELETE = handler;

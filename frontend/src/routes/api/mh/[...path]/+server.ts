import type { IncomingMessage } from 'node:http';
import type { RequestHandler } from './$types';
import { getApiBaseUrl } from '$lib/server/api-target';

function buildTargetUrl(pathSegments: string, search: string): string {
  const base = getApiBaseUrl().replace(/\/$/, '');
  return `${base}/${pathSegments}${search}`;
}

async function proxy(
  request: Request,
  pathSegments: string,
  search: string,
  platform: App.Platform | undefined
): Promise<Response> {
  const target = buildTargetUrl(pathSegments, search);
  const method = request.method.toUpperCase();
  const shouldForwardBody = method !== 'GET' && method !== 'HEAD';

  const headers = new Headers(request.headers);
  headers.delete('host');

  // Bound the time-to-headers, not the body stream: abort if the API hasn't returned
  // response headers within the window, then clear the timer the moment it does. This keeps
  // the long-lived SSE progress feed (/api/enrichment/progress) streaming after headers
  // arrive, while preventing a busy API from making the proxy pend indefinitely.
  //
  // AI lyrics transcription and pronunciation/translation are deliberate long-running synchronous
  // actions (ffmpeg + Whisper, or chunked LLM calls), so they get a much longer window — each is
  // bounded server-side by its own TimeoutSeconds. Aborting early would also cancel the API-side work.
  // So does a stream's AAC rendition (`?format=aac`): its headers wait until ffmpeg has converted
  // the whole file, bounded by StreamTranscode:TimeoutSeconds.
  const isConvertedStream =
    pathSegments.endsWith('/stream') && new URLSearchParams(search).has('format');
  const isLongRunning =
    pathSegments.endsWith('/lyrics/transcribe') ||
    pathSegments.endsWith('/lyrics/translate') ||
    isConvertedStream;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), isLongRunning ? 240_000 : 10_000);

  // A converted stream the listener skipped past should stop its conversion, which the API does
  // once nobody waits for it any more. SvelteKit's `request.signal` never fires for a GET, so under
  // adapter-node (which hands the raw request over as `platform.req`) watch the connection: a
  // client that gives up on an in-flight HTTP/1.1 request closes it. Elsewhere (the dev server)
  // the conversion simply runs to the end and is cached.
  const socket = isConvertedStream
    ? (platform as { req?: IncomingMessage } | undefined)?.req?.socket
    : undefined;
  const onClientGone = () => controller.abort();
  socket?.once('close', onClientGone);

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
    socket?.off('close', onClientGone);
  }

  const responseHeaders = new Headers(response.headers);
  // `fetch` has already decoded a compressed body, so both headers describe bytes the client will
  // never see. When the API sent no encoding at all, the body is passed through untouched and
  // `content-length` is exactly right — and worth keeping: it is how a progressive media player
  // learns a stream's size, and without it ExoPlayer cannot derive a seek position in a constant
  // bitrate MP3 (nor a duration), which left the Android player's scrubber unable to move.
  if (response.headers.has('content-encoding')) {
    responseHeaders.delete('content-encoding');
    responseHeaders.delete('content-length');
  }
  // Hop-by-hop / connection-specific headers — forbidden under HTTP/2, which the
  // dev server now uses since Aspire serves the frontend over HTTPS.
  responseHeaders.delete('transfer-encoding');
  responseHeaders.delete('connection');
  responseHeaders.delete('keep-alive');
  responseHeaders.delete('proxy-connection');
  responseHeaders.delete('upgrade');
  // Multi-account sign-in can set two cookies on one response (mh_session + mh_session_alts).
  // Re-append them individually via getSetCookie() — a `new Headers(...)` copy may fold
  // multiple set-cookie values into one comma-joined header, which browsers misparse.
  const setCookies = response.headers.getSetCookie?.() ?? [];
  if (setCookies.length > 0) {
    responseHeaders.delete('set-cookie');
    for (const value of setCookies) responseHeaders.append('set-cookie', value);
  }

  return new Response(response.body, {
    status: response.status,
    statusText: response.statusText,
    headers: responseHeaders
  });
}

const handler: RequestHandler = ({ request, params, url, platform }) => {
  return proxy(request, params.path ?? '', url.search, platform);
};

export const GET = handler;
export const POST = handler;
export const PUT = handler;
export const PATCH = handler;
export const DELETE = handler;

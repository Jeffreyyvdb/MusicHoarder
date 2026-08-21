import type { Handle } from '@sveltejs/kit';
import {
  appendVaryAccept,
  explicitlyAcceptsHtml,
  preferredType
} from '$lib/server/content-negotiation';
import { resolveSiteUrl } from '$lib/server/site-url';
import {
  canonicalizePath,
  isMarkdownUrl,
  markdownFor,
  markdownSiblingPath,
  notFoundMarkdown
} from '$lib/content/site-markdown';

const MARKDOWN_TYPE = 'text/markdown; charset=utf-8';

/** The representations a negotiable URL can produce, best-default first. */
const PRODUCES = ['text/html', 'text/markdown'];

/**
 * Markdown content negotiation for agents (acceptmarkdown.com) plus an agent-recoverable 404.
 *
 * Two rules keep this from touching the application:
 *
 *  1. Strict negotiation — including the `406` — only runs for paths that actually have a Markdown
 *     representation (`markdownFor` returns non-null). `/api/mh/*` asks for `application/json`, and
 *     a `406` there would break the whole app; those requests fall straight through untouched.
 *  2. The Markdown 404 body only replaces SvelteKit's own HTML page-404, never a route handler's
 *     JSON 404 (the API proxy relays upstream 404s that the frontend parses).
 */
export const handle: Handle = async ({ event, resolve }) => {
  const siteUrl = resolveSiteUrl();
  const pathname = event.url.pathname;
  const accept = event.request.headers.get('accept');
  const markdown = markdownFor(pathname, siteUrl);

  // Explicit `.md` URL: always Markdown, whatever the Accept header says. This is what
  // `Link: rel="alternate"` points at, and crawlers following it may send no Accept at all.
  if (isMarkdownUrl(pathname)) {
    if (markdown) return markdownResponse(markdown);
    return agentFriendly404(await resolve(event), siteUrl, pathname, accept);
  }

  if (markdown) {
    const chosen = preferredType(accept, PRODUCES);

    // The client accepts neither representation this resource can produce.
    if (chosen === null) return notAcceptable();
    if (chosen === 'text/markdown') return markdownResponse(markdown);
  }

  const response = await resolve(event);

  if (response.status === 404) return agentFriendly404(response, siteUrl, pathname, accept);

  // HTML variant of a negotiable URL: advertise the Markdown sibling and vary on Accept, so a CDN
  // cannot hand a cached HTML variant to an agent that asked for Markdown.
  if (markdown) {
    appendVaryAccept(response.headers);
    const canonical = canonicalizePath(pathname);
    const link = `<${markdownSiblingPath(canonical)}>; rel="alternate"; type="text/markdown"`;
    const existing = response.headers.get('link');
    response.headers.set('Link', existing ? `${existing}, ${link}` : link);
  }

  return response;
};

function markdownResponse(body: string, status = 200): Response {
  const response = new Response(body, { status, headers: { 'Content-Type': MARKDOWN_TYPE } });
  appendVaryAccept(response.headers);
  return response;
}

function notAcceptable(): Response {
  const response = new Response(
    'Not Acceptable\n\nThis URL can produce text/html or text/markdown.\n',
    { status: 406, headers: { 'Content-Type': 'text/plain; charset=utf-8' } }
  );
  appendVaryAccept(response.headers);
  return response;
}

/**
 * A 404 an agent can act on: the same real 404 status, with a short Markdown map of the site.
 *
 * Browsers name `text/html` explicitly and keep the styled error page; clients that send a bare
 * wildcard (curl, most agent fetch tools) or ask for Markdown get the map instead. A non-HTML 404
 * from a route handler is passed through untouched.
 */
function agentFriendly404(
  response: Response,
  siteUrl: string,
  pathname: string,
  accept: string | null
): Response {
  const isPageHtml = response.headers.get('content-type')?.includes('text/html') ?? false;
  if (!isPageHtml) return response;

  if (explicitlyAcceptsHtml(accept)) {
    appendVaryAccept(response.headers);
    return response;
  }

  return markdownResponse(notFoundMarkdown(siteUrl, pathname), 404);
}

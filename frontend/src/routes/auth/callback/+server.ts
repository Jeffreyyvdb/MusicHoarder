import { error } from '@sveltejs/kit';
import { getApiBaseUrl } from '$lib/server/api-target';
import { APP_HOME } from '$lib/app-home';
import type { RequestHandler } from './$types';

/**
 * Magic-link click handler. The email contains a link to this route on the frontend; we POST
 * the token to the API, mirror the resulting Set-Cookie onto the browser, and 303 the user
 * into the app.
 *
 * Why this isn't done via the /api/mh proxy: the proxy uses `redirect: 'follow'` which would
 * swallow any Set-Cookie issued during an intermediate hop. Doing the consume here keeps the
 * cookie write on the same response that lands in the browser.
 *
 * When the link was requested by the native app (`client=app`), the token must NOT be consumed
 * here: the app exchanges it for a bearer token at /api/auth/token, and the token is single-use.
 * Instead a small handoff page offers the `musichoarder://auth` deep link into the app, with a
 * plain browser sign-in (the consume path, minus `client`) as the fallback.
 */
export const GET: RequestHandler = async ({ url, fetch, request }) => {
  const token = url.searchParams.get('token');
  if (!token) throw error(400, 'Missing token.');

  if (url.searchParams.get('client') === 'app') {
    // Prefer the origin the API baked into the link — the page's own origin can be wrong
    // behind a proxy — but never trust `url=` blindly beyond it being a URL: the deep link
    // tells the app where to send this token.
    const appBase = url.searchParams.get('url') || url.origin;
    return new Response(appHandoffPage(token, appBase), {
      headers: { 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' }
    });
  }

  const apiBase = getApiBaseUrl().replace(/\/$/, '');
  const response = await fetch(`${apiBase}/api/auth/consume`, {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      // Forward the browser's cookies so a still-signed-in session gets parked by the account
      // switcher instead of silently discarded.
      cookie: request.headers.get('cookie') ?? ''
    },
    body: JSON.stringify({ token })
  });

  if (response.status !== 200) {
    const message = response.status === 400 ? 'Invalid or expired link.' : 'Sign-in failed.';
    return new Response(null, {
      status: 303,
      headers: { Location: `/login?error=${encodeURIComponent(message)}` }
    });
  }

  // Forward the API's Set-Cookie header(s) so the cookie lands on the user's browser.
  const headers = new Headers({ Location: APP_HOME });
  for (const value of response.headers.getSetCookie?.() ?? []) {
    headers.append('set-cookie', value);
  }
  return new Response(null, { status: 303, headers });
};

function appHandoffPage(token: string, appBase: string): string {
  const deepLink = `musichoarder://auth?token=${encodeURIComponent(token)}&url=${encodeURIComponent(appBase)}`;
  const browserHref = `/auth/callback?token=${encodeURIComponent(token)}`;
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<meta name="robots" content="noindex" />
<title>Sign in to MusicHoarder</title>
<style>
  :root { color-scheme: dark; }
  body {
    margin: 0; min-height: 100dvh; display: grid; place-items: center;
    background: oklch(0.15 0.005 260); color: oklch(0.95 0.005 260);
    font-family: system-ui, -apple-system, sans-serif; text-align: center;
  }
  main { padding: 32px 24px; max-width: 380px; }
  h1 { font-size: 1.25rem; margin: 0 0 8px; }
  p { color: oklch(0.7 0.01 260); font-size: 0.9rem; line-height: 1.5; margin: 0 0 24px; }
  .open {
    display: block; padding: 14px 20px; border-radius: 999px; text-decoration: none;
    background: oklch(0.65 0.2 145); color: oklch(0.13 0.005 260); font-weight: 600;
  }
  .fallback { display: inline-block; margin-top: 20px; font-size: 0.85rem; color: oklch(0.7 0.01 260); }
</style>
</head>
<body>
<main>
  <h1>Almost there</h1>
  <p>Finish signing in inside the MusicHoarder app on this phone.</p>
  <a class="open" href="${deepLink}">Open the MusicHoarder app</a>
  <a class="fallback" href="${browserHref}">Sign in in this browser instead</a>
</main>
</body>
</html>`;
}

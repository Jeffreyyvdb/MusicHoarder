import { error } from '@sveltejs/kit';
import { getApiBaseUrl } from '$lib/server/api-target';
import { APP_HOME } from '$lib/app-home';
import { isIosSafariUserAgent } from '$lib/ios-safari';
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
 *
 * Safari on iOS gets the same sign-in (same consume, same cookies) but a beat of explanation
 * instead of a bare 303: an email link always opens in Safari, and an iOS Home Screen app has its
 * own cookie jar, so someone who asked for the link from the installed app would otherwise land
 * signed in here and find the app still signed out, with no word why. The page moves on by itself.
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
    // A code, not a sentence — /login maps it to copy and ignores anything it doesn't know.
    const code = response.status === 400 ? 'link' : 'signin';
    return new Response(null, {
      status: 303,
      headers: { Location: `/login?error=${code}` }
    });
  }

  // Forward the API's Set-Cookie header(s) so the cookie lands on the user's browser. A 200 page
  // sets them exactly as the 303 does.
  const iosSafari = isIosSafariUserAgent(request.headers.get('user-agent'));
  const headers = iosSafari
    ? new Headers({ 'content-type': 'text/html; charset=utf-8', 'cache-control': 'no-store' })
    : new Headers({ Location: APP_HOME });
  for (const value of response.headers.getSetCookie?.() ?? []) {
    headers.append('set-cookie', value);
  }
  if (iosSafari) return new Response(iosSignedInPage(APP_HOME), { status: 200, headers });
  return new Response(null, { status: 303, headers });
};

// Shared by both handoff pages so they read as one product: centred, one pill action. The colours
// are hex copies of the app.css tokens (--background, --foreground, --muted-foreground, --primary,
// --primary-foreground) in both appearances, following the system scheme — these pages run before
// the app, so they cannot know a theme it pinned.
const HANDOFF_STYLE = `
  :root {
    color-scheme: light dark;
    --bg: #ffffff; --fg: #000000; --muted: #5f5f64; --tint: #006b1f; --on-tint: #ffffff;
  }
  @media (prefers-color-scheme: dark) {
    :root { --bg: #000000; --fg: #ffffff; --muted: #a1a1a6; --tint: #2fbd55; --on-tint: #000000; }
  }
  body {
    margin: 0; min-height: 100dvh; display: grid; place-items: center;
    background: var(--bg); color: var(--fg);
    font-family: system-ui, -apple-system, sans-serif; text-align: center;
  }
  main { padding: 32px 24px; max-width: 380px; }
  h1 { font-size: 1.25rem; margin: 0 0 8px; }
  p { color: var(--muted); font-size: 0.9rem; line-height: 1.5; margin: 0 0 24px; }
  .open {
    display: block; padding: 14px 20px; border-radius: 999px; text-decoration: none;
    background: var(--tint); color: var(--on-tint); font-weight: 600;
  }
  .fallback { display: inline-block; margin-top: 20px; font-size: 0.85rem; color: var(--tint); }
`;

function iosSignedInPage(next: string): string {
  // `next` is APP_HOME, a constant path — never request input — so it is safe in the markup and
  // the script. location.replace keeps this spent-token URL out of the back stack, as the 303 did;
  // the meta refresh is the no-script fallback.
  return `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<meta name="robots" content="noindex" />
<meta http-equiv="refresh" content="3;url=${next}" />
<title>Signed in · MusicHoarder</title>
<style>${HANDOFF_STYLE}</style>
</head>
<body>
<main>
  <h1>You're signed in</h1>
  <p>The MusicHoarder app on your Home Screen signs in separately — if you use it, sign in there too. A passkey is quickest.</p>
  <a class="open" href="${next}">Continue</a>
</main>
<script>setTimeout(function () { location.replace(${JSON.stringify(next)}); }, 1500);</script>
</body>
</html>`;
}

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
<style>${HANDOFF_STYLE}</style>
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

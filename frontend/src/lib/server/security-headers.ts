/**
 * Response security headers for every response the frontend serves (pages, the `/api/mh` proxy,
 * route handlers). Static files under `static/` are served by adapter-node before the hook runs,
 * so they do not get these; nothing there is HTML except `offline.html`, which the service worker
 * serves from cache.
 *
 * The Content-Security-Policy itself is report-only and lives in `vite.config.ts` (`kit.csp`), so
 * SvelteKit can put a per-request nonce on its own inline scripts. The two things it cannot know at
 * build time are added here: the Umami origins (runtime env), and the `Reporting-Endpoints` header
 * that its `report-to` directive names.
 */

/** Where report-only CSP violations are sent (`src/routes/api/csp-report/+server.ts`). */
export const CSP_REPORT_PATH = '/api/csp-report';
/** The `report-to` group name, shared with `vite.config.ts`. */
export const CSP_REPORT_GROUP = 'csp-endpoint';

/**
 * Starts short on purpose: a long HSTS max-age is hard to undo if the site ever has to serve plain
 * HTTP. Raise it to a year (31536000) once this has run in production for a while.
 */
const HSTS = 'max-age=300';

// `tools` (WebMCP, `#lib/webmcp`) is left out on purpose: its default allowlist is 'self', which is
// exactly what the app needs — its own pages register tools, and no frame may.
const PERMISSIONS_POLICY = [
  'camera=()',
  'microphone=()',
  'geolocation=()',
  'payment=()',
  'usb=()',
  'serial=()',
  'bluetooth=()',
  'hid=()',
  'midi=()',
  'browsing-topics=()'
].join(', ');

export type SecurityHeaderContext = {
  /** The scheme the browser used: true behind a TLS-terminating proxy too. */
  https: boolean;
  hostname: string;
  /** Origins of the Umami tracker and recorder scripts, when configured. */
  analyticsOrigins: string[];
  /**
   * Chrome origin-trial tokens (`WEBMCP_ORIGIN_TRIAL_TOKEN`), sent as `Origin-Trial`. A token is
   * valid for one origin only, so each instance registers its own; none means no header.
   */
  originTrials?: readonly string[];
};

/** The origin of an absolute URL, or null for an empty, relative or malformed one. */
export function originOf(url: string | undefined | null): string | null {
  if (!url) return null;
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'https:' || parsed.protocol === 'http:' ? parsed.origin : null;
  } catch {
    return null;
  }
}

/** True when the request reached the proxy in front of us over HTTPS. */
export function requestIsHttps(url: URL, forwardedProto: string | null): boolean {
  if (url.protocol === 'https:') return true;
  return forwardedProto?.split(',')[0]?.trim().toLowerCase() === 'https';
}

/** Adds `extra` sources to one directive of a serialized CSP, if that directive is present. */
export function addSources(policy: string, directive: string, extra: string[]): string {
  if (extra.length === 0) return policy;
  return policy
    .split(';')
    .map((part) => {
      const tokens = part.trim().split(/\s+/);
      if (tokens[0] !== directive) return part;
      const missing = extra.filter((source) => !tokens.includes(source));
      return missing.length ? `${part.trimEnd()} ${missing.join(' ')}` : part;
    })
    .join(';');
}

/** Sets the security headers on `headers` in place. Headers a handler set itself are kept. */
export function applySecurityHeaders(headers: Headers, context: SecurityHeaderContext): void {
  const setDefault = (name: string, value: string) => {
    if (!headers.has(name)) headers.set(name, value);
  };

  setDefault('X-Content-Type-Options', 'nosniff');
  setDefault('Referrer-Policy', 'strict-origin-when-cross-origin');
  setDefault('X-Frame-Options', 'DENY');
  setDefault('Cross-Origin-Opener-Policy', 'same-origin-allow-popups');
  setDefault('Permissions-Policy', PERMISSIONS_POLICY);

  // frame-ancestors is ignored in a report-only policy, so it is enforced on its own. The browser
  // enforces every Content-Security-Policy header it gets, so a later enforced kit.csp still works.
  if (!headers.has('Content-Security-Policy')) {
    headers.set('Content-Security-Policy', "frame-ancestors 'none'");
  }

  if (context.originTrials?.length) setDefault('Origin-Trial', context.originTrials.join(', '));

  const isLocal = context.hostname === 'localhost' || context.hostname === '127.0.0.1';
  if (context.https && !isLocal) setDefault('Strict-Transport-Security', HSTS);

  const reportOnly = headers.get('Content-Security-Policy-Report-Only');
  if (reportOnly) {
    let policy = addSources(reportOnly, 'script-src', context.analyticsOrigins);
    policy = addSources(policy, 'connect-src', context.analyticsOrigins);
    headers.set('Content-Security-Policy-Report-Only', policy);
    setDefault('Reporting-Endpoints', `${CSP_REPORT_GROUP}="${CSP_REPORT_PATH}"`);
  }
}

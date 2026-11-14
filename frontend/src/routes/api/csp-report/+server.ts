import type { RequestHandler } from './$types';
import { createLogBudget, parseCspReports, readBodyCapped } from '#lib/server/csp-report.ts';

/** Report bodies are small; anything bigger is not a CSP report. */
const MAX_BODY_BYTES = 64 * 1024;
const withinBudget = createLogBudget(60);

/**
 * Receives the report-only CSP's violation reports (see `csp` in vite.config.ts) and logs one
 * redacted line per violation. Always answers 204: a browser does not retry, and an error here
 * must never reach a page.
 */
export const POST: RequestHandler = async ({ request }) => {
  try {
    // Read as a stream and stop at the cap: a chunked body has no Content-Length to check first,
    // and this route is public.
    const text = await readBodyCapped(request, MAX_BODY_BYTES);
    if (text === null) return new Response(null, { status: 204 });
    for (const violation of parseCspReports(JSON.parse(text))) {
      if (!withinBudget()) break;
      console.warn('csp-violation', JSON.stringify(violation));
    }
  } catch {
    // Malformed body: nothing to log.
  }
  return new Response(null, { status: 204 });
};

/**
 * Parses Content-Security-Policy violation reports into one short, privacy-safe line each.
 *
 * Browsers send two shapes: the legacy `application/csp-report` body (`{"csp-report": {...}}`,
 * from `report-uri`; Safari and Firefox) and the Reporting API's `application/reports+json` array
 * (from `report-to`; Chromium). Both are reduced to the same summary. URLs keep their origin and
 * path only — query strings can carry tokens — and long path segments (share tokens, ids) are
 * masked, so a report never puts a capability URL into the logs.
 */

export type CspViolation = {
  directive: string;
  blocked: string;
  page: string;
  disposition: string;
  sample: string;
};

const MAX_REPORTS_PER_BODY = 20;

/** Origin + path, with long segments masked; keywords like `inline` or `eval` pass through. */
export function redactUrl(value: unknown): string {
  if (typeof value !== 'string' || value === '') return '';
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return value.slice(0, 40);
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return url.protocol.replace(':', '');
  const path = url.pathname
    .split('/')
    .map((segment) => (segment.length >= 16 ? ':id' : segment))
    .join('/');
  return `${url.origin}${path}`;
}

function str(value: unknown): string {
  return typeof value === 'string' ? value : '';
}

function summarize(raw: Record<string, unknown>): CspViolation {
  return {
    directive: str(
      raw.effectiveDirective ?? raw['effective-directive'] ?? raw['violated-directive']
    ),
    blocked: redactUrl(raw.blockedURL ?? raw['blocked-uri']),
    page: redactUrl(raw.documentURL ?? raw['document-uri']),
    disposition: str(raw.disposition),
    sample: str(raw.sample ?? raw['script-sample']).slice(0, 40)
  };
}

/** Every CSP violation in a report body, in either shape. Anything else yields an empty list. */
export function parseCspReports(body: unknown): CspViolation[] {
  if (Array.isArray(body)) {
    return body
      .slice(0, MAX_REPORTS_PER_BODY)
      .filter(
        (entry): entry is { type: string; body: Record<string, unknown> } =>
          !!entry &&
          typeof entry === 'object' &&
          (entry as { type?: unknown }).type === 'csp-violation' &&
          typeof (entry as { body?: unknown }).body === 'object' &&
          (entry as { body?: unknown }).body !== null
      )
      .map((entry) => summarize(entry.body));
  }
  if (body && typeof body === 'object') {
    const legacy = (body as Record<string, unknown>)['csp-report'];
    if (legacy && typeof legacy === 'object') return [summarize(legacy as Record<string, unknown>)];
  }
  return [];
}

/** A fixed window counter: at most `limit` log lines per minute, so a bad page cannot flood logs. */
export function createLogBudget(limit: number, now: () => number = Date.now) {
  let windowStart = 0;
  let used = 0;
  return (): boolean => {
    const t = now();
    if (t - windowStart >= 60_000) {
      windowStart = t;
      used = 0;
    }
    if (used >= limit) return false;
    used += 1;
    return true;
  };
}

/**
 * The request body as text, or null when it is larger than `maxBytes`. Reads the stream and stops
 * (cancelling it) as soon as the cap is passed, so an oversized body — chunked, with no
 * Content-Length — is never held in memory whole.
 */
export async function readBodyCapped(request: Request, maxBytes: number): Promise<string | null> {
  const declared = Number(request.headers.get('content-length') ?? 0);
  if (declared > maxBytes) return null;
  if (!request.body) return '';
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maxBytes) {
      await reader.cancel();
      return null;
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  return new TextDecoder().decode(bytes);
}

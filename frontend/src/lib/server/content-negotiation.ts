/**
 * RFC 9110 §12.5.1 `Accept` parsing, used to serve a Markdown representation of the public pages to
 * agents while browsers keep getting HTML from the same URL (see acceptmarkdown.com).
 *
 * Deliberately dependency-free and pure: the hook is thin, the rules are unit-tested here.
 */
export interface AcceptEntry {
  type: string;
  q: number;
  /**
   * A full wildcard scores 0, a `text/*` range scores 1, an exact type scores 2. The most specific
   * entry wins a tie.
   */
  specificity: number;
}

export function parseAccept(header: string): AcceptEntry[] {
  return header
    .split(',')
    .map((raw) => {
      const parts = raw
        .trim()
        .split(';')
        .map((s) => s.trim());
      const type = (parts[0] ?? '').toLowerCase();
      if (!type) return null;

      let q = 1;
      for (const param of parts.slice(1)) {
        const [name, value] = param.split('=').map((s) => s.trim());
        if (name?.toLowerCase() === 'q') {
          const parsed = Number(value);
          if (!Number.isNaN(parsed)) q = Math.max(0, Math.min(1, parsed));
        }
      }
      return { type, q, specificity: specificityOf(type) };
    })
    .filter((entry): entry is AcceptEntry => entry !== null);
}

function specificityOf(type: string): number {
  if (type === '*/*') return 0;
  return type.endsWith('/*') ? 1 : 2;
}

function matches(entry: AcceptEntry, candidate: string): boolean {
  if (entry.type === '*/*') return true;
  if (entry.type.endsWith('/*')) return candidate.startsWith(entry.type.slice(0, -1));
  return entry.type === candidate;
}

/**
 * Picks the best representation the client will accept, or `null` when it will accept none of them
 * — which is the caller's cue to answer `406 Not Acceptable`.
 *
 * A missing `Accept` header means "anything", so the first candidate wins; that keeps HTML the
 * default for browsers, curl and crawlers that send nothing.
 */
export function preferredType(
  header: string | null | undefined,
  produces: string[]
): string | null {
  if (!header) return produces[0] ?? null;

  const entries = parseAccept(header);
  if (entries.length === 0) return produces[0] ?? null;

  let bestType: string | null = null;
  let bestQ = -1;
  let bestPos = Number.POSITIVE_INFINITY;

  for (const candidate of produces) {
    let matched: AcceptEntry | null = null;
    let matchedPos = Number.POSITIVE_INFINITY;

    for (let i = 0; i < entries.length; i++) {
      const entry = entries[i];
      if (!matches(entry, candidate)) continue;
      if (
        matched === null ||
        entry.specificity > matched.specificity ||
        (entry.specificity === matched.specificity && i < matchedPos)
      ) {
        matched = entry;
        matchedPos = i;
      }
    }

    if (!matched || matched.q <= 0) continue;
    if (matched.q > bestQ || (matched.q === bestQ && matchedPos < bestPos)) {
      bestQ = matched.q;
      bestPos = matchedPos;
      bestType = candidate;
    }
  }

  return bestType;
}

/**
 * True when the client named HTML (or `text/*`) explicitly and did not down-weight it to zero.
 *
 * This is the browser test, and it is deliberately stricter than `preferredType`: a bare wildcard
 * accept (curl, most agent fetch tools) does not count as asking for HTML, so those clients can be handed
 * a Markdown 404 while a real browser still gets the styled error page.
 */
export function explicitlyAcceptsHtml(header: string | null | undefined): boolean {
  if (!header) return false;
  return parseAccept(header).some(
    (entry) => entry.q > 0 && entry.specificity > 0 && matches(entry, 'text/html')
  );
}

/** Adds `Accept` to an existing `Vary` header without clobbering what is already there. */
export function appendVaryAccept(headers: Headers): void {
  const existing = headers.get('vary');
  if (!existing) {
    headers.set('Vary', 'Accept');
    return;
  }
  const tokens = existing.split(',').map((token) => token.trim().toLowerCase());
  if (tokens.includes('*') || tokens.includes('accept')) return;
  headers.set('Vary', `${existing}, Accept`);
}

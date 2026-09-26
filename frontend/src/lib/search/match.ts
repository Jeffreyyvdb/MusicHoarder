/**
 * What a search query means, for every list in the app.
 *
 * The whole app used to search by lowercasing a field and asking `includes(query)`. That is one
 * contiguous substring of one field, which is not how anybody types a song:
 *
 *   - “best friend with fall out” never matched “Best Friend (with Fall Out Boy)”, because the
 *     bracket sits between the words;
 *   - “juice wrld best friend” never matched anything, because the title and the artist are two
 *     different fields and the query spans both;
 *   - “beyonce” never matched “Beyoncé”.
 *
 * So a query is split into terms, and a row matches when **every** term is found in **any** of its
 * fields, over text folded to letters and digits. Punctuation, brackets, diacritics and term order
 * stop mattering; the terms themselves still have to all be there, so a query keeps narrowing the
 * list as you type.
 *
 * `scoreFields` adds the other half, for the places that show a handful of results rather than the
 * whole filtered list (the command palette): a relevance score, so "the eight results" are the
 * eight *best* ones instead of the first eight in library order.
 *
 * Android ports this file as `data/SearchMatch.kt`; the two are tested case for case.
 */

/**
 * Fold text to the form terms are compared in: lowercase letters and digits, single-spaced.
 *
 * Apostrophes are *removed* rather than spaced, so “Girl's” folds to `girls` and a query without
 * the apostrophe still matches; every other non-alphanumeric run becomes a space. Letters and
 * digits are matched by Unicode class, so non-Latin scripts survive the fold.
 */
export function normalizeForSearch(value: string): string {
  return value
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/['‘’ʼ`´]/g, '')
    .replace(/[^\p{L}\p{N}]+/gu, ' ')
    .trim();
}

/** The terms a candidate has to carry, in any field and any order. `[]` means "no query". */
export function searchTerms(query: string): string[] {
  const normalized = normalizeForSearch(query);
  return normalized.length === 0 ? [] : normalized.split(' ');
}

/** A field's folded text, computed once per row rather than per keystroke. */
export type NormalizedFields = readonly string[];

/** Fold a row's fields for matching. Empty/nullish fields are dropped. */
export function normalizeFields(...fields: (string | null | undefined)[]): string[] {
  const out: string[] = [];
  for (const field of fields) {
    if (!field) continue;
    const normalized = normalizeForSearch(field);
    if (normalized.length > 0) out.push(normalized);
  }
  return out;
}

/** Does `haystack` carry `term` at the start of one of its words? */
function hasWordPrefix(haystack: string, term: string): boolean {
  let from = 0;
  for (;;) {
    const at = haystack.indexOf(term, from);
    if (at < 0) return false;
    if (at === 0 || haystack[at - 1] === ' ') return true;
    from = at + 1;
  }
}

/**
 * Every term present in at least one field.
 *
 * A term matches a field at a word boundary *or* mid-word: someone typing “wrld” should find
 * “Juice WRLD”, and someone typing a fragment they half-remember should still narrow the list.
 * Which of the two it was only changes the ranking (see `scoreFields`), never the answer.
 */
export function matchesTerms(fields: NormalizedFields, terms: readonly string[]): boolean {
  for (const term of terms) {
    let found = false;
    for (const field of fields) {
      if (field.includes(term)) {
        found = true;
        break;
      }
    }
    if (!found) return false;
  }
  return true;
}

/** Fold `fields` and match `query` against them in one call — for one-off checks, not hot loops. */
export function matchesQuery(query: string, ...fields: (string | null | undefined)[]): boolean {
  const terms = searchTerms(query);
  if (terms.length === 0) return true;
  return matchesTerms(normalizeFields(...fields), terms);
}

/**
 * Relevance, higher is better; `0` means "does not match" (so a caller can score-and-filter in one
 * pass). Fields are weighted by the order they are passed — first field matters most, which for a
 * track is its title.
 *
 * The shape of the score, in order of how much it moves a row:
 *   - the whole query *is* a field (“best friend” typed at 50 Cent's “Best Friend”);
 *   - a field starts with the whole query;
 *   - a field contains the whole query somewhere;
 *   - then per term: which field it landed in, and whether it started a word there.
 *
 * Ties are broken by the caller (`rankBySearch` sorts equal scores by how much text surrounds the
 * match), so the same query always produces the same order.
 */
export function scoreFields(fields: NormalizedFields, terms: readonly string[]): number {
  if (terms.length === 0) return 1;
  if (!matchesTerms(fields, terms)) return 0;

  const phrase = terms.join(' ');
  let score = 1;

  for (let i = 0; i < fields.length; i++) {
    const field = fields[i];
    // Weight by position: title (0) over artist (1) over album (2), and never below 1.
    const weight = Math.max(6 - i * 2, 1);
    if (field === phrase) score += weight * 20;
    else if (hasWordPrefix(field, phrase))
      score += field.startsWith(phrase) ? weight * 8 : weight * 4;
    else if (field.includes(phrase)) score += weight * 2;
  }

  for (const term of terms) {
    let best = 0;
    for (let i = 0; i < fields.length; i++) {
      const field = fields[i];
      if (!field.includes(term)) continue;
      const weight = Math.max(6 - i * 2, 1);
      const hit = field.startsWith(term)
        ? weight * 3
        : hasWordPrefix(field, term)
          ? weight * 2
          : weight;
      if (hit > best) best = hit;
    }
    score += best;
  }

  return score;
}

/** A row paired with the folded fields it is searched by. Build once per dataset, not per keystroke. */
export type SearchEntry<T> = {
  value: T;
  /** Most significant field first. */
  fields: string[];
  /** Every field joined — the cheap "could this match at all" pre-filter. */
  haystack: string;
};

/** Index `rows` for searching, most significant field first. */
export function indexForSearch<T>(
  rows: readonly T[],
  fieldsOf: (row: T) => (string | null | undefined)[]
): SearchEntry<T>[] {
  return rows.map((value) => {
    const fields = normalizeFields(...fieldsOf(value));
    return { value, fields, haystack: fields.join(' ') };
  });
}

/**
 * Every row matching `terms`, in index order — what a list filter wants (it shows all of them, so
 * relevance would only shuffle a list the user is reading). `terms` empty returns every row.
 */
export function filterBySearch<T>(index: readonly SearchEntry<T>[], terms: readonly string[]): T[] {
  if (terms.length === 0) return index.map((entry) => entry.value);
  const out: T[] = [];
  for (const entry of index) {
    if (matchesTerms([entry.haystack], terms)) out.push(entry.value);
  }
  return out;
}

/**
 * The best `limit` rows for `terms`, most relevant first.
 *
 * Equal scores fall back to the shorter row (an exact-ish match has less text around it, so “Best
 * Friend” outranks “Best Friend (Remix)”), then to the index order, which keeps the list stable.
 */
export function rankBySearch<T>(
  index: readonly SearchEntry<T>[],
  terms: readonly string[],
  limit: number
): T[] {
  if (terms.length === 0) return [];
  const scored: { value: T; score: number; length: number; order: number }[] = [];
  for (let i = 0; i < index.length; i++) {
    const entry = index[i];
    // Cheap reject first: the joined haystack rules out most of a library per term.
    if (!matchesTerms([entry.haystack], terms)) continue;
    const score = scoreFields(entry.fields, terms);
    if (score === 0) continue;
    scored.push({ value: entry.value, score, length: entry.haystack.length, order: i });
  }
  scored.sort((a, b) => b.score - a.score || a.length - b.length || a.order - b.order);
  return scored.slice(0, limit).map((s) => s.value);
}

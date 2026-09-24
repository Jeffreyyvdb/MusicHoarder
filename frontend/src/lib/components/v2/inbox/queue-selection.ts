/**
 * The pure half of an Inbox queue's selection (Tag review, Duplicates, AI flagged): which item
 * the URL asks for, what the queue actually shows, and where a decision advances to. The reactive
 * half (`queue-selection.svelte.ts`) reads the URL and navigates; everything it decides is here,
 * so it can be tested without SvelteKit.
 *
 * The rules: the selection lives in the URL (`song=<id>`, or `group=<id>` for
 * a duplicate cluster — its lowest song id, stable across reloads, never a list index). A phone
 * shows the list first and never auto-selects; a desktop still opens on the first item.
 */

/** A positive integer id from a URL param; anything else means "nothing selected". */
export function parseQueueId(raw: string | null | undefined): number | null {
  if (raw == null || raw.trim() === '') return null;
  const n = Number(raw);
  return Number.isSafeInteger(n) && n > 0 ? n : null;
}

/**
 * The URL of a queue, with `id` selected or (null) as the bare list. It always carries `?tab=`:
 * a bare `/inbox` is the hub on a phone, so a queue URL without it would drop you out of the
 * queue.
 */
export function queueHref(
  pathname: string,
  tab: string,
  param: 'song' | 'group',
  id: number | null
): string {
  const params = new URLSearchParams({ tab });
  if (id != null) params.set(param, String(id));
  return `${pathname}?${params}`;
}

/**
 * The item a queue shows for a requested id. A requested id that is in the queue wins. Otherwise
 * a phone shows the list (null) and a desktop shows the first item.
 */
export function resolveSelection(
  requested: number | null,
  ids: readonly number[],
  compact: boolean
): number | null {
  if (requested != null && ids.includes(requested)) return requested;
  return compact ? null : (ids[0] ?? null);
}

/** 1-based position of `id` in the queue and the ids either side of it (null at the ends). */
export function neighbours(
  id: number | null,
  ids: readonly number[]
): { position: number; prev: number | null; next: number | null } {
  const index = id == null ? -1 : ids.indexOf(id);
  if (index < 0) return { position: 0, prev: null, next: null };
  return {
    position: index + 1,
    prev: ids[index - 1] ?? null,
    next: ids[index + 1] ?? null
  };
}

/**
 * Where the queue goes after `id` has been decided: the first eligible item after it, else the
 * nearest eligible one before it, else nothing. `ids` is the order before the decision, so the
 * decided item's neighbours are still known. `eligible` excludes rows a decision should skip
 * over (Tag review's skipped rows).
 */
export function nextAfterDecision(
  id: number,
  ids: readonly number[],
  eligible: (candidate: number) => boolean = () => true
): number | null {
  const index = ids.indexOf(id);
  if (index < 0) return ids.find((c) => c !== id && eligible(c)) ?? null;
  for (let i = index + 1; i < ids.length; i++) if (eligible(ids[i])) return ids[i];
  for (let i = index - 1; i >= 0; i--) if (eligible(ids[i])) return ids[i];
  return null;
}

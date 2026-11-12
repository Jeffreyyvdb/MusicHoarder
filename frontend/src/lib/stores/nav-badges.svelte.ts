/**
 * The Inbox's counts — one place for every number that says "this needs you".
 *
 * The tab bar's Inbox badge, the sidebar's Inbox group badge and item counts, the Inbox hub's
 * rows and the Pipeline page's "Awaiting you" all read {@link inboxCounts} rather than each
 * keeping a predicate of its own, so no two screens can disagree about the same queue. It doesn't
 * live in `#lib/nav.ts` because that module is deliberately store-free (plain data, so nav.test.ts can
 * import it without pulling in the songs store) — see its own header comment.
 *
 * The badge is the sum of the queues that hold per-item decisions — Tag review, Duplicate tracks
 * and AI flagged — each counted exactly as its page lists it, so the badge never promises items the
 * Inbox does not show. Two things are deliberately left out of it:
 * - Failed matches. No Inbox queue lists them (Tag review is needs-review only); they surface on
 *   the Pipeline page's Errors figure and By folder's "No match". Counting them here was what made
 *   the badge read 14 over a hub whose rows added up to 10.
 * - The name-merge suggestions (Artist names, Album names). Housekeeping rather than decisions, and
 *   a request that makes the server walk every song; the hub (and the desktop sidebar) fetch
 *   their counts while they are on screen.
 */

import {
  fetchDuplicates,
  fetchInboxNameCounts,
  fetchQualityOverview,
  mapEnrichmentStatus
} from '#lib/api-client.ts';
import { songsStore } from '#lib/stores/songs.svelte.ts';

/** Queues whose count comes from a request of its own (Tag review's comes from the songs list). */
export type FetchedInboxQueue = 'dupes' | 'ai' | 'artists' | 'albums';

export type InboxCounts = {
  /** Tag review: needs-review songs. */
  review: number | null;
  /** Duplicate tracks: duplicate groups. */
  dupes: number | null;
  /** AI flagged: graded Wrong or Questionable (the quality overview's `aiFlaggedCount`). */
  ai: number | null;
  /** Artist names: variant-spelling clusters plus combined credits. */
  artists: number | null;
  /** Album names: split albums plus duplicate-name pairs. */
  albums: number | null;
  /** Failed matches. Not an Inbox queue — the Pipeline page's Errors figure. */
  failed: number | null;
  /** The badge: review + dupes + ai, over the parts that are known. */
  total: number | null;
};

const fetched = $state<Record<FetchedInboxQueue, number | null>>({
  dupes: null,
  ai: null,
  artists: null,
  albums: null
});

// A refresh at most once a minute unless forced — the shell asks on every entry to and exit from
// the Inbox, and a queue that has just loaded publishes its own fresher figure anyway.
const STALE_MS = 60_000;
const lastLoaded: Record<'decisions' | 'names', number> = { decisions: 0, names: 0 };
const inFlight: Record<'decisions' | 'names', Promise<void> | null> = {
  decisions: null,
  names: null
};

function songCount(status: string): number | null {
  const songs = songsStore.songs;
  if (songs.length === 0) return null;
  return songs.filter((s) => mapEnrichmentStatus(s.enrichmentStatus) === status).length;
}

/**
 * Resolves once the library's first load is out of the way (or after eight seconds regardless).
 * The name counts make the server walk every song; started alongside the song list they took
 * the same CPU and held back the one request the page is waiting for, for a number in a sidebar.
 */
async function afterLibraryLoad(): Promise<void> {
  const wait = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));
  // A page asks for the library a moment after the shell mounts; give it the chance to.
  await wait(150);
  const deadline = Date.now() + 8000;
  while (songsStore.isLoading && Date.now() < deadline) await wait(100);
}

function load(kind: 'decisions' | 'names', force: boolean): Promise<void> {
  if (typeof window === 'undefined') return Promise.resolve();
  const running = inFlight[kind];
  if (running) return running;
  if (!force && Date.now() - lastLoaded[kind] < STALE_MS) return Promise.resolve();
  const run = (async () => {
    // Each figure independently: one failing endpoint leaves its row blank, not the others.
    if (kind === 'decisions') {
      await Promise.all([
        fetchDuplicates()
          .then((r) => (fetched.dupes = (r.duplicateGroups ?? []).length))
          .catch(() => {}),
        // The server counts it with the queue's own predicate (the "wrong-or-questionable"
        // category), over every grade — not the overview's top-50 worst-offenders sample.
        fetchQualityOverview()
          .then((r) => (fetched.ai = r.aiFlaggedCount))
          .catch(() => {})
      ]);
    } else {
      if (!force) await afterLibraryLoad();
      // One request for both figures: the server counts them from a single read of the library
      // and sends two numbers. The reports themselves belong to the Artist names and Album names
      // pages; asking for all three here made the server walk the library three times per page
      // load and send every suggestion just to have it counted.
      await fetchInboxNameCounts()
        .then((r) => {
          fetched.artists = r.artists;
          fetched.albums = r.albums;
        })
        .catch(() => {});
    }
    lastLoaded[kind] = Date.now();
  })().finally(() => (inFlight[kind] = null));
  inFlight[kind] = run;
  return run;
}

/**
 * Fetch the Duplicate tracks and AI flagged figures the badge adds up. Anyone who sees the Inbox
 * calls this (admin and demo; a member has no Inbox). `force` skips the once-a-minute throttle.
 */
export function refreshInboxQueueCounts(force = false): Promise<void> {
  return load('decisions', force);
}

/**
 * Fetch the Artist names and Album names figures — admin only (the endpoint is admin-gated).
 * The Inbox hub asks while it shows, and the desktop sidebar while its Inbox group is open.
 */
export function refreshInboxNameCounts(force = false): Promise<void> {
  return load('names', force);
}

/**
 * A queue that has just loaded (or just resolved an item) reports its own size, so the badge
 * follows a decision without waiting for the next refresh. `null` (still loading) is ignored.
 */
export function publishInboxQueueCount(queue: FetchedInboxQueue, n: number | null): void {
  if (n != null) fetched[queue] = n;
}

export function inboxCounts(): InboxCounts {
  const review = songCount('needsreview');
  const parts = [review, fetched.dupes, fetched.ai].filter((n): n is number => n != null);
  return {
    review,
    dupes: fetched.dupes,
    ai: fetched.ai,
    artists: fetched.artists,
    albums: fetched.albums,
    failed: songCount('failed'),
    total: parts.length === 0 ? null : parts.reduce((a, b) => a + b, 0)
  };
}

/** The tab bar's and the sidebar group's badge: {@link InboxCounts.total}. */
export function inboxBadgeCount(): number | null {
  return inboxCounts().total;
}

/**
 * The Tag review queue's own size — needs-review only. A queue row (the sidebar's Tag review item,
 * the Inbox hub) says what its page lists.
 */
export function tagReviewCount(): number | null {
  return songCount('needsreview');
}

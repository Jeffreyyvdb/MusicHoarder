/**
 * The Inbox "needs a human" count — songs sitting at needs-review or failed enrichment.
 *
 * AppSidebarV2's Inbox group badge and BottomNavV2's Inbox tab badge both read this instead of
 * each keeping their own copy of the predicate, so the two numbers can never disagree. It doesn't
 * live in `$lib/nav` because that module is deliberately store-free (plain data, so nav.test.ts
 * can import it without pulling in the songs store) — see its own header comment.
 */

import { mapEnrichmentStatus } from '$lib/api-client';
import { songsStore } from '$lib/stores/songs.svelte';

export function inboxBadgeCount(): number | null {
  const songs = songsStore.songs;
  if (songs.length === 0) return null;
  return songs.filter((s) => {
    const status = mapEnrichmentStatus(s.enrichmentStatus);
    return status === 'needsreview' || status === 'failed';
  }).length;
}

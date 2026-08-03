/**
 * Shared library-view predicates.
 *
 * Historical note: this module used to derive whole client-side "sections" (recent / duplicates /
 * missing / queue) from the fetchSongs() result, including a naive artist+title+duration duplicate
 * heuristic that diverged from the backend's detection. Those consumers are gone — duplicates are
 * now served by GET /api/library/duplicates (fingerprint-similarity clusters) and rendered in the
 * Inbox — so only the build-state predicate remains.
 */
import type { ApiSong } from '$lib/api-client';

/**
 * A song is "built"/clean when it reached the destination library:
 * LibraryBuildStatus == Done (serialized as 3 or "Done") AND a destinationPath is set.
 * This implies it was enriched + matched first, so the main Library view can rely on it.
 */
export function isBuiltSong(s: ApiSong): boolean {
  if (!s.destinationPath) return false;
  const status = s.libraryBuildStatus;
  if (typeof status === 'number') return status === 3;
  if (typeof status === 'string') return status.toLowerCase() === 'done';
  return false;
}

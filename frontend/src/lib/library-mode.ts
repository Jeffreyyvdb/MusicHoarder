/**
 * Which library the data layer talks to. `owner` (default) is the account's own rows via the
 * normal endpoints; `shared` re-points the api-client's song/stream/cover/lyrics/video/like
 * calls at the grant-scoped `/api/shared` surface, which is how a Friend session reuses the
 * entire Listen UI (Overview, Library, Artists, Tracks, the full now-playing panel) unchanged.
 *
 * Set once per session by the `(app)` layout from the session role, and reset on sign-out —
 * module state deliberately mirrors the other client singletons that survive client-side
 * navigation. In its own module (not api-client) so pure helpers like `isBuiltSong` can read
 * it without importing the whole client.
 */

export type LibraryMode = 'owner' | 'shared';

let mode: LibraryMode = 'owner';

export function setLibraryMode(next: LibraryMode): void {
  mode = next;
}

export function isSharedLibraryMode(): boolean {
  return mode === 'shared';
}

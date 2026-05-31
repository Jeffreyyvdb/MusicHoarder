import type { HandleClientError } from '@sveltejs/kit';

// When the server is redeployed (or a single-instance container restarts) the
// content-hashed chunks an already-open tab still references disappear, so the
// next lazy import fails with "Failed to fetch dynamically imported module".
// There's nothing to recover client-side — reload once to pull the fresh build.
const isStaleChunkError = (error: unknown): boolean =>
  error instanceof Error &&
  /Failed to fetch dynamically imported module|error loading dynamically imported module|Importing a module script failed/i.test(
    error.message
  );

// Only suppress the auto-reload if we *just* reloaded — a fresh stale-chunk error this soon
// after a reload means the new build is genuinely unreachable, so stop and show the error
// instead of looping. A stale chunk long after the last reload is simply the NEXT deploy and
// must reload again, so the guard is a short time window, not a permanent flag (a permanent
// flag left every deploy after the first one stuck on "Something went wrong." until the user
// cleared their browser data).
const RELOAD_LOOP_WINDOW_MS = 15_000;

export const handleError: HandleClientError = ({ error }) => {
  if (isStaleChunkError(error) && typeof location !== 'undefined' && typeof sessionStorage !== 'undefined') {
    const key = 'mh:stale-chunk-reloaded-at';
    const lastReloadAt = Number(sessionStorage.getItem(key) ?? '0');
    const reloadedJustNow = lastReloadAt > 0 && Date.now() - lastReloadAt < RELOAD_LOOP_WINDOW_MS;
    if (!reloadedJustNow) {
      sessionStorage.setItem(key, String(Date.now()));
      location.reload();
      return { message: 'Reloading…' };
    }
  }

  return { message: 'Something went wrong.' };
};

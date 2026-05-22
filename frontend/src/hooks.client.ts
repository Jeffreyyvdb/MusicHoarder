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

export const handleError: HandleClientError = ({ error }) => {
  if (isStaleChunkError(error) && typeof location !== 'undefined') {
    // Guard against a reload loop if the fresh build is genuinely unreachable.
    const key = 'mh:stale-chunk-reloaded';
    if (!sessionStorage.getItem(key)) {
      sessionStorage.setItem(key, String(Date.now()));
      location.reload();
      return { message: 'Reloading…' };
    }
  } else if (typeof sessionStorage !== 'undefined') {
    sessionStorage.removeItem('mh:stale-chunk-reloaded');
  }

  return { message: 'Something went wrong.' };
};

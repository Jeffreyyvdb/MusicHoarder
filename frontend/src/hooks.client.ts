import type { HandleClientError } from '@sveltejs/kit';
import {
  isStaleChunkError,
  recoverFromStaleChunk,
  registerStaleChunkRecovery
} from '$lib/stale-chunk-recovery';

// Catch the stale-chunk failures that escape SvelteKit's client error hook
// (unhandled rejections / vite:preloadError). See stale-chunk-recovery.ts.
registerStaleChunkRecovery();

export const handleError: HandleClientError = ({ error }) => {
  // A new deploy replaced the content-hashed chunks this tab references — reload
  // (with backoff) to pull the fresh build rather than show a dead error page.
  if (isStaleChunkError(error) && recoverFromStaleChunk()) {
    return { message: 'Updating…' };
  }

  return { message: 'Something went wrong.' };
};

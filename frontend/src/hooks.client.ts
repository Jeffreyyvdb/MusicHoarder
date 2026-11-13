import type { HandleClientError } from '@sveltejs/kit/hooks';
import {
  isStaleChunkError,
  recoverFromStaleChunk,
  registerStaleChunkRecovery
} from '#lib/stale-chunk-recovery.ts';

// Catch the stale-chunk failures that escape SvelteKit's client error hook
// (unhandled rejections / vite:preloadError). See stale-chunk-recovery.ts.
registerStaleChunkRecovery();

export const handleError: HandleClientError = ({ kind, error }) => {
  // A new deploy replaced the content-hashed chunks this tab references — reload
  // (with backoff) to pull the fresh build rather than show a dead error page.
  if (isStaleChunkError(error) && recoverFromStaleChunk()) {
    return { message: 'Updating…' };
  }

  // Since SvelteKit 3 this hook also sees expected errors: `error(404, …)` from a load ('app')
  // and SvelteKit's own, such as an unknown route ('framework'). Those already carry the status
  // and message meant for the error page, so keep them; only an unexpected throw is masked.
  if (kind !== 'unknown') return error;

  return { message: 'Something went wrong.' };
};

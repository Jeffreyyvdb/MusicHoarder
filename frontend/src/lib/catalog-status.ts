/** What a catalog result's Add control says about a track already on the wishlist. */
const LABELS: Record<string, string> = {
  Pending: 'In wishlist',
  Downloading: 'Downloading',
  Downloaded: 'Downloaded',
  SkippedOwned: 'In library'
};

/** A download that ended without a file: adding again requeues it. */
const RETRYABLE = new Set(['Failed', 'NotFound']);

/**
 * The word shown instead of Add, or null when the track can be added (or added again). Any status
 * this list does not know still reads as being on the wishlist rather than offering a second add.
 */
export function wishlistLabel(status: string | null | undefined): string | null {
  if (status == null || RETRYABLE.has(status)) return null;
  return LABELS[status] ?? 'In wishlist';
}

export function isRetry(status: string | null | undefined): boolean {
  return status != null && RETRYABLE.has(status);
}

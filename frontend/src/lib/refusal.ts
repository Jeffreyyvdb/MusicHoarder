/** A 4xx answer, which asking again will not change — unless it was a timeout or a rate limit. */
export function isRefusal(err: unknown): boolean {
  const status = (err as { status?: unknown } | null)?.status;

  return (
    typeof status === 'number' && status >= 400 && status < 500 && status !== 408 && status !== 429
  );
}

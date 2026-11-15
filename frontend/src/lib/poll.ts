/**
 * The one way the app polls the API: `fn` every `intervalMs`, but only while the page is visible.
 *
 * A bare `setInterval` keeps firing in a background tab and in the installed app while it sits in
 * the background, so every banner and status poll kept asking the server something nobody could
 * see. Here the timer stops when the page is hidden, and the page catches up with one call the
 * moment it is shown again, then resumes the cadence.
 *
 * The returned function stops it for good — hand it straight back from an `$effect` or `onMount`.
 * ESLint flags a bare `setInterval` anywhere in `src` and points here; a timer that is
 * not a poll of the server (the player's clocks, Connect's heartbeat) says why on its disable line.
 */
export interface PollOptions {
  /** Call `fn` once right away, whether or not the page is visible (a first load). */
  immediate?: boolean;
}

export function poll(
  fn: () => unknown,
  intervalMs: number,
  { immediate = false }: PollOptions = {}
): () => void {
  if (typeof document === 'undefined') return () => {};

  let timer: ReturnType<typeof setInterval> | null = null;
  let stopped = false;

  const start = () => {
    // eslint-disable-next-line no-restricted-syntax -- this is the helper the rule points to
    if (timer === null) timer = setInterval(fn, intervalMs);
  };
  const pause = () => {
    if (timer !== null) clearInterval(timer);
    timer = null;
  };
  const onVisibility = () => {
    if (stopped) return;
    if (document.visibilityState === 'hidden') {
      pause();
    } else if (timer === null) {
      // Back in view: catch up now rather than up to a whole interval late.
      fn();
      start();
    }
  };

  document.addEventListener('visibilitychange', onVisibility);
  if (immediate) fn();
  if (document.visibilityState !== 'hidden') start();

  return () => {
    stopped = true;
    pause();
    document.removeEventListener('visibilitychange', onVisibility);
  };
}

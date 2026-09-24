/**
 * The one commit model for Settings' switches, iOS-style: the switch moves the moment it is
 * flipped, the write runs behind it, and a refused write puts back what the server holds.
 *
 * One committer per kind of setting; `key` names one control (a provider, one person's
 * capability set). Per key it remembers the last value the server confirmed and how many writes
 * are still out, and only corrects the view when the LAST of them settles:
 * - two quick flips whose writes both fail go back to what the server held before either, not
 *   to the first flip's value (which the server never had);
 * - an earlier write's answer never pulls a later flip back while that one is still saving (a
 *   person's second capability switch would otherwise blink off for a second).
 *
 * `read`/`write` are the view's own state (a $state field, a row in a list), so the switch — a
 * fully controlled SwitchRow — always shows exactly that. Writes for one key must settle in the
 * order they were made (every caller chains them), or a stale answer could become "confirmed".
 *
 * Each call resolves to null on success, else to its error, which the caller words for the
 * person.
 */
export function createOptimisticCommitter<T>(equals: (a: T, b: T) => boolean = Object.is) {
  const confirmed = new Map<string, T>();
  const pending = new Map<string, number>();

  return async function commit(
    key: string,
    opts: {
      read: () => T;
      write: (value: T) => void;
      next: T;
      /**
       * Resolves to the value the server now holds when it says so (a capability set with its
       * implied flags); resolving to nothing confirms `next`.
       */
      save: () => Promise<T | void>;
    }
  ): Promise<unknown> {
    const { read, write, next, save } = opts;
    const out = pending.get(key) ?? 0;
    // With nothing in flight for this control, what it shows is what the server holds.
    if (out === 0) confirmed.set(key, read());
    pending.set(key, out + 1);
    write(next);

    let error: unknown = null;
    try {
      const held = await save();
      confirmed.set(key, held === undefined ? next : held);
    } catch (err) {
      error = err ?? new Error('Failed');
    }

    const left = (pending.get(key) ?? 1) - 1;
    if (left > 0) {
      pending.set(key, left);
      return error;
    }
    const settled = confirmed.get(key) as T;
    pending.delete(key);
    confirmed.delete(key);
    if (!equals(read(), settled)) write(settled);
    return error;
  };
}

/** Two capability lists hold the same set (order-insensitive). */
export function sameSet<T>(a: readonly T[], b: readonly T[]): boolean {
  if (a.length !== b.length) return false;
  const set = new Set(a);
  return b.every((x) => set.has(x));
}

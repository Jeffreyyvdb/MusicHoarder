/**
 * Shuffle as a mode of the queue, the way Spotify's Now Playing button works: turning it on
 * reorders only what is still to come (the song playing keeps playing, and what was heard stays
 * behind it for Previous), and turning it off goes back to the list's own order from the song
 * playing on. Pure, so the player store stays the one place that holds the queue.
 */

export type Rng = () => number;

/** Non-mutating Fisher–Yates over `items`, drawing from `rng`. */
export function shuffled<T>(items: readonly T[], rng: Rng = Math.random): T[] {
  const out = items.slice();
  for (let i = out.length - 1; i > 0; i--) {
    const j = Math.floor(rng() * (i + 1));
    [out[i], out[j]] = [out[j], out[i]];
  }
  return out;
}

/** The queue with everything after `index` shuffled; the history and the current item stay put. */
export function shuffleUpcoming<T>(
  queue: readonly T[],
  index: number,
  rng: Rng = Math.random
): T[] {
  if (index < 0) return queue.slice();
  return [...queue.slice(0, index + 1), ...shuffled(queue.slice(index + 1), rng)];
}

/**
 * A list started in shuffle: `list[index]` first, then every other item of the list in a random
 * order. With no `index`, the first song is a random one too — what a Shuffle button plays.
 */
export function startShuffled<T>(list: readonly T[], index?: number, rng: Rng = Math.random): T[] {
  if (list.length === 0) return [];
  const drawn = index ?? Math.floor(rng() * list.length);
  const at = drawn >= 0 && drawn < list.length ? drawn : 0;
  return [list[at], ...shuffled([...list.slice(0, at), ...list.slice(at + 1)], rng)];
}

/**
 * Turning shuffle off: what was heard (and the song playing) stays where it is, and what follows
 * is the list's own order from the playing song on, then anything the queue gained since that the
 * list never held (the station's additions), in the order they were added. When the playing song
 * (or `anchor`) is not in the list (it can no longer be placed), the queue is left as it is.
 */
export function unshuffle<T extends { id: number }>(
  queue: readonly T[],
  index: number,
  original: readonly T[],
  /**
   * The entry the list carries on from: the playing song unless that is not one of the list's
   * own (a song added to the queue by hand), when it is the last list song played before it.
   */
  anchor = index
): T[] {
  const current = queue[anchor];
  if (!current) return queue.slice();
  const same = original.indexOf(current);
  const at = same >= 0 ? same : original.findIndex((s) => s.id === current.id);
  if (at < 0) return queue.slice();
  const listed = new Set(original.map((s) => s.id));
  const added = queue.slice(index + 1).filter((s) => !listed.has(s.id));
  return [...queue.slice(0, index + 1), ...original.slice(at + 1), ...added];
}

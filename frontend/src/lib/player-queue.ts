/**
 * "Next in queue": the songs someone added by hand (a row's swipe, Add to queue, Play next), which
 * play after the current song and before the rest of the list, the way Spotify's queue works.
 *
 * The player keeps one array, `queue`, positioned at `index`. The added songs are a run of it,
 * `block` = [from, end) in absolute positions, and only the part after `index` is still to come
 * (`queuedRange`). Keeping the run as two numbers rather than a flag on each song means moving on
 * needs nothing (the run's start is clamped to the song after the current one, so a played song
 * drops out of it by itself), and the queue array — which can be the whole library — is replaced
 * only when its order actually changes.
 *
 * Pure, so the player store stays the one place that holds the queue, and every rule here is
 * tested case by case. Positions passed in and out are relative to the songs after `index`
 * ("upcoming"), which is how the queue view numbers its rows.
 */

/** The added songs, as absolute positions [from, end) in the queue. */
export interface QueuedBlock {
  from: number;
  end: number;
}

export const NO_QUEUED: QueuedBlock = { from: 0, end: 0 };

export interface QueueEdit<T> {
  queue: T[];
  block: QueuedBlock;
}

/** The part of the block still to come: from the song after the current one at the earliest. */
export function queuedRange(index: number, block: QueuedBlock): QueuedBlock {
  const from = Math.max(block.from, index + 1);
  return { from, end: Math.max(from, block.end) };
}

/** How many of the upcoming songs were added by hand. They are the first ones up next. */
export function queuedCount(index: number, block: QueuedBlock): number {
  const live = queuedRange(index, block);
  return live.end - live.from;
}

/**
 * Add `songs` to the queue. `last` ("Add to queue") puts them after the songs already added, so
 * what was added first plays first; `next` ("Play next") puts them straight after the current
 * song. Either way they become part of the block.
 */
export function enqueue<T>(
  queue: readonly T[],
  index: number,
  block: QueuedBlock,
  songs: readonly T[],
  where: 'next' | 'last'
): QueueEdit<T> {
  const live = queuedRange(index, block);
  const hasLive = live.end > live.from;
  const at = where === 'last' && hasLive ? live.end : index + 1;
  const from = hasLive && where === 'last' ? live.from : index + 1;
  // Play next with songs already added: the block grows at its head. Should it start further on
  // (a Previous stepped back past it), what lies between joins it — songs that were heard already.
  const end = hasLive ? live.end + songs.length : at + songs.length;
  return {
    queue: [...queue.slice(0, at), ...songs, ...queue.slice(at)],
    block: { from, end }
  };
}

/** Remove the upcoming song at `position`. */
export function removeUpcoming<T>(
  queue: readonly T[],
  index: number,
  block: QueuedBlock,
  position: number
): QueueEdit<T> {
  const at = index + 1 + position;
  const live = queuedRange(index, block);
  if (position < 0 || at >= queue.length) return { queue: queue.slice(), block: live };
  const next = [...queue.slice(0, at), ...queue.slice(at + 1)];
  if (at < live.from) return { queue: next, block: { from: live.from - 1, end: live.end - 1 } };
  if (at < live.end) return { queue: next, block: { from: live.from, end: live.end - 1 } };
  return { queue: next, block: live };
}

/**
 * Move the upcoming song at `from` so it ends up at `to`. An added song stays added when it lands
 * at the block's edge; any song dropped inside the block becomes one of the added ones.
 */
export function moveUpcoming<T>(
  queue: readonly T[],
  index: number,
  block: QueuedBlock,
  from: number,
  to: number
): QueueEdit<T> {
  const upcoming = queue.length - index - 1;
  if (from === to || from < 0 || to < 0 || from >= upcoming || to >= upcoming) {
    return { queue: queue.slice(), block: queuedRange(index, block) };
  }
  const live = queuedRange(index, block);
  const fromAt = index + 1 + from;
  const wasQueued = fromAt >= live.from && fromAt < live.end;
  const song = queue[fromAt];
  const removed = removeUpcoming(queue, index, block, from);
  const at = index + 1 + to;
  const { from: start, end } = removed.block;
  const next = [...removed.queue.slice(0, at), song, ...removed.queue.slice(at)];
  let moved: QueuedBlock;
  if (at === end) moved = wasQueued ? { from: start, end: end + 1 } : { from: start, end };
  else if (at < start) moved = { from: start + 1, end: end + 1 };
  else if (at < end) moved = { from: start, end: end + 1 };
  else moved = { from: start, end };
  // A block emptied by the move has nowhere to be; park it at the song after the current one.
  if (moved.end <= moved.from) moved = { from: index + 1, end: index + 1 };
  return { queue: next, block: moved };
}

/** Take every added song out ("Clear queue"): what follows is the list again. */
export function clearQueued<T>(
  queue: readonly T[],
  index: number,
  block: QueuedBlock
): QueueEdit<T> {
  const live = queuedRange(index, block);
  return {
    queue: [...queue.slice(0, live.from), ...queue.slice(live.end)],
    block: { from: index + 1, end: index + 1 }
  };
}

/** The added songs still to come, in order. */
export function queuedSongs<T>(queue: readonly T[], index: number, block: QueuedBlock): T[] {
  const live = queuedRange(index, block);
  return queue.slice(live.from, live.end);
}

/**
 * A new list started (a row tapped, an album's Play): the songs added by hand and not yet played
 * carry over and play after its first song, as Spotify's queue does. One copy of the song the list
 * starts on is dropped from them — it is playing now, so it has been heard.
 */
export function carryQueued<T extends { id: number }>(
  pending: readonly T[],
  queue: readonly T[],
  index: number
): QueueEdit<T> {
  if (pending.length === 0 || index < 0) {
    return { queue: queue.slice(), block: { from: index + 1, end: index + 1 } };
  }
  const startingId = queue[index]?.id;
  const kept = pending.slice();
  const dup = kept.findIndex((s) => s.id === startingId);
  if (dup >= 0) kept.splice(dup, 1);
  return enqueue(queue, index, NO_QUEUED, kept, 'next');
}

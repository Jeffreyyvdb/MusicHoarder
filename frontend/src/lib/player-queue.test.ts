import { describe, expect, it } from 'vitest';
import {
  NO_QUEUED,
  carryQueued,
  clearQueued,
  enqueue,
  moveUpcoming,
  queuedCount,
  queuedRange,
  queuedSongs,
  removeUpcoming
} from './player-queue';

const songs = (...ids: number[]) => ids.map((id) => ({ id }));
const ids = (list: readonly { id: number }[]) => list.map((s) => s.id);

describe('queuedRange', () => {
  it('drops what has been played: the run starts after the current song at the earliest', () => {
    expect(queuedRange(3, { from: 2, end: 6 })).toEqual({ from: 4, end: 6 });
    expect(queuedCount(3, { from: 2, end: 6 })).toBe(2);
  });

  it('is empty once the current song is past the run', () => {
    expect(queuedCount(7, { from: 2, end: 6 })).toBe(0);
    expect(queuedRange(7, { from: 2, end: 6 })).toEqual({ from: 8, end: 8 });
  });
});

describe('enqueue', () => {
  it('Add to queue: after the current song when nothing was added yet', () => {
    const out = enqueue(songs(1, 2, 3, 4), 1, NO_QUEUED, songs(9), 'last');
    expect(ids(out.queue)).toEqual([1, 2, 9, 3, 4]);
    expect(out.block).toEqual({ from: 2, end: 3 });
  });

  it('Add to queue: after the songs already added, so the first added plays first', () => {
    const first = enqueue(songs(1, 2, 3, 4), 1, NO_QUEUED, songs(9), 'last');
    const out = enqueue(first.queue, 1, first.block, songs(8), 'last');
    expect(ids(out.queue)).toEqual([1, 2, 9, 8, 3, 4]);
    expect(queuedSongs(out.queue, 1, out.block).map((s) => s.id)).toEqual([9, 8]);
  });

  it('Play next: straight after the current song, ahead of what was added before', () => {
    const first = enqueue(songs(1, 2, 3, 4), 1, NO_QUEUED, songs(9), 'last');
    const out = enqueue(first.queue, 1, first.block, songs(8), 'next');
    expect(ids(out.queue)).toEqual([1, 2, 8, 9, 3, 4]);
    expect(queuedSongs(out.queue, 1, out.block).map((s) => s.id)).toEqual([8, 9]);
  });

  it('moving on through the added songs needs no edit: they drop out as they play', () => {
    const out = enqueue(songs(1, 2, 3), 0, NO_QUEUED, songs(8, 9), 'last');
    expect(queuedSongs(out.queue, 1, out.block).map((s) => s.id)).toEqual([9]);
    expect(queuedCount(2, out.block)).toBe(0);
  });

  it('works at the end of the queue', () => {
    const out = enqueue(songs(1, 2), 1, NO_QUEUED, songs(9), 'last');
    expect(ids(out.queue)).toEqual([1, 2, 9]);
    expect(out.block).toEqual({ from: 2, end: 3 });
  });
});

describe('removeUpcoming', () => {
  const start = enqueue(songs(1, 2, 3, 4), 0, NO_QUEUED, songs(8, 9), 'last');
  // queue: 1 | 8 9 | 2 3 4

  it('removes an added song and shrinks the run', () => {
    const out = removeUpcoming(start.queue, 0, start.block, 0);
    expect(ids(out.queue)).toEqual([1, 9, 2, 3, 4]);
    expect(queuedSongs(out.queue, 0, out.block).map((s) => s.id)).toEqual([9]);
  });

  it('removes a song after the run and leaves the run alone', () => {
    const out = removeUpcoming(start.queue, 0, start.block, 3);
    expect(ids(out.queue)).toEqual([1, 8, 9, 2, 4]);
    expect(queuedSongs(out.queue, 0, out.block).map((s) => s.id)).toEqual([8, 9]);
  });

  it('shifts the run when a song before it goes (after a Previous left a gap)', () => {
    // Current is 1 again (stepped back), with 2 between it and the run [2, 4).
    const queue = songs(1, 2, 8, 9, 3);
    const out = removeUpcoming(queue, 0, { from: 2, end: 4 }, 0);
    expect(ids(out.queue)).toEqual([1, 8, 9, 3]);
    expect(queuedSongs(out.queue, 0, out.block).map((s) => s.id)).toEqual([8, 9]);
  });

  it('ignores a position past the end', () => {
    const out = removeUpcoming(start.queue, 0, start.block, 99);
    expect(ids(out.queue)).toEqual(ids(start.queue));
  });
});

describe('moveUpcoming', () => {
  const start = enqueue(songs(1, 2, 3, 4), 0, NO_QUEUED, songs(7, 8, 9), 'last');
  // queue: 1 | 7 8 9 | 2 3 4

  it('reorders inside the added songs', () => {
    const out = moveUpcoming(start.queue, 0, start.block, 2, 0);
    expect(ids(out.queue)).toEqual([1, 9, 7, 8, 2, 3, 4]);
    expect(queuedSongs(out.queue, 0, out.block).map((s) => s.id)).toEqual([9, 7, 8]);
  });

  it('keeps an added song added when it is dragged to the end of the run', () => {
    const out = moveUpcoming(start.queue, 0, start.block, 0, 2);
    expect(ids(out.queue)).toEqual([1, 8, 9, 7, 2, 3, 4]);
    expect(queuedSongs(out.queue, 0, out.block).map((s) => s.id)).toEqual([8, 9, 7]);
  });

  it('reorders the rest of the list without touching the run', () => {
    const out = moveUpcoming(start.queue, 0, start.block, 5, 3);
    expect(ids(out.queue)).toEqual([1, 7, 8, 9, 4, 2, 3]);
    expect(queuedSongs(out.queue, 0, out.block).map((s) => s.id)).toEqual([7, 8, 9]);
  });

  it('a list song dropped inside the run becomes an added one', () => {
    const out = moveUpcoming(start.queue, 0, start.block, 4, 1);
    expect(ids(out.queue)).toEqual([1, 7, 3, 8, 9, 2, 4]);
    expect(queuedSongs(out.queue, 0, out.block).map((s) => s.id)).toEqual([7, 3, 8, 9]);
  });

  it('an added song dragged past the run leaves it', () => {
    const out = moveUpcoming(start.queue, 0, start.block, 0, 4);
    expect(ids(out.queue)).toEqual([1, 8, 9, 2, 3, 7, 4]);
    expect(queuedSongs(out.queue, 0, out.block).map((s) => s.id)).toEqual([8, 9]);
  });

  it('ignores positions out of range', () => {
    const out = moveUpcoming(start.queue, 0, start.block, 0, 99);
    expect(ids(out.queue)).toEqual(ids(start.queue));
  });
});

describe('clearQueued', () => {
  it('takes the added songs out and leaves the list', () => {
    const start = enqueue(songs(1, 2, 3), 0, NO_QUEUED, songs(8, 9), 'last');
    const out = clearQueued(start.queue, 0, start.block);
    expect(ids(out.queue)).toEqual([1, 2, 3]);
    expect(queuedCount(0, out.block)).toBe(0);
  });
});

describe('carryQueued', () => {
  it('puts the songs still to come after the new list’s first song', () => {
    const out = carryQueued(songs(8, 9), songs(10, 11, 12), 1);
    expect(ids(out.queue)).toEqual([10, 11, 8, 9, 12]);
    expect(queuedSongs(out.queue, 1, out.block).map((s) => s.id)).toEqual([8, 9]);
  });

  it('drops one copy of the song the list starts on: it is playing now', () => {
    const out = carryQueued(songs(8, 11, 9), songs(10, 11, 12), 1);
    expect(ids(out.queue)).toEqual([10, 11, 8, 9, 12]);
  });

  it('with nothing to carry, the queue is the list', () => {
    const out = carryQueued([], songs(10, 11), 0);
    expect(ids(out.queue)).toEqual([10, 11]);
    expect(queuedCount(0, out.block)).toBe(0);
  });
});

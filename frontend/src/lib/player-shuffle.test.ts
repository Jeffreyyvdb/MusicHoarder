import { describe, expect, it } from 'vitest';
import { shuffled, shuffleUpcoming, startShuffled, unshuffle } from './player-shuffle';

const songs = (...ids: number[]) => ids.map((id) => ({ id }));
const ids = (list: readonly { id: number }[]) => list.map((s) => s.id);

/** A fixed draw sequence, so an order can be asserted. */
function seq(...values: number[]) {
  let i = 0;
  return () => values[i++ % values.length];
}

describe('shuffled', () => {
  it('returns a permutation and leaves the input alone', () => {
    const input = [1, 2, 3, 4, 5];
    const out = shuffled(input, seq(0.1, 0.9, 0.4, 0.6));
    expect([...out].sort()).toEqual([1, 2, 3, 4, 5]);
    expect(input).toEqual([1, 2, 3, 4, 5]);
  });

  it('with rng 0 rotates the first item to the end (Fisher–Yates, j = 0 each step)', () => {
    expect(shuffled([1, 2, 3, 4], () => 0)).toEqual([2, 3, 4, 1]);
  });
});

describe('shuffleUpcoming', () => {
  it('keeps what was heard and the playing song in place; reorders only what follows', () => {
    const queue = songs(1, 2, 3, 4, 5, 6);
    const out = shuffleUpcoming(queue, 2, () => 0);
    expect(ids(out.slice(0, 3))).toEqual([1, 2, 3]);
    expect(ids(out.slice(3))).toEqual([5, 6, 4]);
  });

  it('at the last item changes nothing', () => {
    expect(ids(shuffleUpcoming(songs(1, 2, 3), 2))).toEqual([1, 2, 3]);
  });
});

describe('startShuffled', () => {
  it('starts at the tapped song, then every other song of the list once', () => {
    const out = startShuffled(songs(1, 2, 3, 4, 5), 3);
    expect(out[0].id).toBe(4);
    expect(ids(out).sort()).toEqual([1, 2, 3, 4, 5]);
  });

  it('with no index, starts at a random song', () => {
    expect(startShuffled(songs(1, 2, 3, 4), undefined, () => 0.99)[0].id).toBe(4);
    expect(startShuffled(songs(1, 2, 3, 4), undefined, () => 0)[0].id).toBe(1);
  });

  it('an empty list is an empty queue', () => {
    expect(startShuffled([])).toEqual([]);
  });
});

describe('unshuffle', () => {
  const album = songs(1, 2, 3, 4, 5, 6);

  it('carries on in the list order from the playing song, keeping the history', () => {
    // Shuffled 1 → 5 → 3, now on 3: back in order means 4, 5, 6 next.
    const queue = songs(1, 5, 3, 6, 2, 4);
    expect(ids(unshuffle(queue, 2, album))).toEqual([1, 5, 3, 4, 5, 6]);
  });

  it('keeps the station additions after the list', () => {
    const queue = songs(2, 6, 4, 1, 3, 5, 90, 91);
    expect(ids(unshuffle(queue, 0, album))).toEqual([2, 3, 4, 5, 6, 90, 91]);
  });

  it('leaves the queue alone when the playing song is not in the list', () => {
    const queue = songs(90, 2, 1);
    expect(ids(unshuffle(queue, 0, album))).toEqual([90, 2, 1]);
  });

  it('round-trips: shuffle on then off is the list order from the playing song', () => {
    const on = shuffleUpcoming(album, 1, seq(0.3, 0.8, 0.1, 0.5));
    expect(ids(unshuffle(on, 1, album))).toEqual([1, 2, 3, 4, 5, 6]);
  });
});

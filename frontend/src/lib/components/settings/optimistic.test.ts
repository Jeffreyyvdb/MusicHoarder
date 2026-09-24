import { describe, expect, it } from 'vitest';
import { createOptimisticCommitter, sameSet } from './optimistic';

function cell<T>(initial: T) {
  let value = initial;
  return { read: () => value, write: (v: T) => (value = v) };
}

/** A save the test settles by hand, in the order it chooses. */
function deferred<T = void>() {
  let resolve!: (value: T) => void;
  let reject!: (error: Error) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

describe('createOptimisticCommitter', () => {
  it('shows the new value at once and keeps it when the write lands', async () => {
    const commit = createOptimisticCommitter<boolean>();
    const c = cell(false);
    let seenDuringSave: boolean | null = null;
    const error = await commit('deezer', {
      ...c,
      next: true,
      save: async () => {
        seenDuringSave = c.read();
      }
    });
    expect(seenDuringSave).toBe(true);
    expect(error).toBeNull();
    expect(c.read()).toBe(true);
  });

  it('puts the old value back when the write is refused, and hands back the error', async () => {
    const commit = createOptimisticCommitter<boolean>();
    const c = cell(false);
    const refusal = new Error('500');
    const error = await commit('deezer', { ...c, next: true, save: () => Promise.reject(refusal) });
    expect(error).toBe(refusal);
    expect(c.read()).toBe(false);
  });

  it('goes back to what the server held when two quick flips both fail', async () => {
    // Deezer is on; flipped off, then on again before the first write answers. Both refused:
    // the server still holds "on", so that is what must show — not the first flip's "off".
    const commit = createOptimisticCommitter<boolean>();
    const c = cell(true);
    const first = deferred();
    const second = deferred();
    const a = commit('deezer', { ...c, next: false, save: () => first.promise });
    const b = commit('deezer', { ...c, next: true, save: () => second.promise });
    first.reject(new Error('500'));
    await a;
    second.reject(new Error('500'));
    await b;
    expect(c.read()).toBe(true);

    // And the mirror image, starting from "off".
    const d = cell(false);
    const third = deferred();
    const fourth = deferred();
    const x = commit('apple', { ...d, next: true, save: () => third.promise });
    const y = commit('apple', { ...d, next: false, save: () => fourth.promise });
    third.reject(new Error('500'));
    await x;
    fourth.reject(new Error('500'));
    await y;
    expect(d.read()).toBe(false);
  });

  it('keeps a later flip that lands after an earlier one failed', async () => {
    const commit = createOptimisticCommitter<boolean>();
    const c = cell(false);
    const first = deferred();
    const second = deferred();
    const a = commit('deezer', { ...c, next: true, save: () => first.promise });
    const b = commit('deezer', { ...c, next: false, save: () => second.promise });
    first.reject(new Error('500'));
    expect(await a).toBeInstanceOf(Error);
    // Still saving the second flip: nothing is pulled back yet.
    expect(c.read()).toBe(false);
    second.resolve();
    expect(await b).toBeNull();
    expect(c.read()).toBe(false);
  });

  it('settles on the earlier write when the later one fails', async () => {
    const commit = createOptimisticCommitter<boolean>();
    const c = cell(false);
    const first = deferred();
    const second = deferred();
    const a = commit('deezer', { ...c, next: true, save: () => first.promise });
    const b = commit('deezer', { ...c, next: false, save: () => second.promise });
    first.resolve();
    await a;
    second.reject(new Error('500'));
    await b;
    expect(c.read()).toBe(true);
  });

  it('never pulls a later flip back when an earlier answer lands first', async () => {
    // Two capability switches on one person, flipped 100ms apart: the first answer carries the
    // set without the second flip, and must not show while the second is still saving.
    const commit = createOptimisticCommitter<string[]>(sameSet);
    const c = cell<string[]>([]);
    const first = deferred<string[]>();
    const second = deferred<string[]>();
    const a = commit('sam', { ...c, next: ['ManageOwnShares'], save: () => first.promise });
    const b = commit('sam', {
      ...c,
      next: ['ManageOwnShares', 'DownloadMusic'],
      save: () => second.promise
    });
    first.resolve(['ManageOwnShares']);
    await a;
    expect(c.read()).toEqual(['ManageOwnShares', 'DownloadMusic']);
    second.resolve(['ManageOwnShares', 'DownloadMusic']);
    await b;
    expect(c.read()).toEqual(['ManageOwnShares', 'DownloadMusic']);
  });

  it('ends on the value the server reports, when it reports one', async () => {
    // Granting Administer implies every other flag; the server's answer is the truth.
    const commit = createOptimisticCommitter<string[]>(sameSet);
    const c = cell<string[]>(['TrackListening']);
    await commit('sam', {
      ...c,
      next: ['TrackListening', 'Administer'],
      save: async () => ['TrackListening', 'DownloadMusic', 'ManageOwnShares', 'Administer']
    });
    expect(
      sameSet(c.read(), ['TrackListening', 'DownloadMusic', 'ManageOwnShares', 'Administer'])
    ).toBe(true);
  });

  it('compares with the given equality (a refused capability set)', async () => {
    const commit = createOptimisticCommitter<string[]>(sameSet);
    const c = cell<string[]>(['TrackListening']);
    await commit('sam', {
      ...c,
      next: ['TrackListening', 'Administer'],
      save: () => Promise.reject(new Error('last_admin'))
    });
    expect(c.read()).toEqual(['TrackListening']);
  });

  it('keeps keys apart', async () => {
    const commit = createOptimisticCommitter<boolean>();
    const deezer = cell(true);
    const apple = cell(false);
    const slow = deferred();
    const a = commit('deezer', { ...deezer, next: false, save: () => slow.promise });
    await commit('apple', { ...apple, next: true, save: () => Promise.reject(new Error('500')) });
    expect(apple.read()).toBe(false);
    expect(deezer.read()).toBe(false);
    slow.resolve();
    await a;
    expect(deezer.read()).toBe(false);
  });
});

describe('sameSet', () => {
  it('ignores order but not membership', () => {
    expect(sameSet(['a', 'b'], ['b', 'a'])).toBe(true);
    expect(sameSet(['a'], ['a', 'b'])).toBe(false);
    expect(sameSet(['a', 'c'], ['a', 'b'])).toBe(false);
  });
});

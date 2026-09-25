import { describe, expect, it } from 'vitest';
import {
  neighbours,
  nextAfterDecision,
  parseQueueId,
  queueHref,
  resolveSelection
} from './queue-selection';

describe('parseQueueId', () => {
  it('reads positive integer ids', () => {
    expect(parseQueueId('42')).toBe(42);
  });

  it('treats anything else as no selection', () => {
    for (const raw of [null, undefined, '', ' ', '0', '-3', '1.5', 'abc', '1e400']) {
      expect(parseQueueId(raw)).toBeNull();
    }
  });
});

describe('queueHref', () => {
  it('always carries the tab, so a phone never lands on the hub', () => {
    expect(queueHref('/inbox', 'review', 'song', null)).toBe('/inbox?tab=review');
    expect(queueHref('/inbox', 'review', 'song', 7)).toBe('/inbox?tab=review&song=7');
    expect(queueHref('/inbox', 'dupes', 'group', 12)).toBe('/inbox?tab=dupes&group=12');
  });
});

describe('resolveSelection', () => {
  const ids = [5, 9, 2];

  it('shows the requested item when the queue has it, at every width', () => {
    expect(resolveSelection(9, ids, true)).toBe(9);
    expect(resolveSelection(9, ids, false)).toBe(9);
  });

  it('shows the list on a phone and the first item on a desktop otherwise', () => {
    expect(resolveSelection(null, ids, true)).toBeNull();
    expect(resolveSelection(null, ids, false)).toBe(5);
    expect(resolveSelection(404, ids, true)).toBeNull();
    expect(resolveSelection(404, ids, false)).toBe(5);
  });

  it('has nothing to show for an empty queue', () => {
    expect(resolveSelection(null, [], false)).toBeNull();
  });
});

describe('neighbours', () => {
  const ids = [5, 9, 2];

  it('gives the 1-based position and the ids either side', () => {
    expect(neighbours(5, ids)).toEqual({ position: 1, prev: null, next: 9 });
    expect(neighbours(9, ids)).toEqual({ position: 2, prev: 5, next: 2 });
    expect(neighbours(2, ids)).toEqual({ position: 3, prev: 9, next: null });
  });

  it('has no position for an item not in the queue', () => {
    expect(neighbours(null, ids)).toEqual({ position: 0, prev: null, next: null });
    expect(neighbours(1, ids)).toEqual({ position: 0, prev: null, next: null });
  });
});

describe('nextAfterDecision', () => {
  const ids = [1, 2, 3, 4];

  it('advances to the next item after the decided one', () => {
    expect(nextAfterDecision(2, ids)).toBe(3);
  });

  it('falls back to the nearest one before it at the end of the queue', () => {
    expect(nextAfterDecision(4, ids)).toBe(3);
  });

  it('skips items that are not eligible (Tag review skips skipped rows)', () => {
    const skipped = new Set([3, 4]);
    expect(nextAfterDecision(2, ids, (id) => !skipped.has(id))).toBe(1);
    expect(nextAfterDecision(1, ids, (id) => id === 1)).toBeNull();
  });

  it('returns nothing when the decided item was the last one', () => {
    expect(nextAfterDecision(1, [1])).toBeNull();
  });

  it('picks the first eligible item when the decided one is not in the order', () => {
    expect(nextAfterDecision(9, ids, (id) => id > 2)).toBe(3);
  });
});

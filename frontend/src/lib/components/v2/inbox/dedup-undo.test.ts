import { beforeEach, describe, expect, it, vi } from 'vitest';

// The module imports the API client and the toaster for its toast helper; both are mocked so the
// toast's Undo can be pressed and the revert it sends inspected.
vi.mock('$lib/api-client', () => ({ fetchDedupActions: vi.fn(), revertDedupAction: vi.fn() }));
vi.mock('svelte-sonner', () => ({ toast: { success: vi.fn(), error: vi.fn() } }));

const api = await import('$lib/api-client');
const { toast } = await import('svelte-sonner');
const { dedupKey, findNewAction, snapshotDedupKeys, toastWithUndo } = await import('./dedup-undo');

type Action = Parameters<typeof findNewAction>[1][number];

function action(partial: Partial<Action>): Action {
  return {
    source: 'artist-merge',
    createdAtUtc: '2026-09-23T10:00:00Z',
    batchTicks: 1,
    songCount: 3,
    changeCount: 3,
    highlights: [],
    reverted: false,
    revertible: true,
    ...partial
  } as Action;
}

describe('findNewAction', () => {
  it('finds the batch the decision wrote', () => {
    const old = action({ batchTicks: 100 });
    const fresh = action({ batchTicks: 200 });
    const before = new Set([dedupKey(old)]);
    expect(findNewAction(before, [fresh, old], 'artist-merge')).toBe(fresh);
  });

  it('ignores batches of another kind, reverted ones and ones that cannot be reverted', () => {
    const before = new Set<string>();
    const after = [
      action({ source: 'album-merge', batchTicks: 5 }),
      action({ batchTicks: 6, reverted: true }),
      action({ source: 'album-identity-heal', batchTicks: 7, revertible: false })
    ];
    expect(findNewAction(before, after, 'artist-merge')).toBeNull();
  });

  it('refuses to guess when more than one batch is new', () => {
    const a = action({ batchTicks: 300 });
    const b = action({ batchTicks: 900 });
    expect(findNewAction(new Set(), [a, b], 'artist-merge')).toBeNull();
  });

  it('finds nothing when the log did not change', () => {
    const old = action({ batchTicks: 100 });
    expect(findNewAction(new Set([dedupKey(old)]), [old], 'artist-merge')).toBeNull();
  });
});

describe('toastWithUndo', () => {
  const fetchLog = vi.mocked(api.fetchDedupActions);
  const revert = vi.mocked(api.revertDedupAction);
  const success = vi.mocked(toast.success);

  // The server log, as the mocked endpoint serves it at each moment.
  let log: Action[] = [];

  beforeEach(() => {
    vi.clearAllMocks();
    log = [];
    fetchLog.mockImplementation(async () => ({ actions: [...log] }) as never);
    revert.mockResolvedValue(undefined as never);
  });

  function undoOf(call: number): () => void {
    const opts = success.mock.calls[call][1] as { action?: { onClick: () => void } } | undefined;
    if (!opts?.action) throw new Error(`toast ${call} offers no Undo`);
    return opts.action.onClick;
  }

  it('undoes its own decision even after a second one landed', async () => {
    // Decision A: snapshot, the server writes batch 100, the toast pins it.
    const beforeA = await snapshotDedupKeys();
    log.push(action({ batchTicks: 100 }));
    await toastWithUndo('Merged A', 'artist-merge', beforeA, () => {});

    // Decision B inside A's toast window writes batch 200.
    const beforeB = await snapshotDedupKeys();
    log.push(action({ batchTicks: 200 }));
    await toastWithUndo('Merged B', 'artist-merge', beforeB, () => {});

    undoOf(0)();
    await vi.waitFor(() => expect(revert).toHaveBeenCalledTimes(1));
    expect(revert).toHaveBeenCalledWith('artist-merge', 100);

    undoOf(1)();
    await vi.waitFor(() => expect(revert).toHaveBeenCalledTimes(2));
    expect(revert).toHaveBeenLastCalledWith('artist-merge', 200);
  });

  it('offers no Undo when the log could not be read before the decision', async () => {
    await toastWithUndo('Merged', 'artist-merge', null, () => {});
    expect(success.mock.calls[0][1]).not.toHaveProperty('action');
    expect(fetchLog).not.toHaveBeenCalled();
  });
});

import { toast } from 'svelte-sonner';
import { fetchDedupActions, revertDedupAction, type DedupAction } from '$lib/api-client';

/**
 * Undo for the Artist names / Album names decisions (merge, credit split, album merge).
 *
 * Those endpoints do not say which batch they wrote, but every one of them lands in the dedup
 * action log that "Recent dedup actions" reverts from. So: remember which batches existed just
 * before the decision, and read the log again the moment the decision returns; the one new batch
 * of the right kind is the one this toast undoes. The ids come from the server's own log, so no
 * client clock is compared with the server's.
 *
 * The batch is pinned when the toast is shown, never when Undo is tapped: by then a second
 * decision may have written its own batch, and Undo on the first toast must not revert that one.
 */

export type DedupSource = 'artist-merge' | 'album-merge' | 'artist-credit-split';

export function dedupKey(action: Pick<DedupAction, 'source' | 'batchTicks'>): string {
  return `${action.source}:${action.batchTicks}`;
}

/**
 * The batch a decision just wrote: of `source`, still revertible, and absent from `before`.
 * Null when there is none, and also when there is more than one (another tab or a background
 * pass wrote at the same moment): guessing could revert a change nobody asked to undo, so the
 * toast then offers no Undo and the history section below stays the way back.
 */
export function findNewAction(
  before: ReadonlySet<string>,
  after: readonly DedupAction[],
  source: DedupSource
): DedupAction | null {
  const fresh = after.filter(
    (a) => a.source === source && a.revertible && !a.reverted && !before.has(dedupKey(a))
  );
  return fresh.length === 1 ? fresh[0] : null;
}

/** The batches in the log right now; null when the log cannot be read (then no Undo is offered). */
export async function snapshotDedupKeys(): Promise<Set<string> | null> {
  try {
    const { actions } = await fetchDedupActions();
    return new Set((actions ?? []).map(dedupKey));
  } catch {
    return null;
  }
}

/** The batch the decision just wrote, read right after it returned; null when unknowable. */
async function resolveNewAction(
  before: Set<string> | null,
  source: DedupSource
): Promise<DedupAction | null> {
  if (!before) return null;
  try {
    const { actions } = await fetchDedupActions();
    return findNewAction(before, actions ?? [], source);
  } catch {
    return null;
  }
}

/**
 * The success toast for a decision, with Undo bound to the exact batch the decision wrote.
 * Awaited inside the decision so the next decision cannot start before its batch is pinned.
 * `onreverted` runs after a successful undo so the queue and the history can reload.
 */
export async function toastWithUndo(
  message: string,
  source: DedupSource,
  before: Set<string> | null,
  onreverted: () => void
): Promise<void> {
  const batch = await resolveNewAction(before, source);
  if (!batch) {
    toast.success(message, { description: 'To undo it, use Revert in Recent dedup actions.' });
    return;
  }
  // Ten seconds, like every Undo toast: time to read it and reach the button.
  toast.success(message, {
    duration: 10_000,
    action: {
      label: 'Undo',
      onClick: () => {
        void (async () => {
          try {
            await revertDedupAction(batch.source, batch.batchTicks);
            toast.success('Undone — the previous tags are back');
            onreverted();
          } catch (err) {
            toast.error(err instanceof Error ? err.message : 'Undo failed');
          }
        })();
      }
    }
  });
}

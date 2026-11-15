/**
 * Storage usage store — the sidebar's storage bar and the breakdown dialog read one snapshot, so
 * they can never disagree about the number. The sidebar loads it once per page load; the dialog
 * re-reads on open and can ask the API to measure again. Measuring walks every managed folder
 * (minutes on a large share) and runs in the API's background, so while `computing` is set the
 * store polls until the snapshot lands (paused while the page is hidden, `#lib/poll`).
 */
import {
  ApiError,
  fetchStorageUsage,
  refreshStorageUsage,
  type StorageUsageResponse,
  type StorageUsageSnapshot
} from '#lib/api-client.ts';
import { poll } from '#lib/poll.ts';

const POLL_INTERVAL_MS = 2_000;

let snapshot = $state<StorageUsageSnapshot | null>(null);
let computing = $state(false);
let error = $state<string | null>(null);
let loaded = $state(false);
let dialogOpen = $state(false);

let inFlight = false;
let stopPoll: (() => void) | null = null;
let followUp: ReturnType<typeof setTimeout> | null = null;

function apply(response: StorageUsageResponse): void {
  snapshot = response.snapshot;
  computing = response.computing;
  error = response.lastError;
  loaded = true;
  syncPoll();
}

/** Poll while a measurement runs; stop once a read says it is done. */
function syncPoll(): void {
  if (computing && typeof window !== 'undefined') {
    stopPoll ??= poll(() => void load(), POLL_INTERVAL_MS);
  } else {
    stopPoll?.();
    stopPoll = null;
  }
}

/** One read a moment from now, for a run that may have finished before its reply arrived. */
function readOnceSoon(): void {
  if (followUp || typeof window === 'undefined') return;
  followUp = setTimeout(() => {
    followUp = null;
    void load();
  }, POLL_INTERVAL_MS);
}

async function load(): Promise<void> {
  if (inFlight) return;
  inFlight = true;
  try {
    apply(await fetchStorageUsage());
  } catch (e) {
    // A transport error ends the poll; the dialog offers a manual retry.
    computing = false;
    syncPoll();
    error = e instanceof Error ? e.message : 'Could not load storage usage.';
    loaded = true;
  } finally {
    inFlight = false;
  }
}

async function refresh(): Promise<void> {
  try {
    apply(await refreshStorageUsage());
    // A small library measures in milliseconds, so the run can already be over by the time the
    // reply lands. One follow-up read catches that instead of waiting for the next open.
    if (!computing) readOnceSoon();
  } catch (e) {
    if (e instanceof ApiError && e.status === 409) {
      // Already measuring — follow that run instead.
      computing = true;
      syncPoll();
      return;
    }
    error = e instanceof Error ? e.message : 'Could not start a measurement.';
  }
}

export const storageUsage = {
  get snapshot() {
    return snapshot;
  },
  get computing() {
    return computing;
  },
  get error() {
    return error;
  },
  get loaded() {
    return loaded;
  },
  get dialogOpen() {
    return dialogOpen;
  },
  set dialogOpen(value: boolean) {
    dialogOpen = value;
  },
  /** First load only. Reads reactive state, so call it under `untrack` from an effect. */
  ensureLoaded(): void {
    if (!loaded && !inFlight) void load();
  },
  reload: load,
  refresh
};

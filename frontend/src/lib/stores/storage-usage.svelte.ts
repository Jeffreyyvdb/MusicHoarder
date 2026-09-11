/**
 * Storage usage store — the sidebar's storage bar and the breakdown dialog read one snapshot, so
 * they can never disagree about the number. The sidebar loads it once per page load; the dialog
 * re-reads on open and can ask the API to measure again. Measuring walks every managed folder
 * (minutes on a large share) and runs in the API's background, so while `computing` is set the
 * store polls until the snapshot lands.
 */
import {
  ApiError,
  fetchStorageUsage,
  refreshStorageUsage,
  type StorageUsageResponse,
  type StorageUsageSnapshot
} from '$lib/api-client';

const POLL_INTERVAL_MS = 2_000;

let snapshot = $state<StorageUsageSnapshot | null>(null);
let computing = $state(false);
let error = $state<string | null>(null);
let loaded = $state(false);
let dialogOpen = $state(false);

let inFlight = false;
let pollHandle: ReturnType<typeof setTimeout> | null = null;

function apply(response: StorageUsageResponse): void {
  snapshot = response.snapshot;
  computing = response.computing;
  error = response.lastError;
  loaded = true;
  schedulePoll();
}

function schedulePoll(once = false): void {
  if (pollHandle) {
    clearTimeout(pollHandle);
    pollHandle = null;
  }
  if ((!computing && !once) || typeof window === 'undefined') return;
  pollHandle = setTimeout(() => {
    pollHandle = null;
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
    if (!computing) schedulePoll(true);
  } catch (e) {
    if (e instanceof ApiError && e.status === 409) {
      // Already measuring — follow that run instead.
      computing = true;
      schedulePoll();
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

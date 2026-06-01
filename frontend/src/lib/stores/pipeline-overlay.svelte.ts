/**
 * Pipeline overlay store — owns the open/closed flag for the bottom-of-screen
 * import-pipeline drawer, plus the live data feeding the drawer:
 *   - `ProgressSnapshot` from the SSE stream at `/api/enrichment/progress`
 *   - `ApiOverview` polled every 5s for `recentActivity[]` + source/destination paths
 *
 * The store is mounted once at the `(app)` layout level. It only opens the SSE
 * connection / polling loop while the drawer is visible OR a job is running,
 * so a closed-drawer idle app stays fully quiet on the wire.
 */

import {
  fetchOverview,
  openProgressStream,
  type ApiOverview,
  type ProgressSnapshot
} from '$lib/api-client';

const STORAGE_KEY = 'mh:pipeline-open';
const POLL_INTERVAL_MS = 5_000;
const RATE_WINDOW_MS = 30_000;

type StageKey = 'scan' | 'fingerprint' | 'enrich' | 'build';

const STAGE_KEYS: readonly StageKey[] = ['scan', 'fingerprint', 'enrich', 'build'] as const;

type RateSample = { t: number; scanned: number; fingerprinted: number; enriched: number; built: number };

function readPersistedOpen(): boolean {
  if (typeof window === 'undefined') return false;
  try {
    return window.localStorage.getItem(STORAGE_KEY) === '1';
  } catch {
    return false;
  }
}

function persistOpen(open: boolean): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(STORAGE_KEY, open ? '1' : '0');
  } catch {
    /* ignore quota / privacy mode errors */
  }
}

let isOpen = $state(false);
let snapshot = $state<ProgressSnapshot | null>(null);
let overview = $state<ApiOverview | null>(null);
let samples = $state<RateSample[]>([]);

let refCount = 0;
// Number of consumers that want the live stream active without opening the
// drawer (e.g. the v2 conveyor home, which must reflect a running job even when
// the bottom drawer is closed). Tracked separately from `refCount`/`isOpen` so
// it never changes the drawer's visual state.
let liveRefCount = 0;
let sseCleanup: (() => void) | null = null;
let sseReconnect: ReturnType<typeof setTimeout> | null = null;
let pollHandle: ReturnType<typeof setInterval> | null = null;
let active = false;
// Guard so the 5s /overview poll never overlaps itself when the API is slow
// (e.g. during a scan). Overlapping requests saturate the same-origin proxy,
// which aborts them at its 10s header-timeout — flooding the console with
// "access control checks" failures for /api/mh/overview.
let overviewInFlight = false;

function pushSample(snap: ProgressSnapshot): void {
  const now = Date.now();
  const sample: RateSample = {
    t: now,
    scanned: snap.scanned,
    fingerprinted: snap.fingerprinted,
    enriched: snap.enriched,
    built: snap.built
  };
  const next = samples.filter((s) => now - s.t <= RATE_WINDOW_MS);
  next.push(sample);
  samples = next;
}

function ratePerSec(key: 'scanned' | 'fingerprinted' | 'enriched' | 'built'): number {
  if (samples.length < 2) return 0;
  const first = samples[0];
  const last = samples[samples.length - 1];
  const dt = (last.t - first.t) / 1000;
  if (dt <= 0) return 0;
  const delta = last[key] - first[key];
  return delta > 0 ? delta / dt : 0;
}

function overallRate(): number {
  return (
    ratePerSec('scanned') +
    ratePerSec('fingerprinted') +
    ratePerSec('enriched') +
    ratePerSec('built')
  );
}

function processedTotal(): number {
  if (!snapshot) return 0;
  return snapshot.scanned + snapshot.fingerprinted + snapshot.enriched + snapshot.built;
}

function remainingTotal(): number {
  if (!snapshot) return 0;
  const target = snapshot.discovered * STAGE_KEYS.length;
  return Math.max(0, target - processedTotal());
}

function etaSecondsValue(): number | null {
  const rate = overallRate();
  if (rate <= 0) return null;
  return Math.round(remainingTotal() / rate);
}

function isStatusRunning(status: string | undefined | null): boolean {
  return status === 'Running';
}

function isAnyRunningFor(snap: ProgressSnapshot | null): boolean {
  if (!snap) return false;
  return (
    isStatusRunning(snap.scan?.status) ||
    isStatusRunning(snap.fingerprint?.status) ||
    isStatusRunning(snap.enrich?.status) ||
    isStatusRunning(snap.build?.status)
  );
}

async function loadOverview(): Promise<void> {
  if (overviewInFlight) return;
  overviewInFlight = true;
  try {
    overview = await fetchOverview();
  } catch {
    /* silently ignore — the drawer falls back to empty state */
  } finally {
    overviewInFlight = false;
  }
}

function startSse(): void {
  if (sseCleanup) return;
  sseCleanup = openProgressStream(
    (snap) => {
      snapshot = snap;
      pushSample(snap);
    },
    () => {
      sseCleanup = null;
      if (!active) return;
      // The server closes the stream when the job completes; reconnect with a
      // small backoff so we pick up the *next* job automatically.
      if (sseReconnect) clearTimeout(sseReconnect);
      sseReconnect = setTimeout(() => {
        sseReconnect = null;
        if (active) startSse();
      }, 2_000);
    }
  );
}

function stopSse(): void {
  if (sseReconnect) {
    clearTimeout(sseReconnect);
    sseReconnect = null;
  }
  if (sseCleanup) {
    sseCleanup();
    sseCleanup = null;
  }
}

function activate(): void {
  if (active) return;
  active = true;
  void loadOverview();
  startSse();
  pollHandle = setInterval(() => void loadOverview(), POLL_INTERVAL_MS);
}

function deactivate(): void {
  if (!active) return;
  active = false;
  if (pollHandle) {
    clearInterval(pollHandle);
    pollHandle = null;
  }
  stopSse();
}

/**
 * Stay active while the drawer is open OR a job is running. Closing the
 * drawer mid-job keeps the SSE alive so the header pulse + counts continue
 * to reflect reality.
 */
function reconcileLifecycle(): void {
  if (refCount <= 0 && liveRefCount <= 0) {
    deactivate();
    return;
  }
  const shouldRun = liveRefCount > 0 || isOpen || isAnyRunningFor(snapshot);
  if (shouldRun) activate();
  else deactivate();
}

// ── Public API ──────────────────────────────────────────────────────────────

function setOpen(value: boolean): void {
  if (isOpen === value) return;
  isOpen = value;
  persistOpen(value);
  reconcileLifecycle();
}

function toggle(): void {
  setOpen(!isOpen);
}

function mount(): () => void {
  if (refCount === 0) {
    isOpen = readPersistedOpen();
  }
  refCount += 1;
  reconcileLifecycle();
  return () => {
    refCount = Math.max(0, refCount - 1);
    reconcileLifecycle();
  };
}

/**
 * Keep the progress stream + overview poll active while mounted, WITHOUT
 * opening the drawer. Used by the v2 conveyor home so its live counts reflect a
 * running job even when the bottom drawer is closed. Returns a cleanup fn.
 */
function keepLive(): () => void {
  liveRefCount += 1;
  reconcileLifecycle();
  return () => {
    liveRefCount = Math.max(0, liveRefCount - 1);
    reconcileLifecycle();
  };
}

export const pipelineOverlay = {
  get isOpen() {
    return isOpen;
  },
  get snapshot() {
    return snapshot;
  },
  get overview() {
    return overview;
  },
  get isAnyRunning() {
    return isAnyRunningFor(snapshot);
  },
  get rates() {
    return {
      scan: ratePerSec('scanned'),
      fingerprint: ratePerSec('fingerprinted'),
      enrich: ratePerSec('enriched'),
      build: ratePerSec('built')
    };
  },
  get overallRate() {
    return overallRate();
  },
  /** Sum of per-stage counters — matches the design's headline "processed" feel. */
  get processed() {
    return processedTotal();
  },
  /** Each file traverses 4 stages — remaining is (discovered * 4) - processed. */
  get remaining() {
    return remainingTotal();
  },
  /** Returns ETA in seconds, or `null` if rate is unknown or zero. */
  get etaSeconds(): number | null {
    return etaSecondsValue();
  },
  setOpen,
  toggle,
  mount,
  keepLive
};

export type PipelineOverlayStore = typeof pipelineOverlay;

// Stale-chunk recovery.
//
// When the server is redeployed (single-instance container is recreated) the
// content-hashed chunks under `_app/immutable/` that an already-open tab still
// references disappear from the new build. The next lazy import — e.g. clicking
// "sign in" which navigates into the client-only `(app)` route group — then
// fails with "Failed to fetch dynamically imported module" / "Importing a
// module script failed" (and, during the brief restart window, a literal 500).
//
// SvelteKit routes *some* of these through `handleError` (hooks.client.ts), but
// the import rejection frequently escapes that lifecycle and surfaces as an
// unhandled promise rejection / Vite `vite:preloadError` event — which is the
// error the user actually sees. There is nothing to recover in-place: the only
// cure is a full reload to pull the fresh build. We retry with backoff so the
// few-second redeploy window self-heals instead of dead-ending on an error.

// Browser phrasings for "a dynamically-imported / preloaded module failed to
// load". Kept deliberately broad so Chrome, Firefox and Safari wording all match.
const STALE_CHUNK_PATTERN =
  /failed to fetch dynamically imported module|error loading dynamically imported module|importing a module script failed|dynamically imported module/i;

export function isStaleChunkError(error: unknown): boolean {
  if (error == null) return false;
  const message =
    typeof error === 'string'
      ? error
      : error instanceof Error
        ? error.message
        : typeof (error as { message?: unknown }).message === 'string'
          ? (error as { message: string }).message
          : '';
  return STALE_CHUNK_PATTERN.test(message);
}

// One redeploy can be unreachable for a few seconds while the container restarts,
// so we reload several times with backoff before giving up — roughly 17.5s of
// patience, enough to ride out a container recreate + healthcheck.
export const BACKOFF_MS = [0, 1_500, 3_000, 5_000, 8_000] as const;
export const MAX_ATTEMPTS = BACKOFF_MS.length;
// A burst of failures within this window is ONE episode (a single deploy). A
// failure after it is treated as a brand-new deploy and starts a fresh episode,
// so the 2nd/3rd/Nth deploy a tab survives still recovers — this is why we key
// off a time window rather than a permanent "already reloaded" flag.
export const EPISODE_WINDOW_MS = 60_000;
// `vite:preloadError` and `unhandledrejection` both fire for the same failure;
// collapse them so one failure only consumes one attempt.
export const DEBOUNCE_MS = 800;

const STATE_KEY = 'mh:stale-chunk-recovery';

export type RecoveryState = { attempts: number; firstAt: number; lastAt: number };

export type RecoveryDecision =
  | { type: 'debounce' } // duplicate event for a failure we're already handling
  | { type: 'reload'; delayMs: number; state: RecoveryState } // schedule a reload
  | { type: 'exhausted' }; // stop auto-reloading; offer a manual reload instead

// Pure decision function (no DOM / storage) so it is unit-testable in node.
export function nextRecoveryStep(prev: RecoveryState | null, now: number): RecoveryDecision {
  let state: RecoveryState =
    prev && typeof prev.attempts === 'number' ? prev : { attempts: 0, firstAt: 0, lastAt: 0 };

  // New episode: first failure ever, or the previous burst aged out.
  if (state.firstAt === 0 || now - state.firstAt > EPISODE_WINDOW_MS) {
    state = { attempts: 0, firstAt: now, lastAt: 0 };
  }

  // Same failure reported twice (preloadError + unhandledrejection) → no-op.
  if (state.lastAt > 0 && now - state.lastAt < DEBOUNCE_MS) {
    return { type: 'debounce' };
  }

  if (state.attempts >= MAX_ATTEMPTS) {
    return { type: 'exhausted' };
  }

  const delayMs = BACKOFF_MS[Math.min(state.attempts, BACKOFF_MS.length - 1)];
  const next: RecoveryState = { attempts: state.attempts + 1, firstAt: state.firstAt, lastAt: now };
  return { type: 'reload', delayMs, state: next };
}

function canRecover(): boolean {
  return (
    typeof window !== 'undefined' &&
    typeof sessionStorage !== 'undefined' &&
    typeof location !== 'undefined'
  );
}

function readState(): RecoveryState | null {
  try {
    const raw = sessionStorage.getItem(STATE_KEY);
    return raw ? (JSON.parse(raw) as RecoveryState) : null;
  } catch {
    return null; // private mode / storage disabled / corrupt value
  }
}

function writeState(state: RecoveryState): void {
  try {
    sessionStorage.setItem(STATE_KEY, JSON.stringify(state));
  } catch {
    // best-effort
  }
}

/** Reset the attempt budget once a navigation has succeeded. */
export function clearStaleChunkRecovery(): void {
  if (!canRecover()) return;
  try {
    sessionStorage.removeItem(STATE_KEY);
  } catch {
    // best-effort
  }
}

/**
 * React to a stale-chunk error: reload (with backoff) to pull the fresh build.
 * Returns true if recovery was initiated / is already in flight (the caller
 * should suppress the error), false once attempts are exhausted (the caller
 * should let the error surface — a manual "Reload" overlay is shown instead).
 */
export function recoverFromStaleChunk(): boolean {
  if (!canRecover()) return false;

  const decision = nextRecoveryStep(readState(), Date.now());
  switch (decision.type) {
    case 'debounce':
      return true;
    case 'exhausted':
      showOverlay('manual');
      return false;
    case 'reload':
      writeState(decision.state);
      if (decision.delayMs > 0) showOverlay('updating');
      setTimeout(() => location.reload(), decision.delayMs);
      return true;
  }
}

let registered = false;

/** Catch stale-chunk failures that escape SvelteKit's `handleError` hook. */
export function registerStaleChunkRecovery(): void {
  if (registered || typeof window === 'undefined') return;
  registered = true;

  // Vite's first-class signal that a (pre)loaded dynamic import failed.
  // (Not in the default DOM lib types, so it resolves via the string overload.)
  window.addEventListener('vite:preloadError', (event: Event) => {
    if (recoverFromStaleChunk()) event.preventDefault();
  });

  // Backstop: the failure most often arrives as an unhandled promise rejection
  // (this is the Safari "Unhandled Promise Rejection: ... Importing a module
  // script failed. 500" the user reported).
  window.addEventListener('unhandledrejection', (event) => {
    if (isStaleChunkError(event.reason) && recoverFromStaleChunk()) {
      event.preventDefault();
    }
  });
}

// --- Minimal, framework-independent overlay -------------------------------
// Injected directly into the DOM (rather than a Svelte component) so it works
// even when the failure happened outside Svelte's render lifecycle. Inline
// styles only, so it never depends on an app stylesheet chunk that may itself
// be stale.

const OVERLAY_ID = 'mh-stale-chunk-overlay';

function ensureOverlay(): HTMLElement | null {
  if (typeof document === 'undefined' || !document.body) return null;
  let el = document.getElementById(OVERLAY_ID);
  if (!el) {
    el = document.createElement('div');
    el.id = OVERLAY_ID;
    el.setAttribute('role', 'status');
    el.setAttribute('aria-live', 'polite');
    el.style.cssText = [
      'position:fixed',
      'inset:0',
      'z-index:2147483647',
      'display:flex',
      'flex-direction:column',
      'align-items:center',
      'justify-content:center',
      'gap:16px',
      'padding:24px',
      'text-align:center',
      'background:rgba(9,9,11,0.92)',
      'color:#fafafa',
      'font:500 15px/1.5 system-ui,-apple-system,"Segoe UI",Roboto,sans-serif',
      'backdrop-filter:blur(4px)'
    ].join(';');
    document.body.appendChild(el);
  }
  return el;
}

function showOverlay(kind: 'updating' | 'manual'): void {
  const el = ensureOverlay();
  if (!el) return;

  if (kind === 'updating') {
    el.innerHTML =
      '<div style="width:28px;height:28px;border:3px solid rgba(250,250,250,0.25);' +
      'border-top-color:#fafafa;border-radius:50%;animation:mh-spin 0.8s linear infinite"></div>' +
      '<div>Updating to the latest version…</div>' +
      '<style>@keyframes mh-spin{to{transform:rotate(360deg)}}</style>';
    return;
  }

  el.innerHTML =
    '<div style="font-size:17px;font-weight:600">A new version is available</div>' +
    '<div style="opacity:0.8;max-width:320px">Reload to get the latest build.</div>' +
    '<button id="mh-stale-chunk-reload" type="button" style="margin-top:4px;padding:10px 22px;' +
    'border:0;border-radius:8px;background:#fafafa;color:#09090b;font:inherit;font-weight:600;' +
    'cursor:pointer">Reload</button>';

  document.getElementById('mh-stale-chunk-reload')?.addEventListener('click', () => {
    clearStaleChunkRecovery();
    location.reload();
  });
}

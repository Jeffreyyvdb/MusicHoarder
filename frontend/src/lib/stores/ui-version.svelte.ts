/**
 * UI-version store — owns the v1/v2 design toggle.
 *
 * The redesign ("v2") is delivered as an in-place shell swap behind this flag.
 * v1 stays the default and fully working until v2 reaches parity; flipping the
 * flag re-renders the `(app)` chrome (and, in later phases, individual page
 * bodies) without changing the route set.
 *
 * Persistence is per-browser via `localStorage['mh:ui-version']`, mirroring the
 * try/catch pattern in `pipeline-overlay.svelte.ts` so private-mode / quota
 * errors degrade gracefully to the default.
 */

const STORAGE_KEY = 'mh:ui-version';

export type UiVersion = 'v1' | 'v2';

const DEFAULT_VERSION: UiVersion = 'v1';

function readPersisted(): UiVersion {
  if (typeof window === 'undefined') return DEFAULT_VERSION;
  try {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return raw === 'v2' ? 'v2' : 'v1';
  } catch {
    return DEFAULT_VERSION;
  }
}

function persist(version: UiVersion): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(STORAGE_KEY, version);
  } catch {
    /* ignore quota / privacy mode errors */
  }
}

// Start from the persisted value. The `(app)` group is `ssr = false`, so this
// module only evaluates in the browser where localStorage is available.
let version = $state<UiVersion>(readPersisted());

function setVersion(next: UiVersion): void {
  if (version === next) return;
  version = next;
  persist(next);
}

function toggle(): void {
  setVersion(version === 'v2' ? 'v1' : 'v2');
}

export const uiVersion = {
  get current(): UiVersion {
    return version;
  },
  get isV2(): boolean {
    return version === 'v2';
  },
  setVersion,
  toggle
};

export type UiVersionStore = typeof uiVersion;

/**
 * Video-backdrop preference — whether the full-screen player replaces the blurred
 * ambient artwork with the song's muted music video (when one is attached). Default
 * ON: the costly gate is server-side (whether a clip was downloaded at all); showing
 * an already-downloaded video is cheap. Module-level reactive state mirrored to
 * localStorage (`mh:` prefix convention, see album-view-prefs).
 */

const STORAGE_KEY = 'mh:video-backdrop';

function readPersisted(): boolean {
  if (typeof window === 'undefined') return true;
  try {
    return window.localStorage.getItem(STORAGE_KEY) !== '0';
  } catch {
    return true;
  }
}

function persist(value: boolean): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage.setItem(STORAGE_KEY, value ? '1' : '0');
  } catch {
    /* ignore quota / privacy mode errors */
  }
}

let enabled = $state(readPersisted());

export const videoBackdropPrefs = {
  get enabled() {
    return enabled;
  },
  setEnabled(value: boolean) {
    if (enabled === value) return;
    enabled = value;
    persist(value);
  },
  toggle() {
    this.setEnabled(!enabled);
  }
};

export type VideoBackdropPrefs = typeof videoBackdropPrefs;

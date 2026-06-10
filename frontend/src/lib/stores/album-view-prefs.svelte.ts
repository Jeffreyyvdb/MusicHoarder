/**
 * Album-view preferences — cross-album UI prefs for the album detail page that
 * should "stick" as the user browses from one album to the next (and survive a
 * reload), rather than resetting per album.
 *
 * Currently just `hideMissing`: when true, the tracklist collapses to the songs
 * the user owns, hiding the greyed-out canonical tracks they're missing. The
 * value is module-level reactive state so every AlbumPage instance shares it,
 * and mirrored to localStorage (`mh:` prefix convention, see pipeline-overlay).
 */

const STORAGE_KEY = 'mh:album-hide-missing';

function readPersisted(): boolean {
  if (typeof window === 'undefined') return false;
  try {
    return window.localStorage.getItem(STORAGE_KEY) === '1';
  } catch {
    return false;
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

let hideMissing = $state(readPersisted());

export const albumViewPrefs = {
  get hideMissing() {
    return hideMissing;
  },
  setHideMissing(value: boolean) {
    if (hideMissing === value) return;
    hideMissing = value;
    persist(value);
  },
  toggleHideMissing() {
    this.setHideMissing(!hideMissing);
  }
};

export type AlbumViewPrefs = typeof albumViewPrefs;

/**
 * Lightweight breadcrumb store — `/library` populates the current album title and
 * artist when an album is open; the toolbar reads it for the breadcrumb trail.
 * Reset to null when the page unmounts or no album is open.
 */

type Crumb = { artist: string; title: string } | null;

let currentAlbum = $state<Crumb>(null);

export const breadcrumbStore = {
  get currentAlbum() {
    return currentAlbum;
  },
  setAlbum(value: Crumb) {
    currentAlbum = value;
  },
  clear() {
    currentAlbum = null;
  }
};

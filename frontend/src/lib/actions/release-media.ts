/**
 * `use:releaseMedia` — stops a media element's download when it leaves the page.
 *
 * Taking a `<video>` out of the document pauses it but does not end its load: it goes on fetching
 * its source until it is garbage-collected. For a music video on a slow link that is the rest of
 * the file, at the full speed of the link, after Now Playing has closed (#739), and in the app
 * shell that is the link the native player streams the song over. Dropping the source and loading
 * again is how the HTML spec has a page abort a media download.
 */
export function releaseMedia(el: HTMLMediaElement) {
  return {
    destroy() {
      el.removeAttribute('src');
      el.load();
    }
  };
}

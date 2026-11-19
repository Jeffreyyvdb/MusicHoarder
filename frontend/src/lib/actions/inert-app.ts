/**
 * Make the app behind a full-screen modal inert while it is open.
 *
 * Now Playing, the bottom sheets and the command palette are bits-ui dialogs: a focus trap plus
 * `aria-modal="true"`. WebKit honours aria-modal, but Chrome with TalkBack (and older WebKit) still
 * let a screen-reader swipe walk out of the dialog into the track list behind a full-screen Now
 * Playing. `inert` on the app root closes that door for every assistive technology. The dialogs
 * are portaled to <body>, outside the root, so they stay live; so do the toasts.
 *
 * The root is the shell's Sidebar.Provider wrapper, which holds the page, the tab bar and the mini
 * player. Holds are counted, so a sheet opened over Now Playing and then closed does not wake the
 * app while Now Playing is still up. Returns the release, for an `$effect` to return.
 */

let holds = 0;

function root(): Element | null {
  if (typeof document === 'undefined') return null;
  return document.querySelector('[data-slot="sidebar-wrapper"]');
}

export function holdAppInert(): () => void {
  const el = root();
  if (!el) return () => {};
  holds += 1;
  el.setAttribute('inert', '');
  let released = false;
  return () => {
    if (released) return;
    released = true;
    holds = Math.max(0, holds - 1);
    if (holds === 0) root()?.removeAttribute('inert');
  };
}

let hidden = 0;

/**
 * `aria-hidden` on the app root: the assistive-technology half of `inert`, for the moments an
 * overlay is up but `inert` would cost too much. `inert` changes the computed style of every
 * element in the app, so setting it while Now Playing slides in drops the slide's frames;
 * `aria-hidden` matches no style rule and costs nothing. Counted like `holdAppInert`.
 */
export function holdAppHidden(): () => void {
  const el = root();
  if (!el) return () => {};
  hidden += 1;
  el.setAttribute('aria-hidden', 'true');
  let released = false;
  return () => {
    if (released) return;
    released = true;
    hidden = Math.max(0, hidden - 1);
    if (hidden === 0) root()?.removeAttribute('aria-hidden');
  };
}

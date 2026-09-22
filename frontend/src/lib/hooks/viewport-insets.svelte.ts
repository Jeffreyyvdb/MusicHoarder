// Publishes the bottom inset of the *visual* viewport (browser bottom chrome
// such as Chrome Android's bottom address bar, plus the on-screen keyboard) as
// the `--mh-vv-bottom` CSS variable on <html>. Floating bottom-anchored chrome
// (BottomNavV2, MiniPlayer) offsets by this so it never hides behind the
// browser's bottom bar. `env(safe-area-inset-bottom)` does NOT cover the
// browser bottom chrome, which is why we need the VisualViewport API here.
//
// An installed (home-screen) app is deliberately excluded: there is no browser
// chrome to dodge there, `env(safe-area-inset-bottom)` alone puts the floating
// chrome where it belongs, and the on-screen keyboard covering the nav is what a
// native tab bar does too — dodging it would slide the nav over the content.
//
// Returns a cleanup that removes the listeners and the published property. Until
// this runs (SSR / before hydration) the variable is unset and consumers fall
// back to `var(--mh-vv-bottom, 0px)` → existing `env()`-only behaviour.
export function installBottomInsetTracker(): () => void {
  if (typeof window === 'undefined' || !window.visualViewport || isInstalledApp()) {
    return () => {};
  }

  const vv = window.visualViewport;
  const root = document.documentElement;

  const update = (): void => {
    // Space between the layout-viewport bottom and the visible (visual) viewport
    // bottom = browser bottom chrome + keyboard. Clamp to >= 0.
    const bottom = Math.max(0, window.innerHeight - vv.height - vv.offsetTop);
    root.style.setProperty('--mh-vv-bottom', `${bottom}px`);
  };

  update();
  vv.addEventListener('resize', update);
  vv.addEventListener('scroll', update);
  window.addEventListener('resize', update);

  return () => {
    vv.removeEventListener('resize', update);
    vv.removeEventListener('scroll', update);
    window.removeEventListener('resize', update);
    root.style.removeProperty('--mh-vv-bottom');
  };
}

/**
 * True when the page runs as an installed (home-screen) app rather than in a browser tab.
 *
 * `display-mode` is the standard signal and matches what the manifest asked for (`standalone`;
 * `fullscreen` has no browser chrome either). `navigator.standalone` is the iOS-only flag that
 * predates the manifest and is still what older iOS versions set.
 */
export function isInstalledApp(): boolean {
  if (typeof window === 'undefined') return false;
  if ((navigator as Navigator & { standalone?: boolean }).standalone === true) return true;
  return (
    window.matchMedia?.('(display-mode: standalone), (display-mode: fullscreen)').matches ?? false
  );
}

// Publishes the bottom inset of the *visual* viewport (browser bottom chrome
// such as Chrome Android's bottom address bar, plus the on-screen keyboard) as
// the `--mh-vv-bottom` CSS variable on <html>. Floating bottom-anchored chrome
// (BottomNavV2, MiniPlayer) offsets by this so it never hides behind the
// browser's bottom bar. `env(safe-area-inset-bottom)` does NOT cover the
// browser bottom chrome, which is why we need the VisualViewport API here.
//
// Returns a cleanup that removes the listeners and the published property. Until
// this runs (SSR / before hydration) the variable is unset and consumers fall
// back to `var(--mh-vv-bottom, 0px)` → existing `env()`-only behaviour.
export function installBottomInsetTracker(): () => void {
  if (typeof window === 'undefined' || !window.visualViewport) {
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

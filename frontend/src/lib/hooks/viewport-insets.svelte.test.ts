import { afterEach, describe, expect, it, vi } from 'vitest';
import { installBottomInsetTracker, isInstalledApp } from './viewport-insets.svelte';

/**
 * The tracker exists for browser bottom chrome (Chrome Android's address bar). Installed as a
 * home-screen app there is none, so it must publish nothing and leave `env(safe-area-inset-bottom)`
 * in charge — whatever the visual viewport happens to report. These pin that the browser-tab path
 * still measures and that both installed-app signals make it a no-op.
 */
function stubBrowser({
  innerHeight,
  visualHeight,
  offsetTop = 0,
  standaloneMedia = false,
  iosStandalone
}: {
  innerHeight: number;
  visualHeight: number;
  offsetTop?: number;
  standaloneMedia?: boolean;
  iosStandalone?: boolean;
}) {
  const vv = Object.assign(new EventTarget(), { height: visualHeight, offsetTop });
  const setProperty = vi.fn();
  const removeProperty = vi.fn();
  const matchMedia = vi.fn((query: string) => ({
    matches: standaloneMedia && query.includes('display-mode'),
    media: query
  }));
  const win = Object.assign(new EventTarget(), { innerHeight, visualViewport: vv, matchMedia });
  vi.stubGlobal('window', win);
  vi.stubGlobal('document', { documentElement: { style: { setProperty, removeProperty } } });
  vi.stubGlobal('navigator', iosStandalone === undefined ? {} : { standalone: iosStandalone });
  return { vv, win, setProperty, removeProperty, matchMedia };
}

afterEach(() => vi.unstubAllGlobals());

describe('installBottomInsetTracker', () => {
  it('publishes the browser bottom chrome in a browser tab and clears it on cleanup', () => {
    // 852px layout viewport, 796px visible: a 56px bottom address bar.
    const { vv, setProperty, removeProperty } = stubBrowser({
      innerHeight: 852,
      visualHeight: 796
    });

    const cleanup = installBottomInsetTracker();
    expect(setProperty).toHaveBeenLastCalledWith('--mh-vv-bottom', '56px');

    vv.height = 852;
    vv.dispatchEvent(new Event('resize'));
    expect(setProperty).toHaveBeenLastCalledWith('--mh-vv-bottom', '0px');

    cleanup();
    expect(removeProperty).toHaveBeenCalledWith('--mh-vv-bottom');
  });

  it('is a no-op in an installed app (display-mode: standalone)', () => {
    // A visual viewport shorter than the layout viewport must still publish nothing here: an
    // installed app has no browser chrome, whatever the difference is.
    const { vv, setProperty, removeProperty } = stubBrowser({
      innerHeight: 852,
      visualHeight: 759,
      standaloneMedia: true
    });

    const cleanup = installBottomInsetTracker();
    vv.dispatchEvent(new Event('resize'));
    expect(setProperty).not.toHaveBeenCalled();

    cleanup();
    expect(removeProperty).not.toHaveBeenCalled();
  });

  it('is a no-op when iOS sets navigator.standalone', () => {
    const { setProperty } = stubBrowser({
      innerHeight: 852,
      visualHeight: 759,
      iosStandalone: true
    });

    installBottomInsetTracker();
    expect(setProperty).not.toHaveBeenCalled();
  });
});

describe('isInstalledApp', () => {
  it('reads the display-mode media query', () => {
    const { matchMedia } = stubBrowser({ innerHeight: 1, visualHeight: 1, standaloneMedia: true });
    expect(isInstalledApp()).toBe(true);
    expect(matchMedia).toHaveBeenCalledWith(
      '(display-mode: standalone), (display-mode: fullscreen)'
    );
  });

  it('is false in a browser tab, including when navigator.standalone is explicitly false', () => {
    stubBrowser({ innerHeight: 1, visualHeight: 1, iosStandalone: false });
    expect(isInstalledApp()).toBe(false);
  });
});

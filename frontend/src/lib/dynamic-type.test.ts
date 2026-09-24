import { afterEach, describe, expect, it, vi } from 'vitest';
import { DYNAMIC_TYPE_CHANGE, installDynamicType, scaleForBodySize } from './dynamic-type';

/**
 * The iOS text styles scale by --mh-dt, the ratio of the system body size to the default 17pt.
 * These pin the clamp (so an extreme Text Size cannot blow the fixed-height chrome apart), that it
 * does nothing off iOS or in a non-WebKit engine claiming to be iOS, and that a change made while
 * the app was in the background is picked up when it becomes visible again.
 */

const IPHONE_UA =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148';

function stubIos({ bodyPx, supportsFont = true }: { bodyPx: number; supportsFont?: boolean }) {
  const setProperty = vi.fn();
  const removeProperty = vi.fn();
  // A style that drops the system font keyword unless the engine "supports" it, as a CSSOM does.
  let font = '';
  const style = {
    cssText: '',
    get font() {
      return font;
    },
    set font(v: string) {
      if (supportsFont) font = v;
    }
  };
  const probe = { setAttribute: vi.fn(), style, textContent: '', remove: vi.fn() };
  const doc = Object.assign(new EventTarget(), {
    visibilityState: 'visible',
    documentElement: { style: { setProperty, removeProperty } },
    body: { appendChild: vi.fn() },
    createElement: vi.fn(() => probe)
  });
  const win = new EventTarget();
  const dispatch = vi.spyOn(win, 'dispatchEvent');
  let size = bodyPx;
  vi.stubGlobal('window', win);
  vi.stubGlobal('document', doc);
  vi.stubGlobal('navigator', { userAgent: IPHONE_UA, maxTouchPoints: 5 });
  vi.stubGlobal(
    'getComputedStyle',
    vi.fn((el: unknown) =>
      el === probe ? { fontSize: `${size}px` } : { getPropertyValue: () => '' }
    )
  );
  vi.stubGlobal(
    'CustomEvent',
    class extends Event {
      detail: unknown;
      constructor(type: string, init?: { detail?: unknown }) {
        super(type);
        this.detail = init?.detail;
      }
    }
  );
  return {
    doc,
    setProperty,
    removeProperty,
    probe,
    dispatch,
    setSize: (px: number) => (size = px)
  };
}

afterEach(() => vi.unstubAllGlobals());

describe('scaleForBodySize', () => {
  it('is the ratio to the 17pt default body', () => {
    expect(scaleForBodySize(17)).toBe(1);
    expect(scaleForBodySize(19)).toBe(1.118);
  });

  it('clamps to the range the chrome can take', () => {
    expect(scaleForBodySize(12)).toBe(0.82);
    expect(scaleForBodySize(53)).toBe(1.5);
  });

  it('falls back to 1 for an unreadable size', () => {
    expect(scaleForBodySize(NaN)).toBe(1);
    expect(scaleForBodySize(0)).toBe(1);
  });
});

describe('installDynamicType', () => {
  it('publishes the scale on iOS and clears it on cleanup', () => {
    const { setProperty, removeProperty, probe } = stubIos({ bodyPx: 21 });
    const cleanup = installDynamicType();
    expect(setProperty).toHaveBeenCalledWith('--mh-dt', '1.235');
    cleanup();
    expect(removeProperty).toHaveBeenCalledWith('--mh-dt');
    expect(probe.remove).toHaveBeenCalled();
  });

  it('re-measures when the app comes back to the foreground', () => {
    const { doc, setProperty, dispatch, setSize } = stubIos({ bodyPx: 17 });
    installDynamicType();
    setSize(23);
    doc.dispatchEvent(new Event('visibilitychange'));
    expect(setProperty).toHaveBeenLastCalledWith('--mh-dt', '1.353');
    const event = dispatch.mock.calls.map(([e]) => e).find((e) => e.type === DYNAMIC_TYPE_CHANGE);
    expect((event as Event & { detail?: unknown }).detail).toBe(1.353);
  });

  it('does nothing in an engine without the system font keywords', () => {
    const { setProperty } = stubIos({ bodyPx: 16, supportsFont: false });
    installDynamicType()();
    expect(setProperty).not.toHaveBeenCalled();
  });

  it('does nothing off iOS', () => {
    const { setProperty } = stubIos({ bodyPx: 21 });
    vi.stubGlobal('navigator', { userAgent: 'Mozilla/5.0 (X11; Linux x86_64)', maxTouchPoints: 0 });
    installDynamicType()();
    expect(setProperty).not.toHaveBeenCalled();
  });
});

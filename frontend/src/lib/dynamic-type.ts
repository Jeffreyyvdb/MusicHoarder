import { isIosDevice } from '$lib/ios-safari';

/**
 * Dynamic Type for the installed iOS app.
 *
 * The iOS text-style utilities in app.css (`text-body`, `text-title-2`, …) are sized as
 * `calc(<px> * var(--mh-dt, 1))`. Pixel type ignores the iPhone's Settings → Display & Brightness
 * → Text Size, and a Home Screen web app has no page-zoom control to make up for it, so this
 * measures the one thing WebKit does scale with that setting — the system `-apple-system-body`
 * font — and publishes the ratio to the default 17pt body as `--mh-dt` on <html>. Spacing stays px,
 * so text grows without the layout zooming. Clamped to 0.82–1.5: the smallest Text Size step and
 * the largest the fixed-height chrome can take before it clips (the accessibility sizes beyond it
 * would need reflowed rows).
 *
 * The setting can change while the app is backgrounded, so it is measured again whenever the page
 * becomes visible. Anything that derives a fixed height from the scale (TrackList's row height)
 * reads {@link dynamicTypeScale} and listens for {@link DYNAMIC_TYPE_CHANGE}.
 *
 * iOS only: elsewhere `--mh-dt` stays at its CSS default of 1 (desktop and Android have their own
 * zoom). app.html runs the same measurement before first paint so the app does not open at the
 * default size and then jump; this keeps it current and gives consumers the change event.
 */

/** Fired on `window` (detail: the new scale) when the measured Text Size changes. */
export const DYNAMIC_TYPE_CHANGE = 'mh:dynamic-type-change';

const MIN_SCALE = 0.82;
const MAX_SCALE = 1.5;
const BODY_PX = 17;

/** The current scale (1 on the server, off iOS, or before it has been measured). */
export function dynamicTypeScale(): number {
  if (typeof document === 'undefined') return 1;
  const raw = getComputedStyle(document.documentElement).getPropertyValue('--mh-dt');
  const scale = parseFloat(raw);
  return Number.isFinite(scale) && scale > 0 ? scale : 1;
}

/** Clamp a measured body size (px) to the scale app.css multiplies by. Exported for tests. */
export function scaleForBodySize(px: number): number {
  if (!Number.isFinite(px) || px <= 0) return 1;
  const scale = Math.min(MAX_SCALE, Math.max(MIN_SCALE, px / BODY_PX));
  return Math.round(scale * 1000) / 1000;
}

/**
 * Start tracking the Text Size setting. Returns the cleanup (removes the probe, the listener and
 * the published property). A no-op, returning a no-op, on the server and off iOS.
 */
export function installDynamicType(): () => void {
  if (typeof window === 'undefined' || typeof document === 'undefined' || !isIosDevice()) {
    return () => {};
  }
  const root = document.documentElement;
  // A hidden probe in the system body style. Absolutely positioned and invisible, so it never
  // affects layout or reaches assistive tech.
  const probe = document.createElement('span');
  probe.setAttribute('aria-hidden', 'true');
  probe.textContent = 'M';
  probe.style.cssText =
    'position:absolute;top:0;left:-9999px;visibility:hidden;pointer-events:none;';
  probe.style.font = '-apple-system-body';
  // Every iOS browser is WebKit, which parses the system font keywords; an engine that drops the
  // declaration (a desktop emulator claiming an iOS user agent) would measure its own default size
  // and shrink the text, so leave the scale at 1 there.
  if (!probe.style.font) return () => {};
  document.body.appendChild(probe);

  let current = dynamicTypeScale();
  const measure = (): void => {
    const next = scaleForBodySize(parseFloat(getComputedStyle(probe).fontSize));
    root.style.setProperty('--mh-dt', String(next));
    if (next !== current) {
      current = next;
      window.dispatchEvent(new CustomEvent(DYNAMIC_TYPE_CHANGE, { detail: next }));
    }
  };
  const onVisibility = (): void => {
    if (document.visibilityState === 'visible') measure();
  };

  measure();
  document.addEventListener('visibilitychange', onVisibility);

  return () => {
    document.removeEventListener('visibilitychange', onVisibility);
    probe.remove();
    root.style.removeProperty('--mh-dt');
  };
}

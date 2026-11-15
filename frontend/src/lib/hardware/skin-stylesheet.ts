/**
 * The hardware skin's stylesheet (`app-skin.css`, ~44 KB) is its own file, requested only when the
 * skin is on, so nobody who never finds the easter egg downloads or parses it.
 *
 * Two places ask for it, both by the same URL:
 *  - app.html's first-paint script writes a render-blocking `<link>` when `mh:hardware` is `on`, so a
 *    reload paints the skin from the first frame. The server fills its `href` in
 *    (`hooks.server.ts`, {@link SKIN_STYLESHEET_PLACEHOLDER}), because the file name is hashed.
 *  - Turning the skin on in a running page ({@link ensureSkinStylesheet}, from hardware-mode). Until
 *    it lands the page simply stays plain: every rule in it hangs off the attribute, and without the
 *    rules the attribute styles nothing.
 *
 * Once in, it stays in, like the device's vendored stylesheets; with the attribute gone nothing in
 * it applies.
 */
import href from './app-skin.css?url';

export const SKIN_STYLESHEET_HREF: string = href;

/** Replaced in app.html by the server with {@link SKIN_STYLESHEET_HREF}. */
export const SKIN_STYLESHEET_PLACEHOLDER = '%mh.skinStylesheet%';

/** Marks the `<link>`, whichever of the two added it. */
const MARKER = 'data-mh-skin-css';

/** Add the skin's stylesheet to the document unless it is there already. */
export function ensureSkinStylesheet(): void {
  if (typeof document === 'undefined') return;
  if (document.querySelector(`link[${MARKER}]`)) return;
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = SKIN_STYLESHEET_HREF;
  link.setAttribute(MARKER, '');
  document.head.appendChild(link);
}

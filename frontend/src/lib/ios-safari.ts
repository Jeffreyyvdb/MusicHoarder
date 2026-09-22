/**
 * Is this Safari on an iPhone, iPod or iPad?
 *
 * It matters because an iOS Home Screen web app does not share cookies with Safari: signing in
 * one leaves the other signed out, and an emailed sign-in link always opens in Safari. The install
 * hint in Settings and the magic-link callback both explain that, and only to this audience.
 *
 * Every iOS browser is WebKit and carries "Safari/" in its user agent, so the others are ruled out
 * by their own tokens; in-app web views (Facebook, Instagram…) drop Safari's "Version/…" pair.
 * Kept free of browser globals so the server-side callback can use it on a request header.
 */
const OTHER_IOS_BROWSERS = /CriOS|FxiOS|EdgiOS|OPiOS|OPT\/|GSA\/|DuckDuckGo|Ddg\/|YaBrowser|Brave/;
const SAFARI_VERSION = /Version\/[\d.]+.*Safari\//;

export function isIosSafariUserAgent(userAgent: string | null | undefined): boolean {
  if (!userAgent) return false;
  if (!/\b(iPhone|iPad|iPod)\b/.test(userAgent)) return false;
  return SAFARI_VERSION.test(userAgent) && !OTHER_IOS_BROWSERS.test(userAgent);
}

/**
 * Client side: any iPhone, iPod or iPad, whatever the browser — including the installed Home
 * Screen app, whose user agent drops Safari's "Version/…Safari/" pair. iPadOS asks for desktop
 * sites by default and sends a Macintosh user agent, which no header can tell apart from a Mac —
 * but a Mac reports no touch points.
 */
export function isIosDevice(): boolean {
  if (typeof navigator === 'undefined') return false;
  const ua = navigator.userAgent ?? '';
  if (/\b(iPhone|iPad|iPod)\b/.test(ua)) return true;
  return /\bMacintosh\b/.test(ua) && (navigator.maxTouchPoints ?? 0) > 1;
}

/** Client side: Safari itself on an iOS/iPadOS device (not the installed app, not Chrome). */
export function isIosSafari(): boolean {
  if (!isIosDevice()) return false;
  const ua = navigator.userAgent ?? '';
  return SAFARI_VERSION.test(ua) && !OTHER_IOS_BROWSERS.test(ua);
}

/**
 * Enter-to-submit that is safe for an input method editor (IME).
 *
 * Japanese, Chinese and Korean keyboards use Return to confirm the text being composed. A field
 * that submits on Enter must ignore that keystroke, or it submits half-typed text. `isComposing`
 * says so in Chromium and Firefox, but Safari has already reset it to false when the confirming
 * keydown arrives (WebKit bug 165004). Every browser reports `keyCode` 229 for a keystroke the IME
 * handled, Safari included, so both are checked. (`keyCode` is deprecated, but it is the only
 * signal Safari gives.)
 */

/** True when the keystroke belongs to an IME composition. */
export function isImeKeystroke(event: KeyboardEvent): boolean {
  return event.isComposing || event.keyCode === 229;
}

/** True for an Enter that should act (submit, move on): Enter, and not an IME confirmation. */
export function isSubmitEnter(event: KeyboardEvent): boolean {
  return event.key === 'Enter' && !isImeKeystroke(event);
}

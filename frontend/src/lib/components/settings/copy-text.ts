/**
 * Put text on the clipboard and say whether it worked, so a copy button can own up when it did
 * not rather than doing nothing.
 *
 * `navigator.clipboard` only exists in a secure context, and a self-hosted server is often reached
 * over plain http on the LAN — where it is undefined and a bare `writeText` throws a TypeError, so
 * the tap silently does nothing. The legacy `execCommand('copy')` path still works there (a tap is
 * the user activation it needs), so it is the fallback before giving up.
 */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (typeof navigator !== 'undefined' && navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch {
    // Denied (a permissions policy, an embedding): try the legacy path.
  }
  return legacyCopy(text);
}

function legacyCopy(text: string): boolean {
  if (typeof document === 'undefined' || !document.body) return false;
  const area = document.createElement('textarea');
  area.value = text;
  area.setAttribute('readonly', '');
  // Off to the side and 16px, so iOS neither scrolls to it nor zooms on the brief focus.
  area.style.cssText = 'position:fixed;top:0;left:-9999px;font-size:16px;opacity:0;';
  const previous = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  document.body.appendChild(area);
  let ok = false;
  try {
    area.select();
    area.setSelectionRange(0, text.length);
    ok = document.execCommand('copy');
  } catch {
    ok = false;
  } finally {
    area.remove();
    // Focus goes back to the copy button, where the person's keyboard or VoiceOver cursor was.
    previous?.focus({ preventScroll: true });
  }
  return ok;
}

/** What a failed copy tells the person: the text is on screen and selectable, so they can. */
export const COPY_FAILED_MESSAGE = 'Couldn’t copy — select the text and copy it instead.';

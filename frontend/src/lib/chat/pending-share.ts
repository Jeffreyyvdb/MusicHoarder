/**
 * What the share sheet handed the installed app (the manifest's `share_target`), kept until it has
 * been sent or dismissed. It lands on the public `/share-target` page, which stores it here before
 * going on to `/chats/share` — so a share made while signed out survives the trip through sign-in:
 * the app shell brings it back up once someone is signed in.
 */

export type PendingShare = { title: string | null; text: string | null; url: string | null };

const KEY = 'mh:pending-share';

/** A share older than this was abandoned; it is not brought back. */
export const PENDING_SHARE_TTL_MS = 60 * 60 * 1000;

function clean(value: string | null | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed.slice(0, 4000) : null;
}

export function pendingShareFrom(params: URLSearchParams): PendingShare | null {
  const share = { title: clean(params.get('title')), text: clean(params.get('text')), url: clean(params.get('url')) };
  return share.title || share.text || share.url ? share : null;
}

export function toSearchParams(share: PendingShare): URLSearchParams {
  const params = new URLSearchParams();
  if (share.title) params.set('title', share.title);
  if (share.text) params.set('text', share.text);
  if (share.url) params.set('url', share.url);
  return params;
}

export function savePendingShare(share: PendingShare, now = Date.now()): void {
  try {
    localStorage.setItem(KEY, JSON.stringify({ ...share, savedAt: now }));
  } catch {
    // Not kept; the URL still carries it for the usual (signed-in) case.
  }
}

export function readPendingShare(now = Date.now()): PendingShare | null {
  try {
    const raw = localStorage.getItem(KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<PendingShare> & { savedAt?: number };
    if (typeof parsed.savedAt !== 'number' || now - parsed.savedAt > PENDING_SHARE_TTL_MS) {
      localStorage.removeItem(KEY);
      return null;
    }
    const share = { title: clean(parsed.title), text: clean(parsed.text), url: clean(parsed.url) };
    return share.title || share.text || share.url ? share : null;
  } catch {
    return null;
  }
}

export function clearPendingShare(): void {
  try {
    localStorage.removeItem(KEY);
  } catch {
    // nothing to clear
  }
}

import { toast } from 'svelte-sonner';
import { createSongShare, listSongShares, shareUrl, type SongShareView } from '$lib/api-client';

/**
 * Public share links for every "Share link…" item: a song row's menu (TrackRowMenu), the album
 * page's ⋯ and Now Playing's ⋯ — one label in all three, since the action makes a link.
 *
 * Two constraints pull against each other. `navigator.share` needs a live user activation, and iOS
 * spends it across a network round trip — so a link minted inside the tap can no longer open the
 * share sheet. The first answer was to mint when the menu OPENED; but a link is a public,
 * unauthenticated stream of the song, and that minted one every time an admin opened a menu to go
 * to an album or read the song info, with nothing in the app that lists or revokes them.
 *
 * So opening a menu is read-only now: it looks up a link that already exists (`findShareLink`), and
 * a link is only ever minted when Share link… is picked (`shareLink`). When the pick has to mint, the
 * tap's activation goes to the clipboard instead — a ClipboardItem whose content is the pending
 * mint is accepted inside the gesture by WebKit and Chromium alike — and the share sheet is one
 * more (activated) tap away, on the toast.
 */

export type ShareScope = 'song' | 'album';
export type ShareLink = { url: string; title: string };

function toLink(share: SongShareView): ShareLink {
  return {
    url: shareUrl(share.token),
    title: share.artist ? `${share.title} — ${share.artist}` : share.title
  };
}

/**
 * The active link for `scope` among `songIds`, preferring earlier ids. An album link is looked for
 * across the whole album because the server keys it on whichever track it was created from.
 */
export function pickExistingShare(
  shares: readonly SongShareView[],
  songIds: readonly number[],
  scope: ShareScope
): SongShareView | null {
  const wanted = scope === 'album' ? 'album' : 'song';
  const matching = shares.filter((s) => String(s.scope).toLowerCase() === wanted);
  for (const id of songIds) {
    const hit = matching.find((s) => s.songId === id);
    if (hit) return hit;
  }
  return null;
}

// The active links, fetched at most once per short window: opening a menu on ten rows in a row costs
// one request, not ten. Dropped after a mint so the new link is found next time.
const LIST_TTL_MS = 30_000;
let listed: { at: number; promise: Promise<SongShareView[]> } | null = null;

function activeShares(): Promise<SongShareView[]> {
  const now = Date.now();
  if (!listed || now - listed.at > LIST_TTL_MS) {
    listed = { at: now, promise: listSongShares().catch(() => []) };
  }
  return listed.promise;
}

/** Test seam: forget the cached list. */
export function resetShareLinkCache(): void {
  listed = null;
}

/** Read-only: the link that already exists for this song or album, or null. Never creates one. */
export async function findShareLink(
  songIds: readonly number[],
  scope: ShareScope
): Promise<ShareLink | null> {
  if (songIds.length === 0) return null;
  const hit = pickExistingShare(await activeShares(), songIds, scope);
  return hit ? toLink(hit) : null;
}

async function mint(songId: number, scope: ShareScope): Promise<ShareLink> {
  const share = await createSongShare(songId, scope);
  listed = null;
  return toLink(share);
}

function canOpenShareSheet(url: string): boolean {
  return (
    typeof navigator !== 'undefined' &&
    typeof navigator.share === 'function' &&
    navigator.canShare?.({ url }) !== false
  );
}

/**
 * Starts a clipboard write whose text is the pending link. It must be called synchronously inside
 * the tap: the write claims the activation now and fills in the text when the mint lands. Resolves
 * false where the browser has no promise-valued ClipboardItem, or refuses the write.
 */
function copyWhenReady(pending: Promise<ShareLink>): Promise<boolean> {
  try {
    if (typeof ClipboardItem === 'undefined' || !navigator.clipboard?.write) {
      return Promise.resolve(false);
    }
    const text = pending.then((link) => new Blob([link.url], { type: 'text/plain' }));
    // A failed mint is reported by the caller; don't let it also surface as an unhandled rejection.
    text.catch(() => {});
    const item = new ClipboardItem({ 'text/plain': text });
    return navigator.clipboard.write([item]).then(
      () => true,
      () => false
    );
  } catch {
    return Promise.resolve(false);
  }
}

function describeFor(noun: 'song' | 'album'): string {
  return `Anyone with the link can play this ${noun} and see its lyrics.`;
}

/** The share sheet, falling back to the clipboard, falling back to showing the link. */
async function handOff(link: ShareLink, noun: 'song' | 'album'): Promise<void> {
  if (canOpenShareSheet(link.url)) {
    try {
      await navigator.share({ url: link.url, title: link.title });
      return; // the sheet is its own confirmation
    } catch (err) {
      // Closing the sheet without picking anything is not a failure worth a toast.
      if (err instanceof DOMException && err.name === 'AbortError') return;
      // Anything else (no activation left, a permissions policy) falls through to the clipboard.
    }
  }
  try {
    await navigator.clipboard.writeText(link.url);
    toast.success('Share link copied', { description: describeFor(noun) });
  } catch {
    toast.info('Share link created', { description: link.url, duration: 12000 });
  }
}

/**
 * The Share link… item. `known` is what `findShareLink` found when the menu opened: with it, the share
 * sheet opens straight from the tap. Without it the link is minted now and copied as it lands, and
 * the confirming toast carries a Share button for the sheet.
 */
export function shareLink(opts: {
  known: ShareLink | null;
  songId: number;
  scope: ShareScope;
}): void {
  const noun = opts.scope === 'album' ? 'album' : 'song';
  if (opts.known) {
    void handOff(opts.known, noun);
    return;
  }

  const pending = mint(opts.songId, opts.scope);
  const copied = copyWhenReady(pending);
  void pending.then(
    async (link) => {
      const sheet = canOpenShareSheet(link.url)
        ? {
            label: 'Share…',
            onClick: () =>
              void navigator.share({ url: link.url, title: link.title }).catch(() => {})
          }
        : undefined;
      if (await copied) {
        toast.success('Share link copied', { description: describeFor(noun), action: sheet });
        return;
      }
      // Nothing reached the clipboard (an older browser, or the write was refused): show the link,
      // with the share sheet — or a copy — one tap away.
      toast.info('Share link created', {
        description: link.url,
        duration: 12000,
        action: sheet ?? {
          label: 'Copy',
          onClick: () =>
            void navigator.clipboard.writeText(link.url).then(
              () => toast.success('Share link copied'),
              () => {}
            )
        }
      });
    },
    (err: unknown) => {
      toast.error(err instanceof Error ? err.message : 'Could not create share link');
    }
  );
}

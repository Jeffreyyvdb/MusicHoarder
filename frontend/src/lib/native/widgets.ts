/**
 * The page's side of the app shell's Home Screen widgets (`Widgets` plugin, `mobile/plugin`). One
 * protocol with the two native implementations: add methods and fields, never rename or remove.
 *
 * Everything here is a no-op in a browser, in the installed web app and in a shell build from
 * before the widgets, where `nativePlugin('Widgets')` is null, and nothing throws into a caller.
 * The Now Playing widget needs nothing from this file: the device's player feeds it.
 */

import { coverThumbUrl, type AlbumSummary, type ApiSong } from '#lib/api-client.ts';
import { albumHref, lastPlayedAlbums } from '#lib/library/last-played.ts';
import { nativePlugin } from './shell.ts';

/** One album tile of the Last played widget. */
export interface ShelfItem {
  id: string;
  title: string;
  subtitle: string;
  /** Absolute cover URL on this server; the app downloads it with the web view's cookie. */
  artwork: string | null;
  /** In-app path a tap opens. */
  path: string;
}

/** What a tap on a widget asks the page to do. */
export type LaunchAction =
  | { kind: 'now-playing' }
  | { kind: 'resume' }
  | { kind: 'open'; path: string };

const WIDGET_SHELF_SIZE = 8;
/** The native side shrinks to its own budget (512 px iOS, 256 px Android). */
const COVER_PX = 512;

export function widgetsAvailable(): boolean {
  return nativePlugin('Widgets') !== null;
}

/** The Overview's Last played rule, as widget tiles. Pure: the caller passes the play stats reader. */
export function buildShelf(
  albums: readonly AlbumSummary[],
  lastPlayedOf: (song: ApiSong) => string | null | undefined,
  origin: string
): ShelfItem[] {
  return lastPlayedAlbums(albums, lastPlayedOf, WIDGET_SHELF_SIZE).map((album) => {
    const thumb = coverThumbUrl(album.coverUrl, COVER_PX);
    return {
      id: album.key,
      title: album.title,
      subtitle: album.artist,
      artwork: thumb ? new URL(thumb, origin).href : null,
      path: albumHref(album)
    };
  });
}

let lastSent: string | null = null;

export function publishShelf(items: readonly ShelfItem[]): void {
  const plugin = nativePlugin('Widgets');
  if (!plugin) return;
  const body = JSON.stringify(items);
  if (body === lastSent) return;
  lastSent = body;
  plugin.call('setShelf', { items: [...items] }).catch(() => {
    // Let the next publish try again rather than believing the widgets have this one.
    if (lastSent === body) lastSent = null;
  });
}

/** The account left (sign-out, account switch): the widgets forget the shelf and the track. */
export async function clearWidgets(): Promise<void> {
  lastSent = null;
  try {
    await nativePlugin('Widgets')?.call('clear');
  } catch {
    // An older build, or the app is going away anyway.
  }
}

/**
 * The boundary: the app hands back data that originated outside the page. A path is taken only
 * as a same-origin absolute path (`/x`, never `//host` or a scheme) that `allowed` accepts.
 */
export function parseLaunchAction(
  raw: unknown,
  allowed: (pathname: string) => boolean
): LaunchAction | null {
  if (typeof raw !== 'object' || raw === null) return null;
  const { kind, path } = raw as { kind?: unknown; path?: unknown };
  if (kind === 'now-playing' || kind === 'resume') return { kind };
  if (kind !== 'open' || typeof path !== 'string') return null;
  // eslint-disable-next-line no-control-regex -- control characters in a path are refused on purpose
  if (!/^\/(?![/\\])/.test(path) || /[\u0000-\u001f]/.test(path)) return null;
  let pathname: string;
  try {
    const url = new URL(path, 'http://widget.invalid');
    if (url.origin !== 'http://widget.invalid') return null;
    pathname = url.pathname;
  } catch {
    return null;
  }
  return allowed(pathname) ? { kind, path } : null;
}

/**
 * Run `handle` for each widget tap: asked at once (a cold start the widget caused), when the page
 * becomes visible (a warm start) and on the app's `launchAction` nudge. The native read is
 * destructive, so the three triggers cannot run one tap twice. Returns a stop function.
 */
export function onLaunchAction(
  handle: (action: LaunchAction) => void,
  allowed: (pathname: string) => boolean
): () => void {
  const plugin = nativePlugin('Widgets');
  if (!plugin) return () => {};
  let stopped = false;
  // The bridge offers no way to take a listener off, so a stopped one must not read the mailbox:
  // the read is destructive, and the live subscription would lose the tap.
  const pull = () => {
    if (stopped) return;
    plugin.call('takeLaunchAction').then(
      (answer) => {
        const action = parseLaunchAction((answer as { action?: unknown } | null)?.action, allowed);
        if (action && !stopped) handle(action);
      },
      () => {}
    );
  };
  const onVisible = () => {
    if (document.visibilityState === 'visible') pull();
  };
  plugin.listen('launchAction', pull);
  document.addEventListener('visibilitychange', onVisible);
  pull();
  return () => {
    stopped = true;
    document.removeEventListener('visibilitychange', onVisible);
  };
}

// Per-icon deep imports rather than the `@lucide/svelte` barrel the components use. The barrel
// re-exports ~1600 .svelte files, and nav.test.ts imports this module — pulling all of them
// through the Svelte compiler took the unit suite from 0.7s to 19s. These paths cost
// nothing. The type import is erased, so it can stay on the barrel.
import type { LucideIcon } from '@lucide/svelte';
import ChartColumnBig from '@lucide/svelte/icons/chart-column-big';
import Compass from '@lucide/svelte/icons/compass';
import Copy from '@lucide/svelte/icons/copy';
import Disc from '@lucide/svelte/icons/disc';
import Disc3 from '@lucide/svelte/icons/disc-3';
import Download from '@lucide/svelte/icons/download';
import FolderTree from '@lucide/svelte/icons/folder-tree';
import Gauge from '@lucide/svelte/icons/gauge';
import Gift from '@lucide/svelte/icons/gift';
import History from '@lucide/svelte/icons/history';
import Inbox from '@lucide/svelte/icons/inbox';
import LayoutGrid from '@lucide/svelte/icons/layout-grid';
import Library from '@lucide/svelte/icons/library';
import Link2 from '@lucide/svelte/icons/link-2';
import ListMusic from '@lucide/svelte/icons/list-music';
import ListVideo from '@lucide/svelte/icons/list-video';
import Music2 from '@lucide/svelte/icons/music-2';
import Settings from '@lucide/svelte/icons/settings';
import SlidersHorizontal from '@lucide/svelte/icons/sliders-horizontal';
import Sparkles from '@lucide/svelte/icons/sparkles';
import Tags from '@lucide/svelte/icons/tags';
import TrendingUp from '@lucide/svelte/icons/trending-up';
import Users from '@lucide/svelte/icons/users';
import Workflow from '@lucide/svelte/icons/workflow';
import { APP_HOME } from '$lib/app-home';
import { isAdmin, isDemo } from '$lib/auth/capabilities';
import type { SessionUser } from '$lib/auth/session-types';

/**
 * THE nav source of truth.
 *
 * Every nav surface — the desktop sidebar, the compact tab bar (tabsFor/tabFor), the hubs, each
 * page's back target (backFor), the browser-tab title and the command palette — derives from this
 * file. Before it there were six independent copies of this knowledge, each with a comment
 * asking the others to stay in step, and they had drifted: /album-quality was missing from
 * the sidebar and the mobile matcher, /wishlist and /playlists were missing from the strip
 * map, /playlists had no browser-tab title, /stats and /history had no mobile representation,
 * and /track/[id] belonged to no section at all. Adding a route in one place and forgetting
 * the other five is the failure mode this module exists to make impossible.
 *
 * Four groups, in sidebar order:
 *   Listen — the results. What the pipeline produced, for playing.
 *   Inbox  — the pile that needs a human decision.
 *   Add    — the doors music comes in through.
 *   Manage — the machinery, plus the numbers it produces and the knobs it takes.
 */

/** Drop a trailing slash so '/library/' matches '/library'. */
function strip(pathname: string): string {
  return pathname.length > 1 && pathname.endsWith('/') ? pathname.slice(0, -1) : pathname;
}

/** The path part of an href, so '/inbox?tab=review' yields '/inbox'. */
function pathOf(href: string): string {
  return strip(href.split('?')[0]);
}

/**
 * Exact path or any child of it. Slash-guarded on purpose: the previous matchers used raw
 * `startsWith`, under which '/tracks' would also claim a hypothetical '/tracksomething'.
 */
function under(base: string): (url: URL) => boolean {
  return (url) => {
    const path = strip(url.pathname);
    return path === base || path.startsWith(base + '/');
  };
}

/** Exact path only — used where a child route must NOT inherit the match. */
function exact(base: string): (url: URL) => boolean {
  return (url) => strip(url.pathname) === base;
}

/** The ?tab= values InboxV2 accepts. Anything else falls back to Tag review, as it does. */
const INBOX_TABS: readonly string[] = ['review', 'dupes', 'artists', 'albums', 'ai'];

/** An Inbox queue, selected by ?tab=. Mirrors InboxV2's own fallback exactly. */
function inboxTab(tab: string): (url: URL) => boolean {
  return (url) => {
    if (!under('/inbox')(url)) return false;
    const raw = url.searchParams.get('tab');
    return (raw && INBOX_TABS.includes(raw) ? raw : 'review') === tab;
  };
}

/**
 * An artist view: `/library?artist=` with no album. It shares Albums' path but is the Artists
 * list's drill-in, so it lights Artists (an album opened from it, `&album=`, is Albums' again).
 */
function isArtistView(url: URL): boolean {
  return (
    strip(url.pathname) === '/library' &&
    param(url, 'artist') !== null &&
    param(url, 'album') === null &&
    url.searchParams.get('view') !== 'source'
  );
}

export type NavGroupId = 'listen' | 'inbox' | 'add' | 'manage';

export type NavItem = {
  /** Unique across ALL groups — the sidebar, strip and tests key off it. */
  id: string;
  label: string;
  href: string;
  icon: LucideIcon;
  /** Show a live pulse dot while a pipeline job is running. */
  live?: boolean;
  /** Extra lowercase search terms for the command palette. */
  keywords?: string;
  /** Defaults to `under(pathOf(href))`. Override for exact/query-param matching. */
  match?: (url: URL) => boolean;
};

export type NavGroup = {
  id: NavGroupId;
  label: string;
  /** The group's landing route. Must be one of its own items' hrefs. */
  href: string;
  /**
   * The group's root on a phone: the page its bottom tab opens on and pops back to. Distinct from
   * `href` because a phone lands on a list of the group's pages (the iOS Settings / Music Library
   * pattern), where the desktop sidebar lands on the first page itself. Listen's hub IS a page —
   * the Overview already reads as the group's front door — so there the two coincide.
   */
  hub: string;
  icon: LucideIcon;
  live?: boolean;
  /**
   * Extra routes the group owns beyond its items' own paths — e.g. /track/[id] under Listen.
   * A group already claims every path one of its items lives on, so this is only for routes
   * that have no tab of their own.
   */
  match?: (url: URL) => boolean;
  items: NavItem[];
};

export const NAV_GROUPS: NavGroup[] = [
  {
    id: 'listen',
    label: 'Listen',
    href: APP_HOME,
    hub: APP_HOME,
    icon: Library,
    // A track page belongs to Listen without being one of its items: the sidebar lights the group
    // header, and on a phone the tab bar keeps whichever tab pushed the track page (tabFor).
    match: under('/track'),
    items: [
      {
        id: 'overview',
        label: 'Overview',
        href: '/overview',
        icon: LayoutGrid,
        keywords: 'home dashboard summary recently added'
      },
      {
        id: 'albums',
        label: 'Albums',
        href: '/library',
        icon: Disc3,
        keywords: 'library home releases',
        // The source view lives at the same path but is deliberately NOT "Albums" — it lists
        // what's on the source share, not what the builder produced. Nor is an artist view.
        match: (url) =>
          under('/library')(url) && url.searchParams.get('view') !== 'source' && !isArtistView(url)
      },
      {
        id: 'artists',
        label: 'Artists',
        href: '/artists',
        icon: Users,
        keywords: 'performers',
        match: (url) => under('/artists')(url) || isArtistView(url)
      },
      {
        // One list, sliced by chips. It replaced three near-identical routes — /my-music (which
        // showed the same rows as this one on any library without album fill), /liked, and this —
        // because a chip row does the same job in a quarter of the sidebar and, unlike routes,
        // chips combine. The keywords carry the retired names so the palette still finds them.
        id: 'tracks',
        label: 'Tracks',
        href: '/tracks',
        icon: ListMusic,
        keywords: 'songs everything liked favourites favorites hearts loved my music mine local files spotify video lyrics unreleased'
      }
    ]
  },
  {
    id: 'inbox',
    label: 'Inbox',
    href: '/inbox',
    // The same path as the Tag review queue: a bare /inbox is the hub list on a phone and the first
    // queue on a desktop. isInboxHub() is what tells the two apart.
    hub: '/inbox',
    icon: Inbox,
    items: [
      {
        id: 'review',
        label: 'Tag review',
        href: '/inbox?tab=review',
        icon: Tags,
        keywords: 'needs review approve reject match',
        match: inboxTab('review')
      },
      {
        id: 'dupes',
        label: 'Duplicate tracks',
        href: '/inbox?tab=dupes',
        icon: Copy,
        keywords: 'dupes copies',
        match: inboxTab('dupes')
      },
      {
        // "Artist names", not "Artists": on a phone this row sits one tab away from Listen's
        // Artists, and the queue merges spellings of a name rather than listing performers.
        id: 'dupe-artists',
        label: 'Artist names',
        href: '/inbox?tab=artists',
        icon: Users,
        keywords: 'artist merge duplicate artists',
        match: inboxTab('artists')
      },
      {
        id: 'dupe-albums',
        label: 'Album names',
        href: '/inbox?tab=albums',
        icon: Disc3,
        keywords: 'album merge duplicate albums',
        match: inboxTab('albums')
      },
      {
        id: 'aiflag',
        label: 'AI flagged',
        href: '/inbox?tab=ai',
        icon: Sparkles,
        keywords: 'ai flagged suspicious llm',
        match: inboxTab('ai')
      }
    ]
  },
  {
    id: 'add',
    label: 'Add',
    href: '/discover',
    hub: '/add',
    // Deliberately not `Plus`: the top bar's Add-from-URL button already owns that glyph, and
    // two different targets sharing an icon in the same chrome is the confusion to avoid.
    icon: Download,
    // The hub is a route of its own that no item represents, so it must be claimed here. Exact,
    // like the conveyor: nothing below /add should inherit the hub.
    match: exact('/add'),
    items: [
      {
        id: 'discover',
        label: 'Discover',
        href: '/discover',
        icon: Compass,
        keywords: 'recommendations new releases radar find'
      },
      {
        id: 'spotify',
        label: 'Spotify',
        href: '/spotify',
        icon: Music2,
        keywords: 'playlists liked connect'
      },
      {
        id: 'wishlist',
        label: 'Wishlist',
        href: '/wishlist',
        icon: Gift,
        keywords: 'wanted missing acquire soulseek'
      },
      {
        // Not a playlist feature — it mirrors Spotify collections as .m3u8 files for
        // Navidrome / Plex / Jellyfin. The old "Playlists" label promised something the app
        // does not do.
        id: 'playlists',
        label: 'Playlist sync',
        href: '/playlists',
        icon: ListVideo,
        keywords: 'playlists m3u8 export mirror navidrome plex jellyfin'
      }
    ]
  },
  {
    id: 'manage',
    label: 'Manage',
    href: '/pipeline',
    hub: '/manage',
    icon: SlidersHorizontal,
    live: true,
    match: exact('/manage'),
    items: [
      {
        id: 'pipeline',
        label: 'Pipeline',
        href: '/pipeline',
        icon: Workflow,
        live: true,
        keywords: 'conveyor runs jobs ingest scan enrich build',
        // Exact, so a future /pipeline/<something> can't silently inherit the conveyor tab.
        match: exact('/pipeline')
      },
      {
        id: 'folders',
        label: 'By folder',
        href: '/directories',
        icon: FolderTree,
        keywords: 'directories folders tree match'
      },
      {
        id: 'quality',
        label: 'AI quality',
        href: '/quality',
        icon: Gauge,
        keywords: 'grade bitrate score'
      },
      {
        id: 'album-quality',
        label: 'Album matches',
        href: '/album-quality',
        icon: Disc,
        keywords: 'album quality matches reconcile tracklist'
      },
      {
        id: 'performance',
        label: 'Performance',
        href: '/performance',
        icon: TrendingUp,
        // The label lost "over time" to fit a phone's nav bar; the palette still answers to it.
        keywords: 'timeline regression version trends over time'
      },
      {
        id: 'stats',
        label: 'Stats',
        href: '/stats',
        icon: ChartColumnBig,
        keywords: 'statistics numbers hoard totals storage'
      },
      {
        id: 'history',
        label: 'History',
        href: '/history',
        icon: History,
        keywords: 'changes feed log activity written'
      },
      {
        // The public links made with "Share link…": how often each was opened and played, and
        // where they are revoked.
        id: 'shares',
        label: 'Share links',
        href: '/shares',
        icon: Link2,
        keywords: 'share links public shared url clicks opens views visitors analytics revoke tiktok'
      },
      {
        id: 'settings',
        label: 'Settings',
        href: '/settings',
        icon: Settings,
        keywords: 'config preferences account providers'
      }
    ]
  }
];

/**
 * The groups an account may see. A member reuses the admin's Listen routes and components
 * unchanged — the data layer no longer branches, because the ordinary endpoints already scope
 * to what was shared with them — but none of the curation machinery: Inbox, Add, and Manage are
 * administration, not listening.
 *
 * One filter here narrows the sidebar, the tab bar, the hubs and the command palette together, and {@link allowedPathPrefixesFor} derives the route guard from the
 * same data, so the two can no longer disagree.
 */
export function navGroupsFor(user: NavAudience): NavGroup[] {
  // The demo keeps the FULL nav on purpose: it exists to show the whole product, pipeline
  // included, and it is already write-blocked server-side by DemoReadOnlyMiddleware. Narrowing it
  // to Listen would quietly turn the public demo into a music player. Only a member is narrowed.
  if (isAdmin(user) || isDemo(user)) return NAV_GROUPS;
  return NAV_GROUPS.filter((g) => g.id === 'listen');
}

/** Whoever the nav is being built for. Structural, so tests can pass a bare object. */
export type NavAudience = Pick<SessionUser, 'role' | 'isAdmin' | 'capabilities'> | null | undefined;

/**
 * Every path prefix an account may open, derived from the groups it can see.
 *
 * This is what the `(app)` server guard enforces. Deriving it rather than hand-keeping a second
 * list is the point: the two used to be maintained separately and had already drifted — the guard
 * listed '/tracks' but not '/track', so a member who opened a song in Now Playing and clicked
 * through to its track page was silently bounced to the overview.
 */
export function allowedPathPrefixesFor(user: NavAudience): string[] {
  // Demo roams freely for the same reason it keeps the full nav — see navGroupsFor.
  if (isAdmin(user) || isDemo(user)) return ['/'];

  const fromNav = navGroupsFor(user).flatMap((g) => g.items.map((item) => pathOf(item.href)));
  return [
    ...new Set([
      ...fromNav,
      // Listen routes that are reachable from the UI but are not themselves nav items: a track
      // page (opened from Now Playing) and the liked-songs view (opened from the
      // library chips). Both must stay listed by hand — deriving from nav items alone silently
      // drops them, which is exactly the bug this function replaced.
      '/track',
      '/liked',
      // Everyone manages their own account: passkeys, phone pairing, signing out.
      '/settings'
    ])
  ];
}

/** True when `pathname` is covered by one of `prefixes` (exact match or a child segment). */
export function isPathAllowed(pathname: string, prefixes: string[]): boolean {
  return prefixes.some((p) => p === '/' || pathname === p || pathname.startsWith(`${p}/`));
}

/** Where a URL sits in the nav. `item` is null when only a group-level fallback matched. */
export type NavMatch = { group: NavGroup; item: NavItem | null };

function matcherFor(item: NavItem): (url: URL) => boolean {
  return item.match ?? under(pathOf(item.href));
}

/**
 * A group owns every path its items live on — ignoring the query string — plus whatever its
 * own `match` adds. The query is dropped on purpose: '/library?view=source' is still a Listen
 * route even though no tab represents it, and the sidebar and mobile bar should stay lit there.
 */
function groupOwns(group: NavGroup, url: URL): boolean {
  if (group.match?.(url)) return true;
  return group.items.some((item) => under(pathOf(item.href))(url));
}

/**
 * Resolve a URL to its group, and to an item when one claims it.
 *
 * Items are checked across every group first, then group ownership — so '/inbox?tab=artists'
 * binds to the Inbox queue rather than being swept up by a broader match. A null `item` means
 * the route belongs to the group but has no tab of its own: a track page, or the library's
 * source view. Returns null entirely for routes outside the app shell (/login, /share/…).
 */
export function resolveNav(url: URL): NavMatch | null {
  for (const group of NAV_GROUPS) {
    for (const item of group.items) {
      if (matcherFor(item)(url)) return { group, item };
    }
  }
  for (const group of NAV_GROUPS) {
    if (groupOwns(group, url)) return { group, item: null };
  }
  return null;
}

/**
 * True when a URL is the Inbox hub — the list of queues a phone shows at a bare /inbox — rather
 * than one of the queues.
 *
 * `?song=` counts as a queue even without `?tab=`: `/inbox?song=<id>` is a live deep link into
 * Tag review (the track timeline builds one), and treating it as the hub would drop the song.
 * Desktop semantics are unchanged — resolveNav still reads a bare /inbox as Tag review, because a
 * desktop has room to open on the first queue itself.
 */
export function isInboxHub(url: URL): boolean {
  return (
    strip(url.pathname) === '/inbox' &&
    !url.searchParams.has('tab') &&
    !url.searchParams.has('song')
  );
}

/**
 * One tab of the compact tab bar.
 *
 * `id` is the group id for a multi-group audience and the Listen item id for a member, so it is
 * stable per audience — tab-memory keys its per-tab stacks by it.
 */
export type NavTab = {
  id: string;
  label: string;
  icon: LucideIcon;
  /** Where the tab starts, and where re-tapping it pops back to. */
  root: string;
  /** Show a live pulse dot while a pipeline job is running. */
  live?: boolean;
  /** Which count badges this tab. Only the Inbox has one; the predicate lives in nav-badges. */
  badge?: 'inbox';
};

/** One tab per group, rooted at the group's hub. */
const GROUP_TABS: NavTab[] = NAV_GROUPS.map((g) => ({
  id: g.id,
  label: g.label,
  icon: g.icon,
  root: g.hub,
  ...(g.live ? { live: true } : {}),
  ...(g.id === 'inbox' ? { badge: 'inbox' as const } : {})
}));

const INBOX_TAB = GROUP_TABS.find((t) => t.id === 'inbox') ?? null;

const LISTEN = (() => {
  const group = NAV_GROUPS.find((g) => g.id === 'listen');
  if (!group) throw new Error('nav: no Listen group');
  return group;
})();

/** Listen's own pages as tabs, for an audience whose whole nav is Listen. */
const ITEM_TABS: NavTab[] = LISTEN.items.map((item) => ({
  id: item.id,
  label: item.label,
  icon: item.icon,
  root: item.href,
  ...(item.live ? { live: true } : {})
}));

function listenItem(id: string): NavItem {
  const item = LISTEN.items.find((i) => i.id === id);
  if (!item) throw new Error(`nav: no Listen item '${id}'`);
  return item;
}

/** A non-blank query parameter, trimmed — the same test LibraryV2 applies before acting on one. */
function param(url: URL, name: string): string | null {
  const raw = url.searchParams.get(name)?.trim();
  return raw ? raw : null;
}

/**
 * The tabs the compact tab bar shows.
 *
 * An audience that sees several groups (admin, demo) gets one tab per group, rooted at the hub. A
 * member sees only Listen, and a tab bar with one tab is not navigation — so their tabs are
 * Listen's own pages, the same four the Android client's library shell switches between.
 * The arrays are module constants, so the result is referentially stable per audience.
 */
export function tabsFor(user: NavAudience): NavTab[] {
  return navGroupsFor(user).length > 1 ? GROUP_TABS : ITEM_TABS;
}

/**
 * A tab by id, whatever the audience. Group tab ids and Listen item ids never collide (the item
 * ids are unique across every group, and no group shares an id with an item), so one lookup
 * over both sets is unambiguous.
 */
export function findTab(id: string): NavTab | null {
  return GROUP_TABS.find((t) => t.id === id) ?? ITEM_TABS.find((t) => t.id === id) ?? null;
}

/** The tab whose root this URL is, or null when the URL is somewhere inside a tab. */
function rootTabOf(url: URL, user: NavAudience): NavTab | null {
  const path = strip(url.pathname);
  if (tabsFor(user) === GROUP_TABS) {
    if (path === '/inbox') return isInboxHub(url) ? INBOX_TAB : null;
    return GROUP_TABS.find((t) => t.root === path) ?? null;
  }
  // /library is the Albums tab only as the grid: an album page, an artist view and the source
  // view all live at the same path and are pushed on top of it.
  if (
    path === '/library' &&
    (param(url, 'album') || param(url, 'artist') || url.searchParams.get('view') === 'source')
  ) {
    return null;
  }
  return ITEM_TABS.find((t) => t.root === path) ?? null;
}

/**
 * True when the URL is the root of a tab: the page a tab opens on, which has no back button.
 * The query is ignored — a filtered Tracks list is still the Tracks tab — except where it names
 * something pushed on top (an Inbox queue, an album, an artist).
 */
export function isTabRoot(url: URL, user: NavAudience): boolean {
  return rootTabOf(url, user) !== null;
}

/**
 * Which tab a URL belongs to.
 *
 * 1. A tab root belongs to its tab.
 * 2. Otherwise the tab you are in keeps whatever you open from it, if it can own it — an album
 *    opened from the Overview stays on the Overview's stack rather than jumping to Albums, the
 *    way a pushed view stays in its tab on iOS. For a group audience "can own" means the URL is
 *    in that group, or is a shared destination every tab links to: a track page (an Inbox item's
 *    View timeline, a History entry) or Settings (the account sheet on every tab root). Those stay
 *    on the tab that pushed them and Back returns there — an admin who opens Settings from the
 *    Listen tab's avatar is not moved to Manage, exactly as a member is not. For a member every
 *    Listen page can live on any tab.
 * 3. Otherwise the URL's own tab: its group's (group audiences), or for a member the Listen page
 *    it hangs off — Artists for an artist view, Tracks for a track page.
 *
 * Null when no tab can own the URL (a member on /settings, a route outside the shell): the tab
 * bar keeps the last active tab lit, and tab-memory records nothing.
 */
export function tabFor(url: URL, user: NavAudience, activeTabId?: string | null): NavTab | null {
  const root = rootTabOf(url, user);
  if (root) return root;

  const match = resolveNav(url);
  if (!match) return null;
  const tabs = tabsFor(user);
  const active = activeTabId ? tabs.find((t) => t.id === activeTabId) : undefined;

  if (tabs === GROUP_TABS) {
    if (active && (active.id === match.group.id || isSharedDestination(url))) return active;
    return tabs.find((t) => t.id === match.group.id) ?? null;
  }

  if (match.group.id !== LISTEN.id) return null;
  if (active) return active;
  const path = strip(url.pathname);
  const home =
    path === '/library' && param(url, 'artist')
      ? 'artists'
      : under('/track')(url)
        ? 'tracks'
        : match.item?.id;
  return tabs.find((t) => t.id === home) ?? null;
}

/** Pages every tab links to, which therefore stay on whichever tab pushed them (see tabFor). */
function isSharedDestination(url: URL): boolean {
  return under('/track')(url) || under('/settings')(url);
}

/** Where a back button goes and what it is called (the label is spoken, not shown, on a phone). */
export type NavBack = { label: string; href: string };

/**
 * The hierarchical parent of a page: where Back goes when there is no history to go back through
 * — a deep link, a reload, a page reached from another tab. tab-memory prefers the page you
 * actually came from and falls back to this.
 *
 * Null on a tab root (nothing above it) and outside the shell.
 */
export function backFor(url: URL, user: NavAudience): NavBack | null {
  if (rootTabOf(url, user)) return null;
  const match = resolveNav(url);
  if (!match) return null;

  const path = strip(url.pathname);
  const to = (item: NavItem): NavBack => ({ label: item.label, href: item.href });

  // Listen's drill-ins name their list rather than the hub: an album goes back to Albums, an
  // artist to Artists, a track page to Tracks — for every audience.
  if (path === '/library') {
    const artist = param(url, 'artist');
    // An album opened from an artist view goes back to that artist.
    if (param(url, 'album') && artist) {
      return { label: artist, href: `/library?artist=${encodeURIComponent(artist)}` };
    }
    if (param(url, 'album')) return to(listenItem('albums'));
    if (artist) return to(listenItem('artists'));
  }
  if (under('/track')(url)) return to(listenItem('tracks'));

  // A member has no hubs: everything that is not a tab root sits on top of their Overview. That
  // includes /settings, which is not in their nav at all.
  if (tabsFor(user) !== GROUP_TABS) return to(listenItem('overview'));

  // A pushed detail goes back to its list rather than to the hub: an Inbox item (selected by
  // ?song= or, for duplicates, ?group=) to its queue, a Discover playlist to Discover, a Settings
  // section to the Settings root, a share link to its list.
  if (
    match.group.id === 'inbox' &&
    match.item &&
    (url.searchParams.has('song') || url.searchParams.has('group'))
  ) {
    return to(match.item);
  }
  if (match.item && path === '/discover' && param(url, 'playlist')) return to(match.item);
  if (match.item && path === '/settings' && param(url, 'tab')) return to(match.item);
  // A share link goes back to the list it sits in (the revoked links are their own list).
  if (match.item && path === '/shares' && param(url, 'link')) {
    const revoked = url.searchParams.get('view') === 'revoked';
    return { label: match.item.label, href: revoked ? '/shares?view=revoked' : match.item.href };
  }

  return { label: match.group.label, href: match.group.hub };
}

/**
 * Pages whose whole body is inset grouped sections at every width, and so paint
 * `bg-background-grouped` themselves: the hubs, Settings, the Manage machinery pages, and a track's
 * timeline (an info page: Match, File, Identifiers sections, like Now Playing's Info).
 */
const GROUPED_PAGES: readonly string[] = [
  '/add',
  '/manage',
  '/settings',
  '/track',
  '/pipeline',
  '/quality',
  '/album-quality',
  '/performance',
  '/stats',
  '/history',
  '/shares'
];

/**
 * Which page background a URL sits on. Grouped pages (hubs, Settings, the Manage pages built from
 * inset sections) use the iOS grouped grey in light mode, so the installed app's status bar —
 * painted from theme-color — and the nav bar's scroll edge have to follow. This must agree with
 * what each page actually paints, in both directions. Now Playing's media appearance is not a URL
 * property; theme-surface holds that override.
 */
export function surfaceFor(url: URL, isCompact: boolean): 'plain' | 'grouped' {
  if (GROUPED_PAGES.some((base) => under(base)(url))) return 'grouped';
  if (!under('/inbox')(url)) return 'plain';

  // The merge queues (artist names, albums) are decision cards on the grouped background at every
  // width. The other queues are plain lists — master-detail on a desktop — but on a phone their
  // pushed detail (a song or a duplicate group) is a grouped page of its own, and the Inbox hub
  // only exists on a phone at all.
  const tab = url.searchParams.get('tab');
  if (tab === 'artists' || tab === 'albums') return 'grouped';
  if (!isCompact) return 'plain';
  if (isInboxHub(url)) return 'grouped';
  const detail = tab === 'dupes' ? param(url, 'group') : param(url, 'song');
  return detail ? 'grouped' : 'plain';
}

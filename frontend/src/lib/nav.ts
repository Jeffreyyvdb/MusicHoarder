// Per-icon deep imports rather than the `@lucide/svelte` barrel the components use. The barrel
// re-exports ~1600 .svelte files, and nav.test.ts imports this module — pulling all of them
// through the Svelte compiler took the unit suite from 0.7s to 19s. These 24 paths cost
// nothing. The type import is erased, so it can stay on the barrel.
import type { Icon as IconType } from '@lucide/svelte';
import ChartColumnBig from '@lucide/svelte/icons/chart-column-big';
import Compass from '@lucide/svelte/icons/compass';
import Copy from '@lucide/svelte/icons/copy';
import Disc from '@lucide/svelte/icons/disc';
import Disc3 from '@lucide/svelte/icons/disc-3';
import Download from '@lucide/svelte/icons/download';
import FolderTree from '@lucide/svelte/icons/folder-tree';
import Gauge from '@lucide/svelte/icons/gauge';
import Gift from '@lucide/svelte/icons/gift';
import Heart from '@lucide/svelte/icons/heart';
import History from '@lucide/svelte/icons/history';
import Inbox from '@lucide/svelte/icons/inbox';
import LayoutGrid from '@lucide/svelte/icons/layout-grid';
import Library from '@lucide/svelte/icons/library';
import ListChecks from '@lucide/svelte/icons/list-checks';
import ListMusic from '@lucide/svelte/icons/list-music';
import ListVideo from '@lucide/svelte/icons/list-video';
import Music2 from '@lucide/svelte/icons/music-2';
import Plug from '@lucide/svelte/icons/plug';
import Settings from '@lucide/svelte/icons/settings';
import SlidersHorizontal from '@lucide/svelte/icons/sliders-horizontal';
import Sparkles from '@lucide/svelte/icons/sparkles';
import Tags from '@lucide/svelte/icons/tags';
import TrendingUp from '@lucide/svelte/icons/trending-up';
import Users from '@lucide/svelte/icons/users';
import Workflow from '@lucide/svelte/icons/workflow';
import { APP_HOME } from '$lib/app-home';

/**
 * THE nav source of truth.
 *
 * Every nav surface — the desktop sidebar, the mobile bottom bar, the section tab strip, the
 * top-bar section title, the browser-tab title and the command palette — derives from this
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

export type NavGroupId = 'listen' | 'inbox' | 'add' | 'manage';

export type NavItem = {
  /** Unique across ALL groups — the sidebar, strip and tests key off it. */
  id: string;
  label: string;
  href: string;
  icon: typeof IconType;
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
  icon: typeof IconType;
  live?: boolean;
  /** The page renders its own tab bar, so the shell strip stays off (Inbox). */
  ownsSubNav?: boolean;
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
    icon: Library,
    // A track page belongs to Listen but is not one of the tabs, so the sidebar group and the
    // mobile bar stay lit while the strip shows no active pill.
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
        // what's on the source share, not what the builder produced.
        match: (url) => under('/library')(url) && url.searchParams.get('view') !== 'source'
      },
      { id: 'artists', label: 'Artists', href: '/artists', icon: Users, keywords: 'performers' },
      { id: 'tracks', label: 'All tracks', href: '/tracks', icon: ListMusic, keywords: 'songs' },
      {
        id: 'liked',
        label: 'Liked songs',
        href: '/liked',
        icon: Heart,
        keywords: 'favourites favorites hearts loved'
      }
    ]
  },
  {
    id: 'inbox',
    label: 'Inbox',
    href: '/inbox',
    icon: Inbox,
    // InboxV2 renders its own ?tab= bar with live per-tab counts; a shell strip would double it.
    ownsSubNav: true,
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
        label: 'Duplicates',
        href: '/inbox?tab=dupes',
        icon: Copy,
        keywords: 'dupes copies',
        match: inboxTab('dupes')
      },
      {
        id: 'dupe-artists',
        label: 'Artists',
        href: '/inbox?tab=artists',
        icon: Users,
        keywords: 'artist merge duplicate artists',
        match: inboxTab('artists')
      },
      {
        id: 'dupe-albums',
        label: 'Albums',
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
    // Deliberately not `Plus`: the top bar's Add-from-URL button already owns that glyph, and
    // two different targets sharing an icon in the same chrome is the confusion to avoid.
    icon: Download,
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
    icon: SlidersHorizontal,
    live: true,
    items: [
      {
        id: 'pipeline',
        label: 'Pipeline',
        href: '/pipeline',
        icon: Workflow,
        live: true,
        keywords: 'conveyor jobs ingest scan enrich build hold pause resume',
        // Exact, so a future /pipeline/<something> can't silently inherit the conveyor tab.
        match: exact('/pipeline')
      },
      {
        id: 'runs',
        label: 'Runs',
        href: '/runs',
        icon: ListChecks,
        keywords: 'history ingest ledger log throughput failures'
      },
      {
        id: 'connections',
        label: 'Connections',
        href: '/connections',
        icon: Plug,
        keywords: 'spotify navidrome soulseek slskd sync status connected integrations'
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
        label: 'Performance over time',
        href: '/performance',
        icon: TrendingUp,
        keywords: 'timeline regression version trends'
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
        id: 'settings',
        label: 'Settings',
        href: '/settings',
        icon: Settings,
        keywords: 'config preferences account providers'
      }
    ]
  }
];

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

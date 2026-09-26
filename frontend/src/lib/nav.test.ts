import { describe, expect, it } from 'vitest';
import {
  NAV_GROUPS,
  allowedPathPrefixesFor,
  backFor,
  isInboxHub,
  isPathAllowed,
  isTabRoot,
  navGroupsFor,
  resolveNav,
  surfaceFor,
  tabFor,
  tabsFor,
  type NavGroupId
} from './nav';

const url = (path: string) => new URL(path, 'https://musichoarder.test');
const at = (path: string) => resolveNav(url(path));

/**
 * Every route under `src/routes/(app)/`, listed by hand.
 *
 * Deliberately NOT derived from NAV_GROUPS: the whole point is to catch a route that exists on
 * disk but has fallen out of the nav, which is exactly what happened to /album-quality,
 * /wishlist, /playlists, /stats and /history under the old six-copy arrangement. A derived list
 * would pass vacuously. When a route directory is added, add it here too.
 *
 * `(app)/liked` is deliberately absent: it is a redirect-only stub to `/tracks?f=mh-liked`, so it
 * has no nav home and never renders inside the shell.
 */
const APP_ROUTES: [path: string, group: NavGroupId][] = [
  ['/add', 'add'],
  ['/album-quality', 'manage'],
  ['/artists', 'listen'],
  ['/directories', 'manage'],
  ['/discover', 'add'],
  ['/history', 'manage'],
  ['/inbox', 'inbox'],
  ['/library', 'listen'],
  ['/manage', 'manage'],
  ['/overview', 'listen'],
  ['/performance', 'manage'],
  ['/pipeline', 'manage'],
  ['/playlists', 'add'],
  ['/quality', 'manage'],
  ['/settings', 'manage'],
  ['/shares', 'manage'],
  ['/spotify', 'add'],
  ['/stats', 'manage'],
  ['/track/42', 'listen'],
  ['/tracks', 'listen'],
  ['/wishlist', 'add']
];

describe('NAV_GROUPS', () => {
  // The IA itself, pinned: results first, then the people you share them with, then the pile that
  // needs you, then the doors music comes in through, then the machinery. Reordering or renaming a
  // group is a deliberate act.
  it('is Listen / Chats / Inbox / Add / Manage, in that order', () => {
    expect(NAV_GROUPS.map((g) => g.label)).toEqual(['Listen', 'Chats', 'Inbox', 'Add', 'Manage']);
  });

  it('gives every item an id unique across all groups', () => {
    const ids = NAV_GROUPS.flatMap((g) => g.items.map((i) => i.id));
    expect(new Set(ids).size).toBe(ids.length);
  });

  // Clicking a group header must land inside that group — never on a dead route, and never on
  // some other group's page. (Inbox's items carry ?tab=, so compare by resolution, not string.)
  it('points every group header at a route inside that group', () => {
    for (const group of NAV_GROUPS) {
      const match = at(group.href);
      expect(match?.group.id, group.href).toBe(group.id);
      expect(match?.item, group.href).not.toBeNull();
    }
  });

  // Listen used to carry three flat track lists — /my-music, /tracks and /liked — that differed only
  // by a predicate. They are one route sliced by chips now, so a fourth item appearing here should
  // be a deliberate act, not a filter that grew a URL.
  it('is Overview / Albums / Artists / Tracks, in that order', () => {
    const listen = NAV_GROUPS.find((g) => g.id === 'listen');
    expect(listen?.items.map((i) => i.id)).toEqual(['overview', 'albums', 'artists', 'tracks']);
  });

  // Renamed for the phone, where Inbox and Listen sit one tab apart: two rows both called "Artists"
  // would name different things. Ids are unchanged, so links and tests keyed on them still hold.
  it('names the Inbox queues and Manage pages as the phone reads them', () => {
    const labels = new Map(NAV_GROUPS.flatMap((g) => g.items.map((i) => [i.id, i.label])));
    expect(labels.get('dupes')).toBe('Duplicate tracks');
    expect(labels.get('dupe-artists')).toBe('Artist names');
    expect(labels.get('dupe-albums')).toBe('Album names');
    expect(labels.get('performance')).toBe('Performance');
  });

  it('roots each group at its hub, and every hub resolves inside its own group', () => {
    expect(NAV_GROUPS.map((g) => g.hub)).toEqual(['/overview', '/chats', '/inbox', '/add', '/manage']);
    for (const group of NAV_GROUPS) {
      expect(at(group.hub)?.group.id, group.hub).toBe(group.id);
    }
  });
});

describe('resolveNav', () => {
  it('resolves every (app) route to the right group', () => {
    for (const [path, group] of APP_ROUTES) {
      expect(at(path)?.group.id, path).toBe(group);
    }
  });

  it('round-trips every item: its own href resolves back to it', () => {
    for (const group of NAV_GROUPS) {
      for (const item of group.items) {
        const match = at(item.href);
        expect(match?.group.id, item.href).toBe(group.id);
        expect(match?.item?.id, item.href).toBe(item.id);
      }
    }
  });

  it('returns null outside the app shell', () => {
    expect(at('/login')).toBeNull();
    expect(at('/share/abc123')).toBeNull();
    expect(at('/nope')).toBeNull();
  });

  it('ignores a trailing slash', () => {
    expect(at('/library/')?.item?.id).toBe('albums');
    expect(at('/pipeline/')?.item?.id).toBe('pipeline');
  });

  // The slash guard: the old matchers used raw startsWith, under which '/tracks' and the
  // '/track/[id]' detail route were one prefix away from claiming each other.
  it('keeps /track/[id] and /tracks apart', () => {
    expect(at('/tracks')?.item?.id).toBe('tracks');
    // The chip filters live in the query string, so a filtered list is still the Tracks tab.
    expect(at('/tracks?f=mh-liked')?.item?.id).toBe('tracks');

    // A track page belongs to Listen, but no tab represents it — so the sidebar group and the
    // mobile bar stay lit while the strip shows no active pill.
    const track = at('/track/42');
    expect(track?.group.id).toBe('listen');
    expect(track?.item).toBeNull();
  });

  it('lights Artists on an artist view, and Albums again on an album opened from it', () => {
    expect(at('/library?artist=Bj%C3%B6rk')?.item?.id).toBe('artists');
    expect(at('/library?artist=Bj%C3%B6rk&album=abc')?.item?.id).toBe('albums');
    expect(at('/library?album=abc')?.item?.id).toBe('albums');
    // A blank artist is no artist view, the same test LibraryV2 applies.
    expect(at('/library?artist=%20')?.item?.id).toBe('albums');
  });

  it('does not treat the library source view as Albums', () => {
    expect(at('/library')?.item?.id).toBe('albums');

    const source = at('/library?view=source');
    expect(source?.group.id).toBe('listen');
    expect(source?.item).toBeNull();
  });

  it('selects the Inbox queue from ?tab=, defaulting to Tag review', () => {
    expect(at('/inbox')?.item?.id).toBe('review');
    expect(at('/inbox?tab=review')?.item?.id).toBe('review');
    expect(at('/inbox?tab=dupes')?.item?.id).toBe('dupes');
    expect(at('/inbox?tab=artists')?.item?.id).toBe('dupe-artists');
    expect(at('/inbox?tab=albums')?.item?.id).toBe('dupe-albums');
    expect(at('/inbox?tab=ai')?.item?.id).toBe('aiflag');
    // An unknown tab falls back the same way InboxV2's own `tab` derived does.
    expect(at('/inbox?tab=bogus')?.item?.id).toBe('review');
  });

  it('binds an Inbox queue to its item rather than a broader match', () => {
    // ?tab=artists must not be captured by the Listen group's Artists item.
    expect(at('/inbox?tab=artists')?.group.id).toBe('inbox');
  });

  it('keeps the conveyor exact so a child route cannot inherit its tab', () => {
    expect(at('/pipeline')?.item?.id).toBe('pipeline');

    // A hypothetical child route still belongs to Manage — the group owns the path — but it is
    // not the conveyor, so no tab lights up.
    const child = at('/pipeline/anything');
    expect(child?.group.id).toBe('manage');
    expect(child?.item).toBeNull();
  });

  // The phone hubs are routes no item represents, like a track page: the group lights up, no tab
  // does, and the browser-tab title falls back to the group label.
  it('resolves the Add and Manage hubs to their group with no item', () => {
    for (const [path, group] of [
      ['/add', 'add'],
      ['/manage', 'manage']
    ] as const) {
      expect(at(path)?.group.id, path).toBe(group);
      expect(at(path)?.item, path).toBeNull();
    }
    // Exact, like the conveyor: nothing below a hub inherits it.
    expect(at('/manage/anything')).toBeNull();
  });

  it('resolves the Manage machinery pages that used to be orphaned', () => {
    expect(at('/album-quality')?.item?.id).toBe('album-quality');
    expect(at('/directories')?.item?.id).toBe('folders');
    expect(at('/stats')?.item?.id).toBe('stats');
    expect(at('/history')?.item?.id).toBe('history');
  });
});

describe('what each account may see', () => {
  const admin = { role: 'Owner' as const, isAdmin: true };
  const member = { role: 'Friend' as const, isAdmin: false, capabilities: [] };

  const allowed = (path: string, user: Parameters<typeof allowedPathPrefixesFor>[0]) =>
    isPathAllowed(path, allowedPathPrefixesFor(user));

  it('gives an admin every group and every path', () => {
    expect(navGroupsFor(admin)).toHaveLength(NAV_GROUPS.length);
    for (const path of ['/overview', '/pipeline', '/settings', '/wishlist', '/album-quality']) {
      expect(allowed(path, admin)).toBe(true);
    }
  });

  it('keeps the demo account on the full product, not just Listen', () => {
    // The demo exists to SHOW the product, pipeline included, and is already write-blocked
    // server-side. An earlier version of this keyed the narrowing on isAdmin alone, which
    // silently demoted the public demo to a music player.
    const demo = { role: 'Demo' as const, isAdmin: false, capabilities: [] };
    expect(navGroupsFor(demo)).toHaveLength(NAV_GROUPS.length);
    for (const path of ['/pipeline', '/inbox', '/stats', '/album-quality', '/wishlist']) {
      expect(allowed(path, demo)).toBe(true);
    }
  });

  it('narrows a member to Listen and Chats', () => {
    expect(navGroupsFor(member).map((g) => g.id)).toEqual(['listen', 'chats']);
  });

  it('lets a member reach their chats, and the page the share sheet opens', () => {
    for (const path of ['/chats', '/chats/0b7c3f4e-1d2a-4c3b-9e8f-7a6b5c4d3e2f', '/chats/share']) {
      expect(allowed(path, member)).toBe(true);
    }
  });

  it('lets a member reach every Listen route plus their own settings', () => {
    for (const path of ['/overview', '/library', '/artists', '/tracks', '/settings']) {
      expect(allowed(path, member)).toBe(true);
    }
  });

  it('lets a member open a track page', () => {
    // Regression: the hand-kept guard listed '/tracks' but not '/track', so a member who opened
    // the song-detail sidebar and clicked through was silently bounced to the overview.
    expect(allowed('/track/123', member)).toBe(true);
  });

  it('lets a member open their liked songs', () => {
    // /liked is a real route reached from the library chips but is not a nav item, so deriving
    // from nav items alone would drop it.
    expect(allowed('/liked', member)).toBe(true);
  });

  it('keeps a member out of every administration route', () => {
    for (const path of ['/pipeline', '/inbox', '/wishlist', '/discover', '/album-quality', '/stats']) {
      expect(allowed(path, member)).toBe(false);
    }
  });

  it('keeps a member out of the Add and Manage hubs', () => {
    // The hubs are group-level routes, not Listen items, so the derived guard never lists them.
    for (const path of ['/add', '/manage']) {
      expect(allowed(path, member)).toBe(false);
      expect(allowed(path, admin)).toBe(true);
    }
  });

  it('does not let a path merely starting with an allowed name through', () => {
    // '/library-admin' must not pass because '/library' is allowed.
    expect(allowed('/library-admin', member)).toBe(false);
  });

  it('treats an unknown or absent account as a member, not an admin', () => {
    expect(navGroupsFor(null).map((g) => g.id)).toEqual(['listen', 'chats']);
    expect(allowed('/pipeline', null)).toBe(false);
  });

  it('reads isAdmin, not the legacy role string', () => {
    // The wire still says 'Friend' for a member and 'Owner' for an admin, but that vocabulary is
    // scheduled to change; nothing here may depend on it.
    expect(navGroupsFor({ role: 'Friend', isAdmin: true })).toHaveLength(NAV_GROUPS.length);
    expect(navGroupsFor({ role: 'Owner', isAdmin: false }).map((g) => g.id)).toEqual([
      'listen',
      'chats'
    ]);
  });
});

const admin = { role: 'Owner' as const, isAdmin: true };
const demo = { role: 'Demo' as const, isAdmin: false, capabilities: [] };
const member = { role: 'Friend' as const, isAdmin: false, capabilities: [] };

describe('isInboxHub', () => {
  it('is a bare /inbox only', () => {
    expect(isInboxHub(url('/inbox'))).toBe(true);
    expect(isInboxHub(url('/inbox/'))).toBe(true);
    expect(isInboxHub(url('/inbox?tab=review'))).toBe(false);
    expect(isInboxHub(url('/inbox?tab=dupes&group=abc'))).toBe(false);
    expect(isInboxHub(url('/overview'))).toBe(false);
  });

  it('treats a ?song= deep link as Tag review, not the hub', () => {
    // The track timeline links a song into review without a tab; the hub would drop the song.
    expect(isInboxHub(url('/inbox?song=42'))).toBe(false);
  });
});

describe('tabsFor', () => {
  it('gives the admin and the demo one tab per group, rooted at the hubs', () => {
    for (const user of [admin, demo]) {
      const tabs = tabsFor(user);
      expect(tabs.map((t) => t.id)).toEqual(['listen', 'chats', 'inbox', 'add', 'manage']);
      expect(tabs.map((t) => t.label)).toEqual(['Listen', 'Chats', 'Inbox', 'Add', 'Manage']);
      expect(tabs.map((t) => t.root)).toEqual(['/overview', '/chats', '/inbox', '/add', '/manage']);
    }
  });

  it('badges only Chats and the Inbox, and pulses only Manage', () => {
    const tabs = tabsFor(admin);
    expect(tabs.filter((t) => t.badge).map((t) => [t.id, t.badge])).toEqual([
      ['chats', 'chats'],
      ['inbox', 'inbox']
    ]);
    expect(tabs.filter((t) => t.live).map((t) => t.id)).toEqual(['manage']);
  });

  it("gives a member Listen's own pages as tabs, then Chats", () => {
    const tabs = tabsFor(member);
    expect(tabs.map((t) => t.id)).toEqual(['overview', 'albums', 'artists', 'tracks', 'chats']);
    expect(tabs.map((t) => t.root)).toEqual([
      '/overview',
      '/library',
      '/artists',
      '/tracks',
      '/chats'
    ]);
    expect(tabs.filter((t) => t.badge).map((t) => t.badge)).toEqual(['chats']);
    expect(tabs.some((t) => t.live)).toBe(false);
  });

  it('is stable per audience, so a keyed each block does not churn', () => {
    expect(tabsFor(admin)).toBe(tabsFor(demo));
    expect(tabsFor(member)).toBe(tabsFor(null));
  });
});

describe('isTabRoot', () => {
  const cases: [path: string, groups: boolean, member: boolean][] = [
    ['/overview', true, true],
    ['/overview?x=1', true, true],
    ['/inbox', true, false],
    ['/inbox?tab=review', false, false],
    ['/inbox?song=42', false, false],
    ['/add', true, false],
    ['/manage', true, false],
    ['/manage/', true, false],
    ['/library', false, true],
    ['/library?sort=added', false, true],
    ['/library?year=2019', false, true],
    ['/library?album=abc', false, false],
    ['/library?artist=Björk', false, false],
    ['/library?view=source', false, false],
    ['/artists', false, true],
    ['/tracks', false, true],
    ['/tracks?f=mh-liked', false, true],
    ['/track/42', false, false],
    ['/discover', false, false],
    ['/pipeline', false, false],
    ['/settings', false, false],
    ['/login', false, false]
  ];

  it.each(cases)('%s → group audience %s, member %s', (path, groups, forMember) => {
    expect(isTabRoot(url(path), admin)).toBe(groups);
    expect(isTabRoot(url(path), demo)).toBe(groups);
    expect(isTabRoot(url(path), member)).toBe(forMember);
  });
});

describe('tabFor', () => {
  const tab = (path: string, user: Parameters<typeof tabFor>[1], active?: string | null) =>
    tabFor(url(path), user, active)?.id ?? null;

  it('gives a tab root to its own tab, whatever was active', () => {
    expect(tab('/inbox', admin, 'listen')).toBe('inbox');
    expect(tab('/overview', admin, 'manage')).toBe('listen');
    expect(tab('/tracks', member, 'overview')).toBe('tracks');
    expect(tab('/library', member, 'artists')).toBe('albums');
  });

  it('files a group page under its group for the admin and the demo', () => {
    expect(tab('/tracks', admin)).toBe('listen');
    expect(tab('/library?album=abc', admin, 'listen')).toBe('listen');
    expect(tab('/inbox?tab=dupes', admin, 'listen')).toBe('inbox');
    expect(tab('/wishlist', admin, 'listen')).toBe('add');
    // With no tab active (a deep link) Settings is Manage's.
    expect(tab('/settings', demo)).toBe('manage');
  });

  it('keeps Settings on whichever tab pushed it', () => {
    // The account sheet on every tab root opens Settings: Back must return to that tab, as it
    // does for a member, rather than land on the Manage hub.
    expect(tab('/settings', admin, 'listen')).toBe('listen');
    expect(tab('/settings?tab=people', demo, 'inbox')).toBe('inbox');
    expect(tab('/settings', admin, 'manage')).toBe('manage');
  });

  it('keeps a track page on whichever tab pushed it', () => {
    // View timeline from an Inbox item, a History entry: a shared detail, not a Listen page.
    expect(tab('/track/42', demo, 'inbox')).toBe('inbox');
    expect(tab('/track/42', admin, 'manage')).toBe('manage');
    expect(tab('/track/42', admin, 'listen')).toBe('listen');
    // With no tab active (a deep link) it is Listen's.
    expect(tab('/track/42', admin)).toBe('listen');
    // Only the track page: anything else in Listen still switches to Listen.
    expect(tab('/library?album=abc', admin, 'inbox')).toBe('listen');
  });

  it("keeps a member's pushes on the tab they were made from", () => {
    // An album opened from an Overview shelf stays on the Overview's stack, as on iOS.
    expect(tab('/library?album=abc', member, 'overview')).toBe('overview');
    expect(tab('/library?artist=Björk', member, 'tracks')).toBe('tracks');
    expect(tab('/track/42', member, 'albums')).toBe('albums');
  });

  it('falls back to the member page a drill-in hangs off when no tab is active', () => {
    expect(tab('/library?album=abc', member)).toBe('albums');
    expect(tab('/library?artist=Björk', member)).toBe('artists');
    expect(tab('/track/42', member)).toBe('tracks');
  });

  it('leaves a page no tab can own unowned', () => {
    // A member's /settings keeps the last tab lit and is never recorded on a stack.
    expect(tab('/settings', member, 'tracks')).toBeNull();
    expect(tab('/settings?tab=account', member)).toBeNull();
    expect(tab('/login', admin, 'listen')).toBeNull();
  });

  it('files a conversation under Chats wherever it was opened from', () => {
    // A notification or Send to… can open a conversation from any tab; Back goes to the chat list.
    const conversation = '/chats/0b7c3f4e-1d2a-4c3b-9e8f-7a6b5c4d3e2f';
    expect(tab('/chats', admin, 'listen')).toBe('chats');
    expect(tab(conversation, admin, 'inbox')).toBe('chats');
    expect(tab(conversation, member, 'overview')).toBe('chats');
    expect(tab('/chats/share?text=hi', member)).toBe('chats');
  });

  it('ignores an active tab id the audience does not have', () => {
    expect(tab('/library?album=abc', admin, 'overview')).toBe('listen');
    expect(tab('/library?album=abc', member, 'listen')).toBe('albums');
  });
});

describe('backFor', () => {
  const back = (path: string, user: Parameters<typeof backFor>[1]) => backFor(url(path), user);

  it('has nothing above a tab root', () => {
    for (const path of ['/overview', '/inbox', '/add', '/manage']) {
      expect(back(path, admin), path).toBeNull();
    }
    for (const path of ['/overview', '/library', '/artists', '/tracks?f=mh-liked']) {
      expect(back(path, member), path).toBeNull();
    }
  });

  it('sends a conversation back to the list of chats, for every audience', () => {
    const conversation = '/chats/0b7c3f4e-1d2a-4c3b-9e8f-7a6b5c4d3e2f';
    expect(back('/chats', admin)).toBeNull();
    expect(back('/chats', member)).toBeNull();
    expect(back(conversation, admin)).toEqual({ label: 'Chats', href: '/chats' });
    expect(back(conversation, member)).toEqual({ label: 'Chats', href: '/chats' });
    expect(back('/chats/share?url=x', member)).toEqual({ label: 'Chats', href: '/chats' });
  });

  it('sends a group page back to its hub', () => {
    expect(back('/tracks', admin)).toEqual({ label: 'Listen', href: '/overview' });
    expect(back('/library', admin)).toEqual({ label: 'Listen', href: '/overview' });
    expect(back('/library?view=source', admin)).toEqual({ label: 'Listen', href: '/overview' });
    expect(back('/inbox?tab=review', admin)).toEqual({ label: 'Inbox', href: '/inbox' });
    expect(back('/discover', demo)).toEqual({ label: 'Add', href: '/add' });
    expect(back('/pipeline', admin)).toEqual({ label: 'Manage', href: '/manage' });
    expect(back('/settings', admin)).toEqual({ label: 'Manage', href: '/manage' });
  });

  it('sends a Listen drill-in back to its list, for every audience', () => {
    for (const user of [admin, member]) {
      expect(back('/library?album=abc', user)).toEqual({ label: 'Albums', href: '/library' });
      expect(back('/library?artist=Björk', user)).toEqual({ label: 'Artists', href: '/artists' });
      expect(back('/track/42', user)).toEqual({ label: 'Tracks', href: '/tracks' });
    }
  });

  it('sends an album opened inside an artist view back to that artist', () => {
    expect(back('/library?artist=Sigur%20R%C3%B3s&album=abc', member)).toEqual({
      label: 'Sigur Rós',
      href: '/library?artist=Sigur%20R%C3%B3s'
    });
  });

  it('sends a pushed detail back to its list rather than the hub', () => {
    expect(back('/inbox?tab=review&song=42', admin)).toEqual({
      label: 'Tag review',
      href: '/inbox?tab=review'
    });
    expect(back('/inbox?song=42', admin)).toEqual({
      label: 'Tag review',
      href: '/inbox?tab=review'
    });
    expect(back('/inbox?tab=ai&song=42', admin)).toEqual({
      label: 'AI flagged',
      href: '/inbox?tab=ai'
    });
    expect(back('/inbox?tab=dupes&group=k1', admin)).toEqual({
      label: 'Duplicate tracks',
      href: '/inbox?tab=dupes'
    });
    expect(back('/discover?playlist=spotify:1', admin)).toEqual({
      label: 'Discover',
      href: '/discover'
    });
    expect(back('/shares?link=3', admin)).toEqual({ label: 'Share links', href: '/shares' });
    expect(back('/shares?link=3&view=revoked', admin)).toEqual({
      label: 'Share links',
      href: '/shares?view=revoked'
    });
    // The list itself hangs off the hub like every other Manage page.
    expect(back('/shares?view=revoked', admin)).toEqual({ label: 'Manage', href: '/manage' });
    expect(back('/settings?tab=people', admin)).toEqual({ label: 'Settings', href: '/settings' });
  });

  it("sends a member's non-root pages, settings included, back to their Overview", () => {
    expect(back('/settings', member)).toEqual({ label: 'Overview', href: '/overview' });
    expect(back('/settings?tab=account', member)).toEqual({ label: 'Overview', href: '/overview' });
    expect(back('/library?view=source', member)).toEqual({ label: 'Overview', href: '/overview' });
  });

  it('has nothing outside the app shell', () => {
    expect(back('/login', admin)).toBeNull();
    expect(back('/share/abc', member)).toBeNull();
  });
});

describe('surfaceFor', () => {
  it('puts the hubs, Settings and the Manage pages on the grouped background at every width', () => {
    for (const path of [
      '/add',
      '/manage',
      '/settings',
      '/settings?tab=account',
      '/pipeline',
      '/quality',
      '/quality?song=42',
      '/album-quality',
      '/performance',
      '/stats',
      '/history',
      '/history?category=lyrics',
      '/shares',
      '/shares?link=3',
      '/track/42'
    ]) {
      expect(surfaceFor(url(path), true), path).toBe('grouped');
      expect(surfaceFor(url(path), false), path).toBe('grouped');
    }
  });

  it('groups the Inbox hub only where it exists: on a phone', () => {
    expect(surfaceFor(url('/inbox'), true)).toBe('grouped');
    expect(surfaceFor(url('/inbox'), false)).toBe('plain');
  });

  it("follows the Inbox queues' own backgrounds", () => {
    // The queue lists are plain; a phone's pushed detail is a grouped page.
    for (const path of ['/inbox?tab=review', '/inbox?tab=dupes', '/inbox?tab=ai']) {
      expect(surfaceFor(url(path), true), path).toBe('plain');
      expect(surfaceFor(url(path), false), path).toBe('plain');
    }
    for (const path of [
      '/inbox?song=42',
      '/inbox?tab=review&song=42',
      '/inbox?tab=ai&song=42',
      '/inbox?tab=dupes&group=k1'
    ]) {
      expect(surfaceFor(url(path), true), path).toBe('grouped');
      // A desktop shows the detail beside the plain list.
      expect(surfaceFor(url(path), false), path).toBe('plain');
    }
    // The detail param is the queue's own: a stray ?song= on Duplicates is still the list.
    expect(surfaceFor(url('/inbox?tab=dupes&song=42'), true)).toBe('plain');
    // The merge queues are decision cards on the grouped background at every width.
    for (const path of ['/inbox?tab=artists', '/inbox?tab=albums']) {
      expect(surfaceFor(url(path), true), path).toBe('grouped');
      expect(surfaceFor(url(path), false), path).toBe('grouped');
    }
  });

  it('leaves every other page plain', () => {
    for (const path of [
      '/overview',
      '/tracks',
      '/library?album=abc',
      '/library?artist=Bj%C3%B6rk',
      '/artists',
      // A prefix match on /track/ only: the Tracks list is a plain page.
      '/tracks',
      '/discover',
      '/wishlist',
      '/playlists',
      '/spotify',
      '/directories'
    ]) {
      expect(surfaceFor(url(path), true), path).toBe('plain');
      expect(surfaceFor(url(path), false), path).toBe('plain');
    }
  });
});

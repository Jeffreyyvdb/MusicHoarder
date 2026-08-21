import { describe, expect, it } from 'vitest';
import { NAV_GROUPS, resolveNav, type NavGroupId } from './nav';

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
  ['/album-quality', 'manage'],
  ['/artists', 'listen'],
  ['/directories', 'manage'],
  ['/discover', 'add'],
  ['/history', 'manage'],
  ['/inbox', 'inbox'],
  ['/library', 'listen'],
  ['/overview', 'listen'],
  ['/performance', 'manage'],
  ['/pipeline', 'manage'],
  ['/playlists', 'add'],
  ['/quality', 'manage'],
  ['/settings', 'manage'],
  ['/spotify', 'add'],
  ['/stats', 'manage'],
  ['/track/42', 'listen'],
  ['/tracks', 'listen'],
  ['/wishlist', 'add']
];

describe('NAV_GROUPS', () => {
  // The IA itself, pinned: results first, then the pile that needs you, then the doors music
  // comes in through, then the machinery. Reordering or renaming a group is a deliberate act.
  it('is Listen / Inbox / Add / Manage, in that order', () => {
    expect(NAV_GROUPS.map((g) => g.label)).toEqual(['Listen', 'Inbox', 'Add', 'Manage']);
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

  it('resolves the Manage machinery pages that used to be orphaned', () => {
    expect(at('/album-quality')?.item?.id).toBe('album-quality');
    expect(at('/directories')?.item?.id).toBe('folders');
    expect(at('/stats')?.item?.id).toBe('stats');
    expect(at('/history')?.item?.id).toBe('history');
  });
});

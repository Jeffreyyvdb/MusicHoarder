import { describe, expect, it } from 'vitest';
import { findTab } from '$lib/nav';
import {
  EMPTY_TAB_MEMORY,
  backTargetFor,
  decideBack,
  findEntry,
  hrefForTab,
  labelFor,
  normUrl,
  parseTabMemory,
  patchEntry,
  reduceTabStacks,
  resetTabStack,
  serializeTabMemory,
  type TabMemoryState
} from './tab-stacks';

const admin = { role: 'Owner' as const, isAdmin: true };
const member = { role: 'Friend' as const, isAdmin: false, capabilities: [] };
type User = typeof admin | typeof member;

const u = (path: string) => new URL(path, 'https://musichoarder.test');

/**
 * Replay a session: each step is [type, path], optionally flagged as a replaceUrl or as a tab-bar
 * switch to a named tab. `from` is always the previous step's path, as afterNavigate reports it.
 */
function replay(
  user: User,
  steps: [type: string, path: string, opts?: { replace?: boolean; tabId?: string }][],
  start: TabMemoryState = EMPTY_TAB_MEMORY
): TabMemoryState {
  let state = start;
  let from: string | null = null;
  for (const [type, path, opts] of steps) {
    state = reduceTabStacks(
      state,
      { type, from: from ? u(from) : null, to: u(path) },
      { user, replace: opts?.replace, tabId: opts?.tabId }
    );
    from = path;
  }
  return state;
}

const urls = (state: TabMemoryState, tab: string) => (state.stacks[tab] ?? []).map((e) => e.url);

describe('normUrl', () => {
  it('keeps path and query, drops origin, hash and a trailing slash', () => {
    expect(normUrl(u('/library/?album=a#top'))).toBe('/library?album=a');
    expect(normUrl('/overview')).toBe('/overview');
  });

  it('strips the one-shot params', () => {
    expect(normUrl('/tracks?song=5&f=mh-liked')).toBe('/tracks?f=mh-liked');
    expect(normUrl('/library?album=a&track=9')).toBe('/library?album=a');
    expect(normUrl('/spotify?spotify_connected=1')).toBe('/spotify');
    expect(normUrl('/spotify?spotify_error=denied')).toBe('/spotify');
  });

  it('keeps ?song= on /inbox, where it is the selected item rather than a one-shot', () => {
    expect(normUrl('/inbox?tab=review&song=5')).toBe('/inbox?tab=review&song=5');
    expect(normUrl('/inbox?tab=ai&song=5')).toBe('/inbox?tab=ai&song=5');
  });

  it('spells a tab-less Inbox detail as the Tag review detail it opens', () => {
    // Otherwise /inbox?song=62 then ?tab=review&song=62 were two entries for one page.
    expect(normUrl('/inbox?song=5')).toBe('/inbox?tab=review&song=5');
    expect(normUrl('/inbox?song=5&tab=dupes')).toBe('/inbox?tab=dupes&song=5');
    // The hub stays bare.
    expect(normUrl('/inbox')).toBe('/inbox');
  });

  it('does not mutate the URL it is given', () => {
    const url = u('/tracks?song=5');
    normUrl(url);
    expect(url.searchParams.get('song')).toBe('5');
  });
});

describe('reduceTabStacks', () => {
  it('starts a stack from a tab root and pushes what is opened from it', () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/library?album=a'],
      ['link', '/library?artist=Bj%C3%B6rk']
    ]);
    expect(s.activeTabId).toBe('listen');
    expect(urls(s, 'listen')).toEqual([
      '/overview',
      '/library?album=a',
      '/library?artist=Bj%C3%B6rk'
    ]);
  });

  it('resets a stack when its root is reached again', () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/tracks'],
      ['link', '/library?album=a'],
      ['goto', '/overview']
    ]);
    expect(urls(s, 'listen')).toEqual(['/overview']);
  });

  it('keeps the root entry (and what it remembered) across a pop to root', () => {
    let s = replay(admin, [['enter', '/overview']]);
    s = patchEntry(s, '/overview', { scrollTop: 640 });
    s = replay(
      admin,
      [
        ['link', '/tracks'],
        ['goto', '/overview']
      ],
      s
    );
    expect(s.stacks.listen).toEqual([{ url: '/overview', scrollTop: 640 }]);
  });

  it('pops on a browser Back to the page below', () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/tracks'],
      ['link', '/library?album=a'],
      ['popstate', '/tracks']
    ]);
    expect(urls(s, 'listen')).toEqual(['/overview', '/tracks']);
  });

  it('truncates a popstate that jumps several entries back', () => {
    const s = replay(admin, [
      ['enter', '/inbox'],
      ['link', '/inbox?tab=review'],
      ['link', '/inbox?tab=review&song=1'],
      ['popstate', '/inbox']
    ]);
    expect(urls(s, 'inbox')).toEqual(['/inbox']);
  });

  it('treats a browser Forward (a popstate to a page not on the stack) as a push', () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['popstate', '/tracks']
    ]);
    expect(urls(s, 'listen')).toEqual(['/overview', '/tracks']);
  });

  it('swaps the top on a replace instead of pushing', () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/tracks'],
      ['goto', '/tracks?f=mh-liked', { replace: true }],
      ['goto', '/tracks?f=mh-liked%2Clyrics', { replace: true }]
    ]);
    expect(urls(s, 'listen')).toEqual(['/overview', '/tracks?f=mh-liked%2Clyrics']);
    expect(s.lastNav?.type).toBe('replace');
  });

  it('collapses a replace that lands on the page below it', () => {
    const s = replay(admin, [
      ['enter', '/inbox'],
      ['link', '/inbox?tab=review'],
      ['link', '/inbox?tab=review&song=1'],
      ['goto', '/inbox?tab=review', { replace: true }]
    ]);
    expect(urls(s, 'inbox')).toEqual(['/inbox', '/inbox?tab=review']);
  });

  it('advances an Inbox detail in place, so Back still returns to the list', () => {
    const s = replay(admin, [
      ['enter', '/inbox'],
      ['link', '/inbox?tab=review'],
      ['link', '/inbox?tab=review&song=1'],
      ['goto', '/inbox?tab=review&song=2', { replace: true }]
    ]);
    expect(urls(s, 'inbox')).toEqual(['/inbox', '/inbox?tab=review', '/inbox?tab=review&song=2']);
    expect(backTargetFor(s, u('/inbox?tab=review&song=2'), admin)?.href).toBe('/inbox?tab=review');
  });

  it("keeps a track page opened from an Inbox detail on the Inbox's stack", () => {
    const s = replay(admin, [
      ['enter', '/inbox'],
      ['link', '/inbox?tab=review'],
      ['link', '/inbox?tab=review&song=7'],
      ['link', '/track/7']
    ]);
    expect(s.activeTabId).toBe('inbox');
    expect(urls(s, 'inbox')).toEqual([
      '/inbox',
      '/inbox?tab=review',
      '/inbox?tab=review&song=7',
      '/track/7'
    ]);
    expect(s.stacks.listen).toBeUndefined();
    expect(backTargetFor(s, u('/track/7'), admin)?.href).toBe('/inbox?tab=review&song=7');
  });

  it('does not push the same page twice', () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/tracks'],
      ['goto', '/tracks']
    ]);
    expect(urls(s, 'listen')).toEqual(['/overview', '/tracks']);
  });

  it('never records a one-shot param', () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/tracks?song=5']
    ]);
    expect(urls(s, 'listen')).toEqual(['/overview', '/tracks']);
  });

  it('starts a deep link a stack of its own', () => {
    const s = replay(admin, [['enter', '/library?album=a']]);
    expect(urls(s, 'listen')).toEqual(['/library?album=a']);
  });

  it('keeps a restored stack on a reload of its top page', () => {
    const before = replay(admin, [
      ['enter', '/overview'],
      ['link', '/tracks'],
      ['link', '/library?album=a']
    ]);
    const restored = parseTabMemory(serializeTabMemory(before));
    expect(restored).not.toBeNull();
    const after = replay(admin, [['enter', '/library?album=a']], restored ?? EMPTY_TAB_MEMORY);
    expect(urls(after, 'listen')).toEqual(['/overview', '/tracks', '/library?album=a']);
  });

  it('keeps one stack per tab across a tab switch', () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/library?album=a'],
      ['goto', '/inbox'],
      ['link', '/inbox?tab=dupes'],
      ['goto', '/library?album=a']
    ]);
    expect(s.activeTabId).toBe('listen');
    expect(urls(s, 'listen')).toEqual(['/overview', '/library?album=a']);
    expect(urls(s, 'inbox')).toEqual(['/inbox', '/inbox?tab=dupes']);
  });

  it("keeps a member's push on the tab it was made from", () => {
    const s = replay(member, [
      ['enter', '/overview'],
      ['link', '/library?album=a']
    ]);
    expect(s.activeTabId).toBe('overview');
    expect(urls(s, 'overview')).toEqual(['/overview', '/library?album=a']);
    expect(s.stacks.albums).toBeUndefined();
  });

  it('files a tab-bar switch under the tab it was made for', () => {
    // A member's Albums stack ends on an album; switching back to it from the Overview must land
    // on the Albums stack, not be claimed by the Overview (which could own any Listen page).
    let s = replay(member, [
      ['enter', '/library'],
      ['link', '/library?album=a'],
      ['goto', '/overview']
    ]);
    s = replay(member, [['goto', '/library?album=a', { tabId: 'albums' }]], s);
    expect(s.activeTabId).toBe('albums');
    expect(urls(s, 'albums')).toEqual(['/library', '/library?album=a']);
    expect(urls(s, 'overview')).toEqual(['/overview']);
  });

  it('does not record a page no tab owns, and keeps the active tab lit', () => {
    const s = replay(member, [
      ['enter', '/tracks'],
      ['link', '/settings?tab=account']
    ]);
    expect(s.activeTabId).toBe('tracks');
    expect(urls(s, 'tracks')).toEqual(['/tracks']);
    expect(s.lastNav).toEqual({ type: 'link', from: '/tracks', to: '/settings?tab=account' });
  });

  it('ignores a navigation that leaves the app', () => {
    const s = replay(admin, [['enter', '/overview']]);
    expect(
      reduceTabStacks(s, { type: 'link', from: u('/overview'), to: null }, { user: admin })
    ).toBe(s);
  });

  it('bounds a stack, keeping its root', () => {
    const steps: [string, string][] = [['enter', '/overview']];
    for (let i = 0; i < 40; i++) steps.push(['link', `/library?album=a${i}`]);
    const stack = urls(replay(admin, steps), 'listen');
    expect(stack.length).toBe(30);
    expect(stack[0]).toBe('/overview');
    expect(stack.at(-1)).toBe('/library?album=a39');
  });
});

describe('backTargetFor', () => {
  it('goes back to where you came from, not to the hierarchical parent', () => {
    // An album opened from an Overview shelf goes back to the Overview, not to Albums.
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/library?album=a']
    ]);
    expect(backTargetFor(s, u('/library?album=a'), admin)).toEqual({
      label: 'Listen',
      href: '/overview'
    });
  });

  it('prefers the title a nav bar gave the entry', () => {
    let s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/library?album=a'],
      ['link', '/library?artist=Bj%C3%B6rk']
    ]);
    s = patchEntry(s, '/library?album=a', { title: 'Homogenic' });
    expect(backTargetFor(s, u('/library?artist=Bj%C3%B6rk'), admin)).toEqual({
      label: 'Homogenic',
      href: '/library?album=a'
    });
  });

  it('falls back to the hierarchical parent on a deep link', () => {
    const s = replay(admin, [['enter', '/library?album=a']]);
    expect(backTargetFor(s, u('/library?album=a'), admin)).toEqual({
      label: 'Albums',
      href: '/library'
    });
  });

  it('uses the parent while the page is not yet recorded (mid-navigation render)', () => {
    const s = replay(admin, [['enter', '/overview']]);
    expect(backTargetFor(s, u('/tracks'), admin)).toEqual({ label: 'Listen', href: '/overview' });
  });

  it('has no target on a tab root', () => {
    const s = replay(admin, [['enter', '/overview']]);
    expect(backTargetFor(s, u('/overview'), admin)).toBeNull();
    expect(backTargetFor(s, u('/inbox'), admin)).toBeNull();
  });

  it("returns an admin's Settings to the tab root the account sheet was opened on", () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/settings']
    ]);
    expect(s.activeTabId).toBe('listen');
    expect(backTargetFor(s, u('/settings'), admin)).toEqual({
      label: 'Listen',
      href: '/overview'
    });
    // A cold load has no stack to return through: the hierarchical parent, the Manage hub.
    const cold = replay(admin, [['enter', '/settings']]);
    expect(backTargetFor(cold, u('/settings'), admin)).toEqual({
      label: 'Manage',
      href: '/manage'
    });
  });

  it("returns a member's unowned page to where it was opened from", () => {
    const s = replay(member, [
      ['enter', '/tracks'],
      ['link', '/settings']
    ]);
    expect(backTargetFor(s, u('/settings'), member)).toEqual({ label: 'Tracks', href: '/tracks' });
    // Reached any other way, it falls back to the Overview.
    const cold = replay(member, [['enter', '/settings']]);
    expect(backTargetFor(cold, u('/settings'), member)).toEqual({
      label: 'Overview',
      href: '/overview'
    });
  });
});

describe('decideBack', () => {
  const push = (from: string, to: string, type = 'link') => ({ type, from, to });

  it('uses history.back() only when the previous entry is exactly the target', () => {
    expect(
      decideBack(push('/overview', '/library?album=a'), u('/library?album=a'), '/overview')
    ).toBe('history');
    expect(decideBack(push('/overview', '/tracks', 'goto'), '/tracks', '/overview')).toBe(
      'history'
    );
  });

  it('compares normalised URLs', () => {
    expect(
      decideBack(push('/tracks', '/library?album=a'), u('/library?album=a&song=3'), '/tracks/')
    ).toBe('history');
  });

  it('replaces instead after a tab switch in between', () => {
    // Overview → album (Listen), Inbox tab, Listen tab: history's previous entry is the Inbox.
    expect(
      decideBack(push('/inbox', '/library?album=a', 'goto'), '/library?album=a', '/overview')
    ).toBe('goto');
  });

  it('replaces instead after a replace, a popstate or a fresh load', () => {
    const target = '/overview';
    const here = '/tracks?f=mh-liked';
    expect(decideBack({ type: 'replace', from: '/overview', to: here }, here, target)).toBe('goto');
    expect(decideBack({ type: 'popstate', from: '/overview', to: here }, here, target)).toBe(
      'goto'
    );
    // The installed app's first page: history.back() would silently do nothing.
    expect(decideBack({ type: 'enter', from: null, to: here }, here, target)).toBe('goto');
    expect(decideBack(null, here, target)).toBe('goto');
  });

  it('replaces instead when the current page is not where the push landed', () => {
    expect(decideBack(push('/overview', '/tracks'), '/artists', '/overview')).toBe('goto');
  });
});

describe('tab helpers', () => {
  const listen = findTab('listen');
  const albums = findTab('albums');

  it('opens a tab at the top of its stack, or at its root', () => {
    if (!listen || !albums) throw new Error('tabs missing');
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/library?album=a']
    ]);
    expect(hrefForTab(s, listen)).toBe('/library?album=a');
    expect(hrefForTab(EMPTY_TAB_MEMORY, listen)).toBe('/overview');
    expect(hrefForTab(s, albums)).toBe('/library');
  });

  it('pops a tab to its root entry, or clears a stack that has none', () => {
    if (!listen) throw new Error('tabs missing');
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/tracks'],
      ['link', '/library?album=a']
    ]);
    expect(urls(resetTabStack(s, listen), 'listen')).toEqual(['/overview']);
    const deep = replay(admin, [['enter', '/library?album=a']]);
    expect(urls(resetTabStack(deep, listen), 'listen')).toEqual([]);
    expect(resetTabStack(EMPTY_TAB_MEMORY, listen)).toBe(EMPTY_TAB_MEMORY);
  });

  it('patches an entry, and returns the same state when nothing changes', () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/tracks']
    ]);
    const patched = patchEntry(s, u('/tracks?song=4'), { scrollTop: 120 });
    expect(findEntry(patched, '/tracks')).toEqual({ url: '/tracks', scrollTop: 120 });
    expect(patchEntry(patched, '/tracks', { scrollTop: 120 })).toBe(patched);
    expect(patchEntry(patched, '/nowhere', { scrollTop: 1 })).toBe(patched);
  });
});

describe('labelFor', () => {
  it('names tab roots after their tab, drill-ins after what they are', () => {
    expect(labelFor('/overview', admin)).toBe('Listen');
    expect(labelFor('/overview', member)).toBe('Overview');
    expect(labelFor('/inbox', admin)).toBe('Inbox');
    expect(labelFor('/library?album=a', admin)).toBe('Album');
    expect(labelFor('/library?artist=Bj%C3%B6rk', admin)).toBe('Björk');
    expect(labelFor('/track/42', admin)).toBe('Track');
    expect(labelFor('/inbox?tab=dupes', admin)).toBe('Duplicate tracks');
    expect(labelFor('/pipeline', admin)).toBe('Pipeline');
  });
});

describe('persistence', () => {
  it('round-trips the stacks and the active tab, not the last navigation', () => {
    const s = replay(admin, [
      ['enter', '/overview'],
      ['link', '/tracks']
    ]);
    const back = parseTabMemory(serializeTabMemory(s));
    expect(back).toEqual({ activeTabId: 'listen', stacks: s.stacks, lastNav: null });
  });

  it('rejects anything that is not the shape it writes', () => {
    expect(parseTabMemory(null)).toBeNull();
    expect(parseTabMemory('not json')).toBeNull();
    expect(parseTabMemory('{"v":99,"stacks":{}}')).toBeNull();
    expect(parseTabMemory('[]')).toBeNull();
  });

  it('drops entries that are not same-origin paths', () => {
    const raw = JSON.stringify({
      v: 1,
      activeTabId: 'listen',
      stacks: {
        listen: [
          { url: '/overview' },
          { url: 'https://evil.test/' },
          { url: '//evil.test/' },
          { url: '/tracks', scrollTop: 'x' },
          { url: '/library?album=a', scrollTop: 50 }
        ],
        junk: 'nope'
      }
    });
    expect(parseTabMemory(raw)?.stacks).toEqual({
      listen: [{ url: '/overview' }, { url: '/library?album=a', scrollTop: 50 }]
    });
  });
});

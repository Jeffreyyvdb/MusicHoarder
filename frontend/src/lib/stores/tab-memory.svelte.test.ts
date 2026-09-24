import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const goto = vi.fn(async (_url: string | URL, _opts?: Record<string, unknown>) => {});
vi.mock('$app/navigation', () => ({ goto }));

import { findTab, type NavTab } from '$lib/nav';

/**
 * The store is module state, so each test gets a fresh copy (vi.resetModules + a dynamic import)
 * and a fresh sessionStorage. SvelteKit is mocked: `goto` only records its arguments, and the
 * tests feed afterNavigate's records by hand, as the (app) layout does.
 */

const ORIGIN = 'https://musichoarder.test';
const u = (path: string) => new URL(path, ORIGIN);

function tab(id: string): NavTab {
  const found = findTab(id);
  if (!found) throw new Error(`no tab ${id}`);
  return found;
}

const admin = { id: 'admin-1', role: 'Owner' as const, isAdmin: true };
const member = { id: 'member-1', role: 'Friend' as const, isAdmin: false, capabilities: [] };

function memoryStorage(): Storage {
  const data = new Map<string, string>();
  return {
    get length() {
      return data.size;
    },
    clear: () => data.clear(),
    getItem: (k) => data.get(k) ?? null,
    key: (i) => [...data.keys()][i] ?? null,
    removeItem: (k) => void data.delete(k),
    setItem: (k, v) => void data.set(k, String(v))
  };
}

let storage: Storage;
let here: string;
const back = vi.fn();

beforeEach(() => {
  vi.resetModules();
  goto.mockClear();
  back.mockClear();
  storage = memoryStorage();
  here = '/overview';
  vi.stubGlobal('sessionStorage', storage);
  vi.stubGlobal('location', {
    get href() {
      return ORIGIN + here;
    }
  });
  vi.stubGlobal('history', { back });
});

afterEach(() => vi.unstubAllGlobals());

async function load() {
  return (await import('./tab-memory.svelte')).tabMemory;
}

type Memory = Awaited<ReturnType<typeof load>>;

/** Navigate: move `location`, then report it the way afterNavigate would. */
function nav(memory: Memory, user: typeof admin | typeof member, type: string, to: string) {
  const from = here;
  here = to;
  memory.record({ type, from: { url: u(from) }, to: { url: u(to) } }, user);
}

describe('tabMemory', () => {
  it('keeps its memory per account, in sessionStorage', async () => {
    let memory = await load();
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);
    nav(memory, admin, 'link', '/library?album=a');
    expect(storage.getItem('mh:tab-memory:admin-1')).toContain('/library?album=a');

    // A reload (fresh module) restores it for the same account…
    vi.resetModules();
    memory = await load();
    memory.init('admin-1');
    expect(memory.activeTabId).toBe('listen');
    expect(memory.hrefFor(tab('listen'))).toBe('/library?album=a');

    // …and never hands it to the next account signed in in the same tab.
    vi.resetModules();
    memory = await load();
    memory.init('member-1');
    expect(memory.activeTabId).toBeNull();
    expect(memory.hrefFor(tab('albums'))).toBe('/library');
  });

  it('survives storage that throws', async () => {
    vi.stubGlobal('sessionStorage', {
      getItem: () => {
        throw new Error('denied');
      },
      setItem: () => {
        throw new Error('denied');
      }
    });
    const memory = await load();
    expect(() =>
      memory.record({ type: 'enter', from: null, to: u('/overview') }, admin)
    ).not.toThrow();
    expect(memory.activeTabId).toBe('listen');
  });

  it('goes back through the history when the previous entry is the target', async () => {
    const memory = await load();
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);
    nav(memory, admin, 'link', '/library?album=a');

    const target = memory.backTarget(u('/library?album=a'), admin);
    expect(target).toEqual({ label: 'Listen', href: '/overview' });
    await memory.goBack(target!);
    expect(back).toHaveBeenCalledOnce();
    expect(goto).not.toHaveBeenCalled();
  });

  it('replaces instead of popping across tabs', async () => {
    const memory = await load();
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);
    nav(memory, admin, 'link', '/library?album=a');
    nav(memory, admin, 'goto', '/inbox');
    nav(memory, admin, 'goto', '/library?album=a');

    const target = memory.backTarget(u('/library?album=a'), admin);
    expect(target?.href).toBe('/overview');
    await memory.goBack(target!);
    expect(back).not.toHaveBeenCalled();
    expect(goto).toHaveBeenCalledWith('/overview', { replaceState: true });
  });

  it('records the replacing Back as a replace, not a push', async () => {
    const memory = await load();
    memory.record({ type: 'enter', from: null, to: u('/library?album=a') }, admin);
    // Deep link: no history, so Back replaces to the parent — and must not leave the album below.
    goto.mockImplementationOnce(async (url) => nav(memory, admin, 'goto', String(url)));
    await memory.goBack(memory.backTarget(u('/library?album=a'), admin)!);
    expect(here).toBe('/library');
    expect(memory.backTarget(u('/library'), admin)).toEqual({ label: 'Listen', href: '/overview' });
    expect(memory.hrefFor(tab('listen'))).toBe('/library');
  });

  it('treats a replaceUrl navigation as a replace', async () => {
    const memory = await load();
    const { replaceUrl: fresh } = await import('$lib/navigation/replace-url');
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);
    nav(memory, admin, 'link', '/tracks');

    goto.mockImplementationOnce(async (url) => nav(memory, admin, 'goto', String(url)));
    await fresh('/tracks?f=mh-liked');
    expect(goto).toHaveBeenLastCalledWith('/tracks?f=mh-liked', {
      replaceState: true,
      noScroll: true,
      keepFocus: true
    });
    // One entry, rewritten — Back goes to the Overview, not to the unfiltered list.
    expect(memory.backTarget(u('/tracks?f=mh-liked'), admin)?.href).toBe('/overview');
  });

  it('ignores a replace flag meant for a different URL', async () => {
    const memory = await load();
    const { markReplace } = await import('./tab-memory.svelte');
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);
    nav(memory, admin, 'link', '/tracks');
    markReplace('/tracks?f=x'); // a replace that never happened…
    nav(memory, admin, 'link', '/library?album=a'); // …must not turn this push into a replace
    expect(memory.backTarget(u('/library?album=a'), admin)?.href).toBe('/tracks');
  });

  it('applies a URL-less replace flag to the next navigation', async () => {
    const memory = await load();
    const { markReplace } = await import('./tab-memory.svelte');
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);
    nav(memory, admin, 'link', '/tracks');
    markReplace();
    nav(memory, admin, 'goto', '/tracks?f=mh-liked');
    expect(memory.backTarget(u('/tracks?f=mh-liked'), admin)?.href).toBe('/overview');
  });

  it('opens another tab where it was left', async () => {
    const memory = await load();
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);
    nav(memory, admin, 'link', '/library?album=a');
    nav(memory, admin, 'goto', '/inbox');

    await memory.select(tab('listen'), u('/inbox'), admin);
    expect(goto).toHaveBeenCalledWith('/library?album=a');
  });

  it('pops the active tab to its root', async () => {
    const memory = await load();
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);
    nav(memory, admin, 'link', '/tracks');
    nav(memory, admin, 'link', '/library?album=a');

    await memory.select(tab('listen'), u('/library?album=a'), admin);
    expect(goto).toHaveBeenCalledWith('/overview');
    expect(memory.hrefFor(tab('listen'))).toBe('/overview');
  });

  it('scrolls to the top instead when the active tab is already at its root', async () => {
    const scrollTo = vi.fn();
    vi.stubGlobal('document', {
      querySelector: () => null,
      querySelectorAll: () => [],
      scrollingElement: { scrollTo }
    });
    vi.stubGlobal('window', { matchMedia: () => ({ matches: false }) });
    const memory = await load();
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);

    await memory.select(tab('listen'), u('/overview'), admin);
    expect(goto).not.toHaveBeenCalled();
    expect(scrollTo).toHaveBeenCalledWith({ top: 0, behavior: 'smooth' });
  });

  it('finds the page scroller in the content column when no nav bar sits inside one', async () => {
    // A page whose bar is still outside its scroller (or that has none) scrolls a pane inside
    // the shell, never the document: the tallest visible one is the page.
    const pane = (clientHeight: number, visible = true) => ({
      clientHeight,
      scrollTo: vi.fn(),
      hasAttribute: (name: string) => name === 'data-scroll-area-viewport',
      getClientRects: () => (visible ? [{}] : [])
    });
    const shelf = pane(180);
    const page = pane(700);
    const hidden = pane(900, false);
    const documentScroller = { scrollTo: vi.fn() };
    vi.stubGlobal('document', {
      querySelector: (sel: string) =>
        sel === '[data-mh-content]' ? { querySelectorAll: () => [shelf, hidden, page] } : null,
      querySelectorAll: () => [],
      scrollingElement: documentScroller
    });
    vi.stubGlobal('window', { matchMedia: () => ({ matches: false }) });
    const memory = await load();
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);

    await memory.select(tab('listen'), u('/overview'), admin);
    expect(page.scrollTo).toHaveBeenCalledWith({ top: 0, behavior: 'smooth' });
    expect(shelf.scrollTo).not.toHaveBeenCalled();
    expect(hidden.scrollTo).not.toHaveBeenCalled();
    expect(documentScroller.scrollTo).not.toHaveBeenCalled();
  });

  it("files a member's tab switch under the tab that was tapped", async () => {
    const memory = await load();
    memory.record({ type: 'enter', from: null, to: u('/library') }, member);
    nav(memory, member, 'link', '/library?album=a');
    nav(memory, member, 'goto', '/overview');

    goto.mockImplementationOnce(async (url) => nav(memory, member, 'goto', String(url)));
    await memory.select(tab('albums'), u('/overview'), member);
    expect(here).toBe('/library?album=a');
    expect(memory.activeTabId).toBe('albums');
    expect(memory.backTarget(u('/library?album=a'), member)?.href).toBe('/library');
  });

  it('remembers scroll positions and titles for Back', async () => {
    const memory = await load();
    memory.record({ type: 'enter', from: null, to: u('/overview') }, admin);
    nav(memory, admin, 'link', '/library?album=a');
    memory.setTitle(u('/library?album=a'), '  Homogenic ');
    nav(memory, admin, 'link', '/library?artist=Bj%C3%B6rk');
    expect(memory.backTarget(u('/library?artist=Bj%C3%B6rk'), admin)?.label).toBe('Homogenic');

    memory.saveScroll(u('/library?album=a'), 812.4);
    const rafs: FrameRequestCallback[] = [];
    vi.stubGlobal('requestAnimationFrame', (cb: FrameRequestCallback) => rafs.push(cb));
    const scroller = { scrollTop: 0, scrollHeight: 4000, clientHeight: 800 };
    vi.stubGlobal('document', {
      querySelector: () => null,
      querySelectorAll: () => [],
      scrollingElement: scroller
    });

    // Back through the history: the page scroller comes back new, so the popstate restores it.
    await memory.goBack(memory.backTarget(u('/library?artist=Bj%C3%B6rk'), admin)!);
    expect(back).toHaveBeenCalledOnce();
    nav(memory, admin, 'popstate', '/library?album=a');
    rafs.shift()?.(0);
    expect(scroller.scrollTop).toBe(812);

    // And a tab switch away and back lands there again.
    scroller.scrollTop = 0;
    nav(memory, admin, 'goto', '/inbox');
    await memory.select(tab('listen'), u('/inbox'), admin);
    expect(goto).toHaveBeenLastCalledWith('/library?album=a');
    rafs.shift()?.(0);
    expect(scroller.scrollTop).toBe(812);
  });
});

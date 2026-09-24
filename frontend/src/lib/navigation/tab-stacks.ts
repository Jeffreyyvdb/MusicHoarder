import {
  backFor,
  isTabRoot,
  resolveNav,
  tabFor,
  tabsFor,
  type NavAudience,
  type NavBack,
  type NavTab
} from '$lib/nav';

/**
 * Per-tab navigation stacks — the pure half of `$lib/stores/tab-memory.svelte.ts`.
 *
 * A phone app keeps one navigation stack per tab: open an album in Listen, switch to Inbox, come
 * back, and Listen is still on the album with Back returning to wherever the album was opened
 * from. The browser has ONE history shared by every tab, so `history.back()` alone pops across
 * tabs (album → Inbox tab → Listen tab → Back lands in the Inbox) and after a replaceState it
 * returns to a page that is not the parent at all. So the stacks are kept here, beside the
 * history, and Back only uses `history.back()` when the history's previous entry provably IS the
 * target (see {@link decideBack}).
 *
 * Everything in this file is pure so the rules can be pinned without SvelteKit or a browser; the
 * store feeds it `afterNavigate` records and owns storage and `goto`.
 */

/** One page on a tab's stack. `url` is normalised (see {@link normUrl}). */
export type TabEntry = { url: string; title?: string; scrollTop?: number };

/**
 * The previous navigation, as the store saw it. `type` is SvelteKit's navigation type, or
 * 'replace' for a navigation made through `replaceUrl` (afterNavigate cannot tell a replaceState
 * goto from a push, which is why replaceUrl flags it).
 */
export type LastNav = { type: string; from: string | null; to: string };

export type TabMemoryState = {
  activeTabId: string | null;
  stacks: Record<string, TabEntry[]>;
  lastNav: LastNav | null;
};

export const EMPTY_TAB_MEMORY: TabMemoryState = { activeTabId: null, stacks: {}, lastNav: null };

/**
 * Parameters that mean "do something once on arrival" rather than "this is where you are".
 * `song`/`track` open the song detail and are stripped by LibraryV2 as soon as they are consumed;
 * the Spotify pair reports an OAuth round-trip. Remembering them would replay the action every
 * time the tab is revisited.
 */
const ONE_SHOT_PARAMS = ['song', 'track', 'spotify_connected', 'spotify_error'];

/** Only path + query matter, so any fixed origin will do for resolving a relative href. */
const BASE = 'http://tab-memory.invalid';

/** A stack can't grow without bound in sessionStorage; iOS itself never shows more than this. */
const MAX_DEPTH = 30;

function toUrl(url: URL | string): URL {
  return typeof url === 'string' ? new URL(url, BASE) : url;
}

/**
 * The comparable form of a URL: path (trailing slash dropped) + query, one-shot params removed,
 * no origin or hash. Every URL the stacks store or compare goes through this.
 *
 * On /inbox, `song` is NOT one-shot: there it is the selected item of a queue (the Inbox detail's
 * own URL), and stripping it would fold the detail into its list. A tab-less `/inbox?song=N` is
 * Tag review's detail (the queue a bare /inbox?song= deep link opens), so it is written as
 * `/inbox?tab=review&song=N` — otherwise the two spellings of one page were two stack entries and
 * Back could land on the page you were already on.
 */
export function normUrl(url: URL | string): string {
  const u = new URL(toUrl(url).href);
  const path =
    u.pathname.length > 1 && u.pathname.endsWith('/') ? u.pathname.slice(0, -1) : u.pathname;
  const inbox = path === '/inbox';
  for (const name of ONE_SHOT_PARAMS) {
    if (inbox && name === 'song') continue;
    u.searchParams.delete(name);
  }
  let params = u.searchParams;
  if (inbox && (params.has('tab') || params.has('song'))) {
    // `tab` first, so both spellings serialise identically.
    const canonical = new URLSearchParams({ tab: params.get('tab') ?? 'review' });
    for (const [k, v] of params) if (k !== 'tab') canonical.append(k, v);
    params = canonical;
  }
  const query = params.toString();
  return query ? `${path}?${query}` : path;
}

/**
 * What to call a page when it is a Back target. On a phone the label is spoken ("Back to …"),
 * not shown, so a plain description is enough; a nav bar that knows a better title (an album
 * name) sets it on the entry instead.
 */
export function labelFor(url: URL | string, user: NavAudience): string {
  const u = toUrl(url);
  if (isTabRoot(u, user)) {
    const tab = tabFor(u, user);
    if (tab) return tab.label;
  }
  const path = normUrl(u).split('?')[0];
  if (path === '/library') {
    if (u.searchParams.get('album')?.trim()) return 'Album';
    const artist = u.searchParams.get('artist')?.trim();
    if (artist) return artist;
  }
  if (path.startsWith('/track/')) return 'Track';
  const match = resolveNav(u);
  return match?.item?.label ?? match?.group.label ?? 'Back';
}

function isPush(type: string): boolean {
  // 'enter' (first load) and 'popstate' say nothing about what the previous history entry is.
  return type === 'link' || type === 'goto' || type === 'form';
}

function lastIndexOfUrl(stack: TabEntry[], url: string): number {
  for (let i = stack.length - 1; i >= 0; i--) if (stack[i].url === url) return i;
  return -1;
}

function capped(stack: TabEntry[]): TabEntry[] {
  // Keep the root: it is what the tab pops back to.
  return stack.length > MAX_DEPTH
    ? [stack[0], ...stack.slice(stack.length - MAX_DEPTH + 1)]
    : stack;
}

export type TabNav = { type: string; from: URL | string | null; to: URL | string | null };

export type TabNavContext = {
  user: NavAudience;
  /** The navigation came from `replaceUrl` (or a Back that fell back to a replacing goto). */
  replace?: boolean;
  /**
   * The tab the navigation was made FOR — set when the tab bar switches tabs. Without it a
   * member's tab switch to, say, an album on the Albums stack would be claimed by the tab they
   * are leaving (tabFor rule 2 lets any Listen page live on any member tab).
   */
  tabId?: string | null;
};

/**
 * Fold one completed navigation into the stacks. Called from `afterNavigate`.
 *
 * With T the tab that owns the destination:
 * - a tab root resets T's stack to just the root;
 * - a replace swaps T's top entry;
 * - a popstate or a fresh load onto a page already on T's stack truncates back to it (Back, and
 *   a reload that restored the stack from sessionStorage);
 * - anything else pushes, unless the page is already on top.
 * A destination no tab owns (a member's /settings) is not recorded and leaves the active tab lit.
 */
export function reduceTabStacks(
  state: TabMemoryState,
  nav: TabNav,
  ctx: TabNavContext
): TabMemoryState {
  if (!nav.to) return state;
  const toUrlValue = toUrl(nav.to);
  const to = normUrl(toUrlValue);
  const lastNav: LastNav = {
    type: ctx.replace ? 'replace' : nav.type,
    from: nav.from ? normUrl(nav.from) : null,
    to
  };

  const forced = ctx.tabId ? tabsFor(ctx.user).find((t) => t.id === ctx.tabId) : undefined;
  const tab = forced ?? tabFor(toUrlValue, ctx.user, state.activeTabId);
  if (!tab) return { ...state, lastNav };

  const stack = state.stacks[tab.id] ?? [];
  const entry: TabEntry = { url: to };
  let next: TabEntry[];

  if (isTabRoot(toUrlValue, ctx.user)) {
    // Keep the old root entry when it is the same page, so its remembered scroll position and
    // title survive a pop to root.
    next = stack[0]?.url === to ? [stack[0]] : [entry];
  } else if (ctx.replace) {
    next = [...stack.slice(0, -1), entry];
    // A replace that lands on the page below it (a list filtered back to what it was) collapses.
    if (next.length > 1 && next[next.length - 2].url === to) next = next.slice(0, -1);
  } else if (nav.type === 'popstate' || nav.type === 'enter') {
    const at = lastIndexOfUrl(stack, to);
    next = at >= 0 ? stack.slice(0, at + 1) : [...stack, entry];
  } else if (stack.at(-1)?.url === to) {
    next = stack;
  } else {
    next = [...stack, entry];
  }

  return {
    activeTabId: tab.id,
    stacks: { ...state.stacks, [tab.id]: capped(next) },
    lastNav
  };
}

/**
 * Where Back goes from `url`: the page below it on its tab's stack — i.e. where you actually
 * came from — else the hierarchical parent from {@link backFor}. Null on a tab root.
 *
 * A page no tab owns (a member's /settings) goes back to the page it was opened from when the
 * last navigation was a push to it, since it sits on no stack.
 */
export function backTargetFor(
  state: TabMemoryState,
  url: URL | string,
  user: NavAudience
): NavBack | null {
  const u = toUrl(url);
  if (isTabRoot(u, user)) return null;
  const here = normUrl(u);
  const tab = tabFor(u, user, state.activeTabId);

  if (tab) {
    const stack = state.stacks[tab.id] ?? [];
    // Only when this page is the recorded top: during a navigation the nav bar renders the new
    // page before afterNavigate records it, and the stack still describes the old one.
    if (stack.length > 1 && stack[stack.length - 1].url === here) {
      const prev = stack[stack.length - 2];
      return { label: prev.title ?? labelFor(prev.url, user), href: prev.url };
    }
  } else {
    const last = state.lastNav;
    if (last && isPush(last.type) && last.to === here && last.from && last.from !== here) {
      return { label: labelFor(last.from, user), href: last.from };
    }
  }
  return backFor(u, user);
}

/**
 * How to execute a Back to `targetHref` from `currentHref`.
 *
 * 'history' — `history.back()` — only when the last navigation was a push from exactly the target
 * to exactly the current page: then the previous history entry IS the target, and going back
 * through the history keeps the browser's Forward (and Android's system Back) coherent.
 * In every other case 'goto': a replacing navigation to the target. That covers a tab switch in
 * between (the previous entry is another tab's page), a replaceState (the previous entry is not
 * the parent), a popstate or a fresh load (unknown previous entry), and the installed app's first
 * page, where `history.back()` is a silent no-op that would strand the user.
 */
export function decideBack(
  lastNav: LastNav | null,
  currentHref: URL | string,
  targetHref: string
): 'history' | 'goto' {
  if (!lastNav || !isPush(lastNav.type) || lastNav.from === null) return 'goto';
  return lastNav.from === normUrl(targetHref) && lastNav.to === normUrl(currentHref)
    ? 'history'
    : 'goto';
}

/** Where a tab opens: the top of its stack, or its root when it has none yet. */
export function hrefForTab(state: TabMemoryState, tab: NavTab): string {
  return state.stacks[tab.id]?.at(-1)?.url ?? tab.root;
}

/** The last entry recorded for `url` on any stack — the active tab's first. */
export function findEntry(state: TabMemoryState, url: URL | string): TabEntry | null {
  const key = normUrl(url);
  const order = [
    ...(state.activeTabId ? [state.activeTabId] : []),
    ...Object.keys(state.stacks).filter((id) => id !== state.activeTabId)
  ];
  for (const id of order) {
    const stack = state.stacks[id] ?? [];
    const at = lastIndexOfUrl(stack, key);
    if (at >= 0) return stack[at];
  }
  return null;
}

/**
 * Return `state` with the entry for `url` patched — on the active tab's stack when it is there,
 * else on whichever stack holds it. Unchanged (the same object) when there is nothing to patch
 * or the patch changes nothing, so a caller in an effect cannot loop on it.
 */
export function patchEntry(
  state: TabMemoryState,
  url: URL | string,
  patch: Partial<Omit<TabEntry, 'url'>>
): TabMemoryState {
  const key = normUrl(url);
  const order = [
    ...(state.activeTabId ? [state.activeTabId] : []),
    ...Object.keys(state.stacks).filter((id) => id !== state.activeTabId)
  ];
  for (const id of order) {
    const stack = state.stacks[id] ?? [];
    const at = lastIndexOfUrl(stack, key);
    if (at < 0) continue;
    const current = stack[at];
    const changed = (Object.keys(patch) as (keyof typeof patch)[]).some(
      (k) => current[k] !== patch[k]
    );
    if (!changed) return state;
    const nextStack = stack.slice();
    nextStack[at] = { ...current, ...patch };
    return { ...state, stacks: { ...state.stacks, [id]: nextStack } };
  }
  return state;
}

/**
 * Pop a tab back to its root entry (the tab bar's "tap the active tab"). The root entry is kept
 * rather than dropped, so the root comes back at the scroll position it was left at — what
 * popping to the root of a UINavigationController does.
 */
export function resetTabStack(state: TabMemoryState, tab: NavTab): TabMemoryState {
  const stack = state.stacks[tab.id];
  if (!stack?.length) return state;
  // A stack that started from a deep link has no root entry to keep.
  const next = stack[0].url === normUrl(tab.root) ? [stack[0]] : [];
  if (next.length === stack.length) return state;
  return { ...state, stacks: { ...state.stacks, [tab.id]: next } };
}

const STORAGE_VERSION = 1;

/** The persisted part: the stacks and the active tab. `lastNav` describes this document only. */
export function serializeTabMemory(state: TabMemoryState): string {
  return JSON.stringify({
    v: STORAGE_VERSION,
    activeTabId: state.activeTabId,
    stacks: state.stacks
  });
}

function isEntry(value: unknown): value is TabEntry {
  if (!value || typeof value !== 'object') return false;
  const e = value as Record<string, unknown>;
  return (
    typeof e.url === 'string' &&
    // Same-origin paths only: these are handed straight to goto().
    e.url.startsWith('/') &&
    !e.url.startsWith('//') &&
    (e.title === undefined || typeof e.title === 'string') &&
    (e.scrollTop === undefined || (typeof e.scrollTop === 'number' && Number.isFinite(e.scrollTop)))
  );
}

/**
 * Read a stored value back. Anything that is not the shape this version writes yields null, and
 * a malformed entry is dropped rather than poisoning its whole stack.
 */
export function parseTabMemory(raw: string | null): TabMemoryState | null {
  if (!raw) return null;
  let data: unknown;
  try {
    data = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!data || typeof data !== 'object') return null;
  const d = data as Record<string, unknown>;
  if (d.v !== STORAGE_VERSION || !d.stacks || typeof d.stacks !== 'object') return null;
  const stacks: Record<string, TabEntry[]> = {};
  for (const [id, value] of Object.entries(d.stacks as Record<string, unknown>)) {
    if (!Array.isArray(value)) continue;
    const entries = value.filter(isEntry).map((e) => ({ ...e, url: normUrl(e.url) }));
    if (entries.length) stacks[id] = capped(entries);
  }
  return {
    activeTabId: typeof d.activeTabId === 'string' ? d.activeTabId : null,
    stacks,
    lastNav: null
  };
}

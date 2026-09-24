/**
 * Tab memory — one navigation stack per compact tab, so switching tabs and coming back returns
 * you to where you were in that tab, and Back returns to where you actually came from rather than
 * wherever the shared browser history happens to point.
 *
 * The rules live in `$lib/navigation/tab-stacks` (pure, unit-tested); this store feeds them from
 * `afterNavigate`, persists them to sessionStorage and executes Back and tab switches.
 *
 * Wiring (the (app) layout):
 *   beforeNavigate(({ from }) => from && tabMemory.captureScroll(from.url));
 *   afterNavigate((nav) => tabMemory.record(nav, page.data.user));
 * The tab bar calls {@link select}; a nav bar reads {@link backTarget} and calls {@link goBack};
 * a URL rewrite that should not add a history entry goes through `replaceUrl`.
 *
 * Storage is per account (`mh:tab-memory:<userId>`), like the player snapshot: an account switch
 * is a hard reload in the same tab, and the next account must never inherit the previous one's
 * album keys or admin routes.
 */
import { untrack } from 'svelte';
import { findTab, isTabRoot, tabFor, type NavAudience, type NavBack, type NavTab } from '$lib/nav';
import { prefersReducedMotion } from '$lib/motion';
import {
  EMPTY_TAB_MEMORY,
  backTargetFor,
  decideBack,
  findEntry,
  hrefForTab,
  normUrl,
  parseTabMemory,
  patchEntry,
  reduceTabStacks,
  resetTabStack,
  serializeTabMemory,
  type TabMemoryState
} from '$lib/navigation/tab-stacks';

export {
  reduceTabStacks,
  decideBack,
  backTargetFor,
  normUrl,
  type TabMemoryState
} from '$lib/navigation/tab-stacks';

const KEY_PREFIX = 'mh:tab-memory:';

let memory = $state.raw<TabMemoryState>(EMPTY_TAB_MEMORY);
/** The account whose memory is loaded; undefined until the first init. */
let loadedFor: string | null | undefined = undefined;

/**
 * A navigation the store has been told about before it happens, keyed by the normalised URL it
 * is going to, so a navigation that is cancelled or overtaken cannot leave a stale flag behind
 * for the next, unrelated one.
 */
let pendingReplace: string | null = null;
/** markReplace() without a URL: the very next navigation, wherever it goes, is the replace. */
const NEXT_NAVIGATION = '\0next';
let pendingTab: { tabId: string; url: string } | null = null;

/**
 * Waiting for the next navigation to land: {@link record} settles these. A Back through the
 * history is fire-and-forget (`history.back()` only queues a popstate), so this is how
 * {@link goBack} can still promise "the page you went back to is on screen" — which the installed
 * app's edge swipe waits on before it lets go of the page it is leaving.
 */
let landingWaiters: (() => void)[] = [];
/** A Back that never lands (nothing to pop to after all) stops being waited on after this. */
const LANDING_TIMEOUT_MS = 1000;

function nextLanding(): Promise<void> {
  return new Promise((resolve) => {
    const done = () => {
      clearTimeout(timer);
      landingWaiters = landingWaiters.filter((w) => w !== done);
      resolve();
    };
    const timer = setTimeout(done, LANDING_TIMEOUT_MS);
    landingWaiters.push(done);
  });
}

function storage(): Storage | null {
  try {
    return typeof sessionStorage === 'undefined' ? null : sessionStorage;
  } catch {
    return null; // storage disabled (some private modes throw on access)
  }
}

function persist(): void {
  if (!loadedFor) return;
  try {
    storage()?.setItem(KEY_PREFIX + loadedFor, serializeTabMemory(memory));
  } catch {
    // best-effort: a full or disabled store only costs the memory across a reload
  }
}

function commit(next: TabMemoryState): void {
  if (next === memory) return;
  memory = next;
  persist();
}

/** Load the memory for an account. A no-op when it is already loaded. */
function init(userId: string | null | undefined): void {
  const id = userId ?? null;
  if (loadedFor === id) return;
  loadedFor = id;
  let restored: TabMemoryState | null = null;
  if (id) {
    try {
      restored = parseTabMemory(storage()?.getItem(KEY_PREFIX + id) ?? null);
    } catch {
      restored = null;
    }
  }
  memory = restored ?? EMPTY_TAB_MEMORY;
}

type UrlLike = URL | { url: URL } | null | undefined;

function urlOf(value: UrlLike): URL | null {
  if (!value) return null;
  return value instanceof URL ? value : value.url;
}

function userIdOf(user: NavAudience): string | undefined {
  const id = (user as { id?: unknown } | null | undefined)?.id;
  return typeof id === 'string' ? id : undefined;
}

/**
 * Fold a completed navigation into the stacks. Call from `afterNavigate`; it takes SvelteKit's
 * navigation object as-is (`from`/`to` may be URLs or navigation targets).
 */
function record(nav: { type: string; from: UrlLike; to: UrlLike }, user: NavAudience): void {
  untrack(() => {
    // The user object carries the id in practice (it is page.data.user); loading here as well
    // makes the order of init() and the first afterNavigate irrelevant.
    const uid = userIdOf(user);
    if (uid !== undefined) init(uid);

    const to = urlOf(nav.to);
    const target = to ? normUrl(to) : null;
    const replace =
      pendingReplace !== null && (pendingReplace === target || pendingReplace === NEXT_NAVIGATION);
    const tabId = pendingTab && pendingTab.url === target ? pendingTab.tabId : null;
    pendingReplace = null;
    pendingTab = null;

    commit(
      reduceTabStacks(
        memory,
        { type: nav.type, from: urlOf(nav.from), to },
        { user, replace, tabId }
      )
    );

    // SvelteKit restores the WINDOW's scroll on Back/Forward, but this app scrolls inside its
    // pages, and the page's scroller comes back new at 0. Put it where it was left.
    if (nav.type === 'popstate' && target) {
      const entry = findEntry(memory, target);
      if (entry?.scrollTop) restoreScroll(entry.scrollTop);
    }

    const landed = landingWaiters;
    landingWaiters = [];
    for (const done of landed) done();
  });
}

/**
 * Flag the next navigation to `url` as a replace. `replaceUrl` calls this; anything else that
 * navigates with `replaceState: true` should too, or the stacks will read it as a push. Pass the
 * URL whenever it is known: without one the flag applies to whatever navigation comes next, so a
 * replace that never happens would turn the following push into one.
 */
export function markReplace(url?: string | URL): void {
  pendingReplace = url === undefined ? NEXT_NAVIGATION : normUrl(url);
}

function clearPending(url: string): void {
  if (pendingReplace === url) pendingReplace = null;
  if (pendingTab?.url === url) pendingTab = null;
}

const SCROLLER_CANDIDATES =
  '[data-scroll-area-viewport], [class*="overflow-y-auto"], [class*="overflow-auto"], ' +
  '[class*="overflow-y-scroll"], [class*="overflow-scroll"]';

function scrollsY(el: Element): boolean {
  // A bits-ui ScrollArea viewport only gets its `overflow-y: scroll` once its scrollbar has
  // mounted, so it is recognised by its attribute as well.
  if (el.hasAttribute('data-scroll-area-viewport')) return true;
  const overflow = getComputedStyle(el).overflowY;
  return overflow === 'auto' || overflow === 'scroll' || overflow === 'overlay';
}

/**
 * The page's primary scroller: the scroll container of the nav bar, which is the first child of
 * it (PageToolbarV2 renders `[data-mh-navbar]` inside the scroller so the large title can scroll
 * away). A page whose bar sits outside its scroller — or that has no bar — still scrolls inside
 * the app shell rather than the document, so the tallest visible scroll container in the content
 * column stands in for it; the document scroller is the last resort (it never scrolls in the
 * shell, but it is the right answer outside it).
 */
export function findPageScroller(): HTMLElement | null {
  if (typeof document === 'undefined') return null;
  for (const bar of document.querySelectorAll<HTMLElement>('[data-mh-navbar]')) {
    // A nav bar hidden at this width (display: none) has no box and no scroller worth using.
    if (!bar.getClientRects().length) continue;
    for (let el = bar.parentElement; el && el !== document.body; el = el.parentElement) {
      if (scrollsY(el)) return el;
    }
  }
  const content = document.querySelector('[data-mh-content]');
  if (content) {
    let best: HTMLElement | null = null;
    // Candidates by markup (a ScrollArea viewport, a Tailwind overflow utility) rather than a
    // computed-style pass over every node: restoreScroll asks once a frame while a page lays out.
    for (const el of content.querySelectorAll<HTMLElement>(SCROLLER_CANDIDATES)) {
      if (!scrollsY(el) || !el.getClientRects().length) continue;
      if (!best || el.clientHeight > best.clientHeight) best = el;
    }
    if (best) return best;
  }
  return (document.scrollingElement as HTMLElement | null) ?? null;
}

/**
 * Put the page back where it was left, once the new page has laid out. The content can arrive a
 * few frames after the navigation resolves (a list rendering from a warm store), so wait for the
 * scroller to be tall enough, for a bounded number of frames, then set it once.
 */
function restoreScroll(top: number): void {
  if (typeof requestAnimationFrame === 'undefined' || !(top > 0)) return;
  let frames = 0;
  const step = () => {
    const el = findPageScroller();
    if (el && (el.scrollHeight - el.clientHeight >= top || frames >= 30)) {
      el.scrollTop = top;
      return;
    }
    if (++frames <= 30) requestAnimationFrame(step);
  };
  requestAnimationFrame(step);
}

function scrollPageToTop(): void {
  findPageScroller()?.scrollTo({ top: 0, behavior: prefersReducedMotion() ? 'auto' : 'smooth' });
}

function saveScroll(url: URL | string, scrollTop: number): void {
  if (!Number.isFinite(scrollTop)) return;
  untrack(() => commit(patchEntry(memory, url, { scrollTop: Math.max(0, Math.round(scrollTop)) })));
}

/** Remember the page scroller's position for `url`. Call from `beforeNavigate` with `from.url`. */
function captureScroll(url: URL | string): void {
  const el = findPageScroller();
  if (el) saveScroll(url, el.scrollTop);
}

/**
 * Give the entry for `url` a better Back label than the generic one — the album's name rather
 * than "Album". Safe to call from an effect: an unchanged title is a no-op.
 */
function setTitle(url: URL | string, title: string): void {
  const clean = title.trim();
  if (!clean) return;
  untrack(() => commit(patchEntry(memory, url, { title: clean })));
}

/**
 * The title a page gave itself with {@link setTitle} (an album's name, an artist's), or null. The
 * browser-tab title reads it too, so the route announcer names the album that opened rather than
 * "Albums".
 */
function titleOf(url: URL | string): string | null {
  return findEntry(memory, url)?.title ?? null;
}

function resetTab(tabId: string): void {
  const tab = findTab(tabId);
  if (!tab) return;
  untrack(() => commit(resetTabStack(memory, tab)));
}

function currentHref(): string | null {
  return typeof location === 'undefined' ? null : location.href;
}

/**
 * Go back to `target` (from {@link backTarget}). Uses `history.back()` only when the previous
 * history entry is provably the target; otherwise replaces the current entry with the target.
 * Either way the page comes back at the scroll position it was left at (see record / below), and
 * the promise settles once the navigation has landed.
 */
async function goBack(target: NavBack): Promise<void> {
  const here = currentHref();
  if (!here) return;
  const lastNav = untrack(() => memory.lastNav);
  if (decideBack(lastNav, here, target.href) === 'history') {
    const landed = nextLanding();
    history.back();
    return landed;
  }
  const entry = untrack(() => findEntry(memory, target.href));
  const key = normUrl(target.href);
  const { goto } = await import('$app/navigation');
  pendingReplace = key;
  try {
    await goto(target.href, { replaceState: true });
  } finally {
    clearPending(key);
  }
  if (entry?.scrollTop) restoreScroll(entry.scrollTop);
}

/**
 * The tab bar's tap handler — the whole of the per-tab behaviour:
 * - another tab → open it where it was left (the top of its stack), at its old scroll position;
 * - the active tab, somewhere inside it → pop it to its root;
 * - the active tab, already at its root → scroll the page to the top (the tap on the status bar
 *   that an installed web app does not get).
 * Pass the current page URL and the signed-in user.
 */
async function select(tab: NavTab, url: URL, user: NavAudience): Promise<void> {
  const active = untrack(() => memory.activeTabId) ?? tabFor(url, user)?.id ?? null;
  // A page no tab owns (a member's /settings) keeps the last tab lit, but tapping that tab
  // should take you back into it, not pop it.
  const here = tabFor(url, user, active);

  if (tab.id === active && here?.id === tab.id && isTabRoot(url, user)) {
    scrollPageToTop();
    return;
  }

  const popToRoot = tab.id === active && here?.id === tab.id;
  if (popToRoot) resetTab(tab.id);
  const href = popToRoot ? tab.root : hrefFor(tab);
  const key = normUrl(href);
  const entry = untrack(() => findEntry(memory, href));

  // Light the tapped tab now rather than when the navigation lands: a tab bar that lags the tap
  // by a page load reads as unresponsive.
  const previous = untrack(() => memory.activeTabId);
  if (previous !== tab.id) untrack(() => commit({ ...memory, activeTabId: tab.id }));

  const { goto } = await import('$app/navigation');
  pendingTab = { tabId: tab.id, url: key };
  try {
    await goto(href);
  } catch (error) {
    untrack(() => commit({ ...memory, activeTabId: previous }));
    throw error;
  } finally {
    clearPending(key);
  }
  if (entry?.scrollTop) restoreScroll(entry.scrollTop);
}

function hrefFor(tab: NavTab): string {
  return hrefForTab(memory, tab);
}

function backTarget(url: URL, user: NavAudience): NavBack | null {
  return backTargetFor(memory, url, user);
}

export const tabMemory = {
  init,
  record,
  /** The tab the user is in. Keeps its value on a page no tab owns, so the bar stays lit. */
  get activeTabId(): string | null {
    return memory.activeTabId;
  },
  hrefFor,
  backTarget,
  goBack,
  select,
  resetTab,
  saveScroll,
  captureScroll,
  setTitle,
  titleOf
};

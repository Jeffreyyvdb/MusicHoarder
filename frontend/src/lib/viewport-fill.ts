/**
 * Building a long grid's tiles only where someone is looking.
 *
 * A grid of hundreds of tiles can give every item its link at once — so the page has its full
 * height, its scroll position and a working link per item from the first frame — and still leave
 * the expensive inside of a tile (cover, labels, buttons, gesture handlers) unbuilt until the tile
 * comes within reach of the screen. Each tile carries an attribute holding its key;
 * {@link watchNearViewport} reports the keys that came within reach, and the grid builds those.
 * Tiles are never un-built: scrolling back over them costs nothing, and memory grows only with
 * what was actually scrolled past.
 *
 * The price is that find-in-page, and anything else reading the DOM, only sees built tiles — so a
 * grid should keep building everything where that matters (a desktop browser).
 *
 * While the scroller is flinging fast, tiles that come within reach are held back briefly instead
 * of built at once: a fling crosses hundreds of tiles in a second, and building (and fetching the
 * covers of) every one of them starves the tiles that actually stay on screen. Held tiles are built
 * as soon as the scroll slows or stops (or after a short cap, so nothing is ever left unbuilt).
 */

/** How far beyond the scroller's edges a tile counts as near: two screens above and below. */
const REACH = '200% 0px';

/** Scroll speed (px/s) above which newly-reached tiles are held back instead of built. */
const FLING_VELOCITY_PX_S = 1800;

/** The velocity is measured over this window of scroll samples. */
const VELOCITY_WINDOW_MS = 120;

/** How often held tiles are re-checked against the current velocity. */
const HOLD_FLUSH_MS = 150;

/** A tile is never held longer than this, even if the scroll never slows. */
const HOLD_MAX_MS = 700;

export type NearViewportOptions = {
  /** The attribute every tile carries; its value is the tile's key. */
  attribute: string;
  /** Keys of the tiles that came within reach, or took focus. May name a tile already built. */
  onnear: (keys: string[]) => void;
  /**
   * Hold tiles back while the scroller is flinging (default true). Off where every tile must
   * exist immediately.
   */
  gateOnVelocity?: boolean;
};

export type NearViewportWatcher = {
  destroy: () => void;
};

/**
 * The element that scrolls `el`. A bits-ui ScrollArea viewport only turns `overflow-y: scroll` on
 * once its scrollbar has mounted, so it is recognised by its attribute too.
 */
function scrollParentOf(el: HTMLElement): HTMLElement | null {
  for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
    if (p.hasAttribute('data-scroll-area-viewport')) return p;
    const { overflowY } = getComputedStyle(p);
    if (overflowY === 'auto' || overflowY === 'scroll' || overflowY === 'overlay') return p;
  }
  return null;
}

export type VelocityTracker = {
  /** Current scroll speed in px/s (absolute value), over the last ~120 ms. */
  velocity: () => number;
  destroy: () => void;
};

/**
 * A rolling scroll-velocity tracker for one scroller (the window when null). Used to hold tile
 * building back while a grid is being flung, and to shrink a list's overscan mid-fling.
 */
export function attachVelocityTracker(scroller: HTMLElement | null): VelocityTracker {
  const samples: [number, number][] = [];
  const read = () =>
    scroller ? scroller.scrollTop : typeof window === 'undefined' ? 0 : (window.scrollY ?? 0);
  const onScroll = () => {
    const now = performance.now();
    samples.push([now, read()]);
    while (samples.length > 0 && now - samples[0][0] > VELOCITY_WINDOW_MS) samples.shift();
  };
  const target: HTMLElement | Window | null =
    scroller ?? (typeof window === 'undefined' ? null : window);
  if (!target) return { velocity: () => 0, destroy: () => {} };
  target.addEventListener('scroll', onScroll, { passive: true });
  return {
    velocity: () => {
      // Age out old samples even when no scroll event arrived (the scroll has stopped): the
      // velocity must decay to 0 once the window slides past the last movement.
      const now = performance.now();
      while (samples.length > 0 && now - samples[0][0] > VELOCITY_WINDOW_MS) samples.shift();
      if (samples.length < 2) return 0;
      const [t0, y0] = samples[0];
      const [t1, y1] = samples[samples.length - 1];
      const dt = t1 - t0;
      return dt > 0 ? Math.abs(y1 - y0) / (dt / 1000) : 0;
    },
    destroy: () => target.removeEventListener('scroll', onScroll)
  };
}

/**
 * Watch the tiles `container` holds right now (the elements carrying `options.attribute`) and
 * report each one's key when it comes within two screens of the visible part of the page, or
 * takes focus. Tiles rendered later are not seen: destroy the watcher and make another when the
 * list changes.
 *
 * One IntersectionObserver for all of them, and no scroll listener other than the velocity
 * tracker. Its root has to be the element that actually scrolls: measured against the window, the
 * margin is clipped by that scroller's own box and nothing ahead of the visible rows would ever
 * count as near. A tile stops being watched once it has been reported, so the observer's work
 * shrinks as the page is scrolled.
 *
 * Focus is reported straight away rather than waiting for the scroll it causes, so tabbing (or
 * Shift+Tab from below the grid, which lands on the very last tile) never rests on an empty one.
 */
export function watchNearViewport(
  container: HTMLElement,
  options: NearViewportOptions
): NearViewportWatcher {
  const { attribute, onnear } = options;
  const gate = options.gateOnVelocity ?? true;
  const selector = `[${attribute}]`;
  const tiles = Array.from(container.querySelectorAll<HTMLElement>(selector));

  // Nothing to watch with: build everything rather than leave tiles empty for good.
  if (typeof IntersectionObserver === 'undefined') {
    const keys = tiles.map((el) => el.getAttribute(attribute)).filter((key) => key !== null);
    if (keys.length > 0) onnear(keys);
    return { destroy: () => {} };
  }

  const root = scrollParentOf(container);
  const tracker = attachVelocityTracker(root);

  const observer = new IntersectionObserver(
    (entries) => {
      const now = performance.now();
      const fast = gate && tracker.velocity() > FLING_VELOCITY_PX_S;
      const keys: string[] = [];
      for (const entry of entries) {
        const key = entry.target.getAttribute(attribute);
        if (key === null) continue;
        if (!entry.isIntersecting) {
          held.delete(key);
          continue;
        }
        if (fast) {
          // Held, not built: keep watching so we notice when it leaves reach again.
          held.set(key, { el: entry.target as HTMLElement, at: now });
          continue;
        }
        observer.unobserve(entry.target);
        held.delete(key);
        keys.push(key);
      }
      if (keys.length > 0) onnear(keys);
    },
    { root, rootMargin: REACH }
  );
  for (const el of tiles) observer.observe(el);

  // Held tiles: flushed when the scroll slows/stops, when they leave reach, or after the cap.
  const held = new Map<string, { el: HTMLElement; at: number }>();
  const flush = () => {
    if (held.size === 0) return;
    const now = performance.now();
    const slow = !gate || tracker.velocity() <= FLING_VELOCITY_PX_S;
    const keys: string[] = [];
    for (const [key, h] of held) {
      if (slow || now - h.at >= HOLD_MAX_MS) {
        observer.unobserve(h.el);
        held.delete(key);
        keys.push(key);
      }
    }
    if (keys.length > 0) onnear(keys);
  };
  // eslint-disable-next-line local/no-bare-poll -- a local flush timer for held tiles, not a server poll
  const timer = setInterval(flush, HOLD_FLUSH_MS);

  const onFocusIn = (event: Event) => {
    const target = event.target as Element | null;
    const tile = target?.closest?.(selector);
    if (!tile || !container.contains(tile)) return;
    const key = tile.getAttribute(attribute);
    if (key === null) return;
    observer.unobserve(tile);
    held.delete(key);
    onnear([key]);
  };
  container.addEventListener('focusin', onFocusIn);

  return {
    destroy: () => {
      observer.disconnect();
      clearInterval(timer);
      container.removeEventListener('focusin', onFocusIn);
      tracker.destroy();
    }
  };
}

/**
 * The built tiles to carry into a list that just changed.
 *
 * Those still in it stay built. A tile that left the list was torn down with its element, and is
 * forgotten: when it comes back — a search cleared, a filter lifted — it waits for the viewport
 * again, instead of every tile ever scrolled past being rebuilt in one go.
 *
 * The first `firstScreen` items join them. The grid builds those in its first frame by position,
 * so a page never opens on empty tiles; recording them here is what keeps a tile built that way
 * from being torn down when a longer list moves it further down.
 *
 * Returns `built` itself when nothing changed, so the caller's state write is a no-op.
 */
export function carryBuiltTiles(
  built: ReadonlySet<string>,
  items: readonly { readonly key: string }[],
  firstScreen: number
): ReadonlySet<string> {
  const next = new Set<string>();
  let added = false;
  for (let i = 0; i < items.length; i++) {
    const { key } = items[i];
    if (built.has(key)) next.add(key);
    else if (i < firstScreen) {
      next.add(key);
      added = true;
    }
  }
  return !added && next.size === built.size ? built : next;
}

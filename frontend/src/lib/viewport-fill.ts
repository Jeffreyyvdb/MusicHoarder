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
 */

/** How far beyond the scroller's edges a tile counts as near: two screens above and below. */
const REACH = '200% 0px';

export type NearViewportOptions = {
  /** The attribute every tile carries; its value is the tile's key. */
  attribute: string;
  /** Keys of the tiles that came within reach, or took focus. May name a tile already built. */
  onnear: (keys: string[]) => void;
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

/**
 * Watch the tiles `container` holds right now (the elements carrying `options.attribute`) and
 * report each one's key when it comes within two screens of the visible part of the page, or
 * takes focus. Tiles rendered later are not seen: destroy the watcher and make another when the
 * list changes.
 *
 * One IntersectionObserver for all of them, and no scroll listener. Its root has to be the element
 * that actually scrolls: measured against the window, the margin is clipped by that scroller's own
 * box and nothing ahead of the visible rows would ever count as near. A tile stops being watched
 * once it has been reported, so the observer's work shrinks as the page is scrolled.
 *
 * Focus is reported straight away rather than waiting for the scroll it causes, so tabbing (or
 * Shift+Tab from below the grid, which lands on the very last tile) never rests on an empty one.
 */
export function watchNearViewport(
  container: HTMLElement,
  options: NearViewportOptions
): NearViewportWatcher {
  const { attribute, onnear } = options;
  const selector = `[${attribute}]`;
  const tiles = Array.from(container.querySelectorAll<HTMLElement>(selector));

  // Nothing to watch with: build everything rather than leave tiles empty for good.
  if (typeof IntersectionObserver === 'undefined') {
    const keys = tiles.map((el) => el.getAttribute(attribute)).filter((key) => key !== null);
    if (keys.length > 0) onnear(keys);
    return { destroy: () => {} };
  }

  const observer = new IntersectionObserver(
    (entries) => {
      const keys: string[] = [];
      for (const entry of entries) {
        if (!entry.isIntersecting) continue;
        observer.unobserve(entry.target);
        const key = entry.target.getAttribute(attribute);
        if (key !== null) keys.push(key);
      }
      if (keys.length > 0) onnear(keys);
    },
    { root: scrollParentOf(container), rootMargin: REACH }
  );
  for (const el of tiles) observer.observe(el);

  const onFocusIn = (event: Event) => {
    const target = event.target as Element | null;
    const tile = target?.closest?.(selector);
    if (!tile || !container.contains(tile)) return;
    const key = tile.getAttribute(attribute);
    if (key === null) return;
    observer.unobserve(tile);
    onnear([key]);
  };
  container.addEventListener('focusin', onFocusIn);

  return {
    destroy: () => {
      observer.disconnect();
      container.removeEventListener('focusin', onFocusIn);
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

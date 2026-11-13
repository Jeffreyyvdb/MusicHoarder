import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { carryBuiltTiles, watchNearViewport } from './viewport-fill';

/**
 * The suite runs in node, so the grid is a few bare EventTargets with just the surface the watcher
 * touches, and IntersectionObserver is a recorder the tests fire by hand. That is enough to pin
 * what decides whether a tile gets built: which element the observer is rooted at, how far its
 * margin reaches, and that a tile is reported once — by coming near, or by taking focus.
 */

const KEY = 'data-album-key';

class FakeElement extends EventTarget {
  parentElement: FakeElement | null = null;
  children: FakeElement[] = [];
  overflowY = 'visible';
  private attrs = new Map<string, string>();

  constructor(attrs: Record<string, string> = {}) {
    super();
    for (const [name, value] of Object.entries(attrs)) this.attrs.set(name, value);
  }
  append(...kids: FakeElement[]): this {
    for (const kid of kids) {
      kid.parentElement = this;
      this.children.push(kid);
    }
    return this;
  }
  hasAttribute(name: string): boolean {
    return this.attrs.has(name);
  }
  getAttribute(name: string): string | null {
    return this.attrs.get(name) ?? null;
  }
  /** Only the `[attribute]` form the watcher uses. */
  private matches(selector: string): boolean {
    return this.attrs.has(selector.slice(1, -1));
  }
  querySelectorAll(selector: string): FakeElement[] {
    return this.children.flatMap((kid) => [
      ...(kid.matches(selector) ? [kid] : []),
      ...kid.querySelectorAll(selector)
    ]);
  }
  closest(selector: string): FakeElement | null {
    if (this.matches(selector)) return this;
    return this.parentElement?.closest(selector) ?? null;
  }
  contains(other: FakeElement | null): boolean {
    for (let el = other; el; el = el.parentElement) if (el === this) return true;
    return false;
  }
  /** Fire a bubbling focusin from this element, as the browser does when it takes focus. */
  focus(): void {
    const fire = (on: FakeElement) => {
      const event = new Event('focusin');
      Object.defineProperty(event, 'target', { value: this });
      on.dispatchEvent(event);
    };
    fire(this);
    for (let el = this.parentElement; el; el = el.parentElement) fire(el);
  }
}

class FakeObserver {
  static made: FakeObserver[] = [];
  watched = new Set<FakeElement>();
  disconnected = false;
  constructor(
    private callback: (entries: { target: FakeElement; isIntersecting: boolean }[]) => void,
    public options: { root: FakeElement | null; rootMargin: string }
  ) {
    FakeObserver.made.push(this);
  }
  observe(el: FakeElement): void {
    this.watched.add(el);
  }
  unobserve(el: FakeElement): void {
    this.watched.delete(el);
  }
  disconnect(): void {
    this.watched.clear();
    this.disconnected = true;
  }
  /** The browser reporting these tiles as inside (or outside) the root's margin. */
  report(tiles: FakeElement[], isIntersecting = true): void {
    this.callback(tiles.map((target) => ({ target, isIntersecting })));
  }
}

/** body > shell > scroller > wrapper > grid > tiles, the shape of the Albums page. */
function page(count: number) {
  const body = new FakeElement();
  const shell = new FakeElement();
  const scroller = new FakeElement({ 'data-scroll-area-viewport': '' });
  const wrapper = new FakeElement();
  const grid = new FakeElement();
  const tiles = Array.from({ length: count }, (_, i) => new FakeElement({ [KEY]: `album-${i}` }));
  body.append(shell.append(scroller.append(wrapper.append(grid.append(...tiles)))));
  vi.stubGlobal('document', { body });
  return { body, shell, scroller, wrapper, grid, tiles };
}

function watch(grid: FakeElement) {
  const onnear = vi.fn<(keys: string[]) => void>();
  const watcher = watchNearViewport(grid as unknown as HTMLElement, { attribute: KEY, onnear });
  return { onnear, watcher, observer: FakeObserver.made[FakeObserver.made.length - 1] };
}

beforeEach(() => {
  FakeObserver.made = [];
  vi.stubGlobal('IntersectionObserver', FakeObserver);
  vi.stubGlobal('getComputedStyle', (el: FakeElement) => ({ overflowY: el.overflowY }));
});

afterEach(() => vi.unstubAllGlobals());

describe('watchNearViewport', () => {
  it('watches every tile with one observer, two screens beyond the page’s own scroller', () => {
    const { grid, scroller, tiles } = page(930);
    const { observer, onnear } = watch(grid);

    expect(FakeObserver.made).toHaveLength(1);
    // Rooted at the element that scrolls: against the window, the margin would be clipped by
    // that scroller's box and nothing ahead of the visible rows would count.
    expect(observer.options.root).toBe(scroller);
    expect(observer.options.rootMargin).toBe('200% 0px');
    expect(observer.watched.size).toBe(930);
    expect([...observer.watched]).toEqual(tiles);
    // Nothing is built just for being watched.
    expect(onnear).not.toHaveBeenCalled();
  });

  it('finds a hand-rolled scroller by its overflow, and settles for the window without one', () => {
    const first = page(2);
    first.scroller.overflowY = 'visible';
    first.wrapper.overflowY = 'auto';
    expect(watch(first.grid).observer.options.root).toBe(first.wrapper);

    // A ScrollArea viewport counts before its scrollbar has turned its overflow on.
    const second = page(2);
    expect(watch(second.grid).observer.options.root).toBe(second.scroller);

    const third = new FakeElement().append(new FakeElement({ [KEY]: 'a' }));
    vi.stubGlobal('document', { body: new FakeElement().append(third) });
    expect(watch(third).observer.options.root).toBeNull();
  });

  it('reports the tiles that come within reach, once, and leaves the rest waiting', () => {
    const { grid, tiles } = page(6);
    const { observer, onnear } = watch(grid);

    // The first notification covers every tile; only two are inside the margin.
    observer.report([tiles[0], tiles[1]]);
    observer.report([tiles[2], tiles[3], tiles[4], tiles[5]], false);
    expect(onnear.mock.calls).toEqual([[['album-0', 'album-1']]]);

    // Reported tiles are no longer watched, so scrolling back over them is free.
    expect([...observer.watched]).toEqual(tiles.slice(2));

    observer.report([tiles[2]]);
    expect(onnear).toHaveBeenLastCalledWith(['album-2']);
    expect(observer.watched.has(tiles[2])).toBe(false);
  });

  it('reports a tile the moment it takes focus, wherever it is', () => {
    const { grid, tiles } = page(930);
    const { observer, onnear } = watch(grid);

    // Shift+Tab from below the grid lands on the very last tile.
    tiles[929].focus();
    expect(onnear.mock.calls).toEqual([[['album-929']]]);
    expect(observer.watched.has(tiles[929])).toBe(false);

    // Focus moving to something inside a tile (its play button) names that tile.
    const button = new FakeElement();
    tiles[500].append(button);
    button.focus();
    expect(onnear).toHaveBeenLastCalledWith(['album-500']);
  });

  it('ignores focus on anything that is not one of its tiles', () => {
    // The grid sits inside something that carries the same attribute, and holds a plain element.
    const stray = new FakeElement();
    const grid = new FakeElement().append(new FakeElement({ [KEY]: 'mine' }), stray);
    const enclosing = new FakeElement({ [KEY]: 'not-mine' }).append(grid);
    vi.stubGlobal('document', { body: new FakeElement().append(enclosing) });
    const { onnear } = watch(grid);

    stray.focus();
    expect(onnear).not.toHaveBeenCalled();
  });

  it('stops watching and listening when destroyed', () => {
    const { grid, tiles } = page(4);
    const { observer, onnear, watcher } = watch(grid);

    watcher.destroy();
    expect(observer.disconnected).toBe(true);
    tiles[3].focus();
    expect(onnear).not.toHaveBeenCalled();
  });

  it('builds everything where there is no IntersectionObserver to wait on', () => {
    vi.stubGlobal('IntersectionObserver', undefined);
    const { grid } = page(3);
    const { onnear, watcher } = watch(grid);
    expect(onnear.mock.calls).toEqual([[['album-0', 'album-1', 'album-2']]]);
    watcher.destroy();
  });
});

describe('carryBuiltTiles', () => {
  const built = new Set(['a', 'b', 'c']);
  const list = (...keys: string[]) => keys.map((key) => ({ key }));

  it('keeps the built tiles that are still in the list, whatever order it is in now', () => {
    // A re-sort: nothing was torn down, so nothing is forgotten — the very same set comes back.
    expect(carryBuiltTiles(built, list('c', 'x', 'a', 'b'), 0)).toBe(built);
  });

  it('forgets the ones that left, so they wait for the viewport again when they return', () => {
    // A search narrowed the list to "b"; clearing it must not rebuild "a" and "c" in one go.
    const kept = carryBuiltTiles(built, list('b', 'x'), 0);
    expect([...kept]).toEqual(['b']);
    expect(carryBuiltTiles(kept, list('a', 'b', 'c', 'x'), 0)).toBe(kept);
  });

  it('forgets everything when the list empties', () => {
    expect(carryBuiltTiles(built, list(), 36).size).toBe(0);
  });

  // The grid builds the first screenful by position. Without recording them, a search that
  // leaves three albums (all built, being first) and is then cleared would tear those tiles down
  // again the moment the full list moved them past the first screenful.
  it('records the first screenful, so a tile built by position stays built when it moves', () => {
    const narrowed = carryBuiltTiles(new Set(), list('m', 'n', 'o'), 2);
    expect([...narrowed]).toEqual(['m', 'n']);

    const cleared = carryBuiltTiles(narrowed, list('a', 'b', 'm', 'n', 'o'), 2);
    expect([...cleared].sort()).toEqual(['a', 'b', 'm', 'n']);
    // Settled: the same list again changes nothing.
    expect(carryBuiltTiles(cleared, list('a', 'b', 'm', 'n', 'o'), 2)).toBe(cleared);
  });
});

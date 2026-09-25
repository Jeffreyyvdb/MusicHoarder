/**
 * Apple Music's playlist page, for a pushed playlist (Discover, Spotify): at rest the hero says
 * the title — big, under the cover — and the nav bar's inline title waits; once the hero's title
 * has scrolled up under the bar, the inline title fades in. Without this the title shows twice,
 * one line apart.
 *
 * Put the action on the hero's heading. It reports `true` while that heading is still on screen
 * below the bar (or further down), `false` once it has gone under it; the page hides the bar's
 * title while it is `true` (a CSS opacity, so VoiceOver still finds the heading).
 *
 * One IntersectionObserver rooted at the page's scroller with the bar's height taken off the top
 * — the same geometry PageToolbarV2 collapses by — so no scroll listener runs.
 */
export function heroTitle(node: HTMLElement, onchange: (visible: boolean) => void) {
  let report = onchange;
  let io: IntersectionObserver | null = null;

  // A bits-ui ScrollArea viewport only turns `overflow-y: scroll` on once its scrollbar has
  // mounted, so it is recognised by its attribute too.
  function scrollParentOf(el: HTMLElement): HTMLElement | null {
    for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
      if (p.hasAttribute('data-scroll-area-viewport')) return p;
      const { overflowY } = getComputedStyle(p);
      if (overflowY === 'auto' || overflowY === 'scroll') return p;
    }
    return null;
  }

  // A frame late: the bar only has its final height once it has laid out.
  const frame = requestAnimationFrame(() => {
    const root = scrollParentOf(node);
    if (!root) return;
    const bar = root.querySelector<HTMLElement>('[data-mh-navbar]');
    io = new IntersectionObserver(
      (entries) => {
        const entry = entries[entries.length - 1];
        const top = entry.rootBounds?.top ?? 0;
        report(entry.isIntersecting || entry.boundingClientRect.bottom > top);
      },
      { root, rootMargin: `-${bar?.offsetHeight ?? 0}px 0px 0px 0px`, threshold: 0 }
    );
    io.observe(node);
  });

  return {
    update(next: (visible: boolean) => void) {
      report = next;
    },
    destroy() {
      cancelAnimationFrame(frame);
      io?.disconnect();
    }
  };
}

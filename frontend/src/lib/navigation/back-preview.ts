/** Visual-only copies of recently visited pages, never a second mounted route or data source. */
type Preview = {
  element: HTMLElement;
  scroll: { element: HTMLElement; top: number; left: number }[];
  width: number;
  appearance: string;
};

const pages = new Map<string, Preview>();
const appearance = () =>
  document.documentElement.className + document.documentElement.dataset.mhSkin;
const key = (href: string) => {
  const url = new URL(href, location.href);
  return url.pathname + url.search;
};

export function copyBackPreview(node: HTMLElement): Preview {
  const element = node.cloneNode(true) as HTMLElement;
  const originals = [node, ...node.querySelectorAll<HTMLElement>('*')];
  const copies = [element, ...element.querySelectorAll<HTMLElement>('*')];
  const scroll: Preview['scroll'] = [];
  for (let i = 0; i < originals.length; i++) {
    const source = originals[i];
    const copy = copies[i];
    copy.removeAttribute('id');
    copy.removeAttribute('data-mh-content');
    if (source.scrollTop || source.scrollLeft)
      scroll.push({ element: copy, top: source.scrollTop, left: source.scrollLeft });
  }
  // Clones must not start media or mount embedded documents.
  element.querySelectorAll('audio, video, iframe, script').forEach((el) => el.remove());
  element.inert = true;
  element.setAttribute('aria-hidden', 'true');
  element.setAttribute('data-mh-back-preview', '');
  Object.assign(element.style, {
    position: 'absolute',
    inset: '0',
    margin: '0',
    width: '100%',
    height: '100%',
    pointerEvents: 'none',
    transition: 'none',
    transform: 'none',
    opacity: '1',
    background: getComputedStyle(node.parentElement!).backgroundColor
  });
  return { element, scroll, width: node.clientWidth, appearance: appearance() };
}

export function mountBackPreview(preview: Preview, parent: HTMLElement): HTMLElement {
  parent.appendChild(preview.element);
  // cloneNode does not copy scroll offsets; restore only after the copy has a layout box.
  for (const { element, top, left } of preview.scroll) {
    element.scrollTop = top;
    element.scrollLeft = left;
  }
  return preview.element;
}

export const backPreviews = {
  capture(href: string, node: HTMLElement) {
    pages.delete(key(href));
    pages.set(key(href), copyBackPreview(node));
    // Bound detached DOM memory, even after a long browsing session.
    if (pages.size > 4) pages.delete(pages.keys().next().value!);
  },
  get(href: string | null, node: HTMLElement): Preview | null {
    if (!href) return null;
    const preview = pages.get(key(href));
    if (!preview || preview.width !== node.clientWidth || preview.appearance !== appearance())
      return null;
    return preview;
  },
  clear() {
    pages.clear();
  }
};

export interface ScrubOptions {
  count: number;
  index: number | null;
  onchange: (index: number | null) => void;
}

export function indexAt(
  clientX: number,
  left: number,
  width: number,
  count: number
): number | null {
  if (width <= 0 || count < 1) return null;
  return Math.min(count - 1, Math.max(0, Math.floor(((clientX - left) / width) * count)));
}

export function indexAfterKey(
  key: string,
  current: number | null,
  count: number
): number | null | undefined {
  if (count < 1) return undefined;
  switch (key) {
    case 'ArrowLeft':
    case 'ArrowDown':
      return current === null ? count - 1 : Math.max(0, current - 1);
    case 'ArrowRight':
    case 'ArrowUp':
      return current === null ? count - 1 : Math.min(count - 1, current + 1);
    case 'Home':
      return 0;
    case 'End':
      return count - 1;
    case 'Escape':
      return null;
    default:
      return undefined;
  }
}

export function scrub(node: HTMLElement, initial: ScrubOptions) {
  let options = initial;

  function at(event: PointerEvent): number | null {
    const box = node.getBoundingClientRect();
    return indexAt(event.clientX, box.left, box.width, options.count);
  }

  function onpointermove(event: PointerEvent) {
    if (event.pointerType === 'mouse' || event.buttons > 0) options.onchange(at(event));
  }
  function onpointerdown(event: PointerEvent) {
    options.onchange(at(event));
  }
  function onpointerleave(event: PointerEvent) {
    if (event.pointerType === 'mouse') options.onchange(null);
  }
  function onkeydown(event: KeyboardEvent) {
    const next = indexAfterKey(event.key, options.index, options.count);
    if (next === undefined) return;
    event.preventDefault();
    options.onchange(next);
  }
  function onfocus() {
    if (options.index === null && node.matches(':focus-visible') && options.count > 0) {
      options.onchange(options.count - 1);
    }
  }

  node.addEventListener('pointermove', onpointermove);
  node.addEventListener('pointerdown', onpointerdown);
  node.addEventListener('pointerleave', onpointerleave);
  node.addEventListener('keydown', onkeydown);
  node.addEventListener('focus', onfocus);

  return {
    update(next: ScrubOptions) {
      options = next;
    },
    destroy() {
      node.removeEventListener('pointermove', onpointermove);
      node.removeEventListener('pointerdown', onpointerdown);
      node.removeEventListener('pointerleave', onpointerleave);
      node.removeEventListener('keydown', onkeydown);
      node.removeEventListener('focus', onfocus);
    }
  };
}

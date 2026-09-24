import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { HOLD_MS, longpress } from './long-press';

/**
 * The suite runs in node, so the element is a bare EventTarget with just the style and query
 * surface the action touches, and pointer events are plain Events carrying pointer fields. That
 * is enough to pin the timing and cancellation rules — the part that decides whether a flick
 * through a list opens a menu.
 */

class FakeStyle {
  transform = '';
  transition = '';
  private props = new Map<string, string>();
  getPropertyValue(name: string): string {
    return this.props.get(name) ?? '';
  }
  setProperty(name: string, value: string): void {
    this.props.set(name, value);
  }
  removeProperty(name: string): void {
    this.props.delete(name);
  }
}

class FakeElement extends EventTarget {
  style = new FakeStyle();
  images = [{ draggable: true }];
  querySelectorAll() {
    return this.images;
  }
  contains(other: unknown) {
    return other === this;
  }
}

function pointer(
  type: string,
  init: {
    pointerType?: string;
    pointerId?: number;
    x?: number;
    y?: number;
    isPrimary?: boolean;
  } = {}
): Event {
  return Object.assign(new Event(type, { cancelable: true }), {
    pointerType: init.pointerType ?? 'touch',
    pointerId: init.pointerId ?? 1,
    isPrimary: init.isPrimary ?? true,
    clientX: init.x ?? 100,
    clientY: init.y ?? 200
  });
}

let node: FakeElement;
let doc: EventTarget;
let vibrate: ReturnType<typeof vi.fn>;

beforeEach(() => {
  vi.useFakeTimers();
  node = new FakeElement();
  doc = new EventTarget();
  vibrate = vi.fn();
  vi.stubGlobal('document', doc);
  vi.stubGlobal('navigator', { vibrate });
  vi.stubGlobal('window', { matchMedia: () => ({ matches: false }) });
});

afterEach(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

function attach(onlongpress = vi.fn(), disabled = false) {
  const action = longpress(node as unknown as HTMLElement, { onlongpress, disabled });
  return { onlongpress, action };
}

describe('longpress', () => {
  it('fires after a still hold, at the touch point, with a haptic tick', () => {
    const { onlongpress } = attach();
    node.dispatchEvent(pointer('pointerdown', { x: 40, y: 60 }));
    vi.advanceTimersByTime(HOLD_MS - 1);
    expect(onlongpress).not.toHaveBeenCalled();
    vi.advanceTimersByTime(1);
    expect(onlongpress).toHaveBeenCalledWith({ x: 40, y: 60 });
    expect(vibrate).toHaveBeenCalledWith(10);
  });

  it('tolerates a small wobble but not a drag', () => {
    const { onlongpress } = attach();
    node.dispatchEvent(pointer('pointerdown', { x: 100, y: 100 }));
    node.dispatchEvent(pointer('pointermove', { x: 106, y: 106 }));
    vi.advanceTimersByTime(HOLD_MS);
    expect(onlongpress).toHaveBeenCalledOnce();

    node.dispatchEvent(pointer('pointerdown', { x: 100, y: 100 }));
    node.dispatchEvent(pointer('pointermove', { x: 100, y: 111 }));
    vi.advanceTimersByTime(HOLD_MS);
    expect(onlongpress).toHaveBeenCalledOnce();
  });

  it('is cancelled by lifting early, by the browser taking the pointer, and by a scroll', () => {
    const { onlongpress } = attach();
    node.dispatchEvent(pointer('pointerdown'));
    vi.advanceTimersByTime(HOLD_MS / 2);
    node.dispatchEvent(pointer('pointerup'));
    vi.advanceTimersByTime(HOLD_MS);

    node.dispatchEvent(pointer('pointerdown'));
    node.dispatchEvent(pointer('pointercancel'));
    vi.advanceTimersByTime(HOLD_MS);

    node.dispatchEvent(pointer('pointerdown'));
    doc.dispatchEvent(new Event('scroll'));
    vi.advanceTimersByTime(HOLD_MS);

    expect(onlongpress).not.toHaveBeenCalled();
  });

  it('leaves the mouse to the element’s own contextmenu', () => {
    const { onlongpress } = attach();
    node.dispatchEvent(pointer('pointerdown', { pointerType: 'mouse' }));
    vi.advanceTimersByTime(HOLD_MS);
    expect(onlongpress).not.toHaveBeenCalled();

    const menu = pointer('contextmenu', { pointerType: 'mouse' });
    const own = vi.fn();
    node.addEventListener('contextmenu', own);
    node.dispatchEvent(menu);
    expect(menu.defaultPrevented).toBe(false);
    expect(own).toHaveBeenCalledOnce();
  });

  it('cancels on a second finger', () => {
    const { onlongpress } = attach();
    node.dispatchEvent(pointer('pointerdown', { pointerId: 1 }));
    node.dispatchEvent(pointer('pointerdown', { pointerId: 2, isPrimary: false }));
    vi.advanceTimersByTime(HOLD_MS);
    expect(onlongpress).not.toHaveBeenCalled();
  });

  it('swallows the click the lift delivers, and only that one', () => {
    attach();
    const onclick = vi.fn();
    node.addEventListener('click', onclick);

    node.dispatchEvent(pointer('pointerdown'));
    vi.advanceTimersByTime(HOLD_MS);
    node.dispatchEvent(pointer('pointerup'));
    const swallowed = new Event('click', { cancelable: true });
    node.dispatchEvent(swallowed);
    expect(swallowed.defaultPrevented).toBe(true);
    expect(onclick).not.toHaveBeenCalled();

    node.dispatchEvent(new Event('click', { cancelable: true }));
    expect(onclick).toHaveBeenCalledOnce();
  });

  it('forgets the swallow if no click follows the lift', () => {
    attach();
    const onclick = vi.fn();
    node.addEventListener('click', onclick);
    node.dispatchEvent(pointer('pointerdown'));
    vi.advanceTimersByTime(HOLD_MS);
    node.dispatchEvent(pointer('pointerup'));
    vi.advanceTimersByTime(1000);
    node.dispatchEvent(new Event('click', { cancelable: true }));
    expect(onclick).toHaveBeenCalledOnce();
  });

  it("takes Android's touch contextmenu as the long-press instead of opening twice", () => {
    const { onlongpress } = attach();
    const own = vi.fn();
    node.addEventListener('contextmenu', own);
    node.dispatchEvent(pointer('pointerdown'));
    vi.advanceTimersByTime(HOLD_MS - 50);
    const menu = pointer('contextmenu');
    node.dispatchEvent(menu);
    expect(menu.defaultPrevented).toBe(true);
    expect(own).not.toHaveBeenCalled();
    expect(onlongpress).toHaveBeenCalledOnce();
    vi.advanceTimersByTime(HOLD_MS);
    expect(onlongpress).toHaveBeenCalledOnce();
  });

  it('shrinks the element while held and settles it back', () => {
    attach();
    node.dispatchEvent(pointer('pointerdown'));
    expect(node.style.transform).toBe('');
    vi.advanceTimersByTime(150);
    expect(node.style.transform).toBe('scale(0.97)');
    vi.advanceTimersByTime(HOLD_MS);
    expect(node.style.transform).toBe('');
  });

  it('turns off the iOS callout and selection, and image dragging', () => {
    const { action } = attach();
    expect(node.style.getPropertyValue('-webkit-touch-callout')).toBe('none');
    expect(node.style.getPropertyValue('user-select')).toBe('none');
    expect(node.images[0].draggable).toBe(false);
    action.destroy();
    expect(node.style.getPropertyValue('-webkit-touch-callout')).toBe('');
  });

  it('does nothing while disabled, and resumes when enabled', () => {
    const { onlongpress, action } = attach(vi.fn(), true);
    expect(node.style.getPropertyValue('user-select')).toBe('');
    node.dispatchEvent(pointer('pointerdown'));
    vi.advanceTimersByTime(HOLD_MS);
    expect(onlongpress).not.toHaveBeenCalled();

    action.update({ onlongpress, disabled: false });
    node.dispatchEvent(pointer('pointerdown'));
    vi.advanceTimersByTime(HOLD_MS);
    expect(onlongpress).toHaveBeenCalledOnce();
  });

  it('stops listening when destroyed', () => {
    const { onlongpress, action } = attach();
    node.dispatchEvent(pointer('pointerdown'));
    action.destroy();
    vi.advanceTimersByTime(HOLD_MS);
    node.dispatchEvent(pointer('pointerdown'));
    vi.advanceTimersByTime(HOLD_MS);
    expect(onlongpress).not.toHaveBeenCalled();
  });
});

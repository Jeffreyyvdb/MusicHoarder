import { afterEach, describe, expect, it, vi } from 'vitest';
import { playerSwipe } from './player-swipe';
class Bar extends EventTarget {
  captured = false;
  hasPointerCapture() {
    return this.captured;
  }
  setPointerCapture() {
    this.captured = true;
  }
  releasePointerCapture() {
    this.captured = false;
  }
}
function setup() {
  const node = new Bar();
  const options = {
    onnext: vi.fn(),
    onprevious: vi.fn(),
    onopen: vi.fn(),
    ondrag: vi.fn(),
    onsettle: vi.fn(),
    disabled: false
  };
  const action = playerSwipe(node as unknown as HTMLElement, options);
  const send = (type: string, x: number, y = 0, extra = {}) => {
    const e = Object.assign(new Event(type, { cancelable: true }), {
      pointerId: 1,
      pointerType: 'touch',
      isPrimary: true,
      clientX: x,
      clientY: y,
      ...extra
    });
    node.dispatchEvent(e);
    return e;
  };
  return { node, options, action, send };
}
afterEach(() => vi.useRealTimers());
describe('compact player swipes', () => {
  it('keeps a swipe when implicit capture transfers from a child button to the bar', () => {
    const { node, send, options } = setup();
    send('pointerdown', 100);
    send('pointermove', 20);
    const lost = Object.assign(new Event('lostpointercapture'), { pointerId: 1 });
    Object.defineProperty(lost, 'target', { value: new EventTarget() });
    node.dispatchEvent(lost);
    send('pointerup', 20);
    expect(options.onnext).toHaveBeenCalledOnce();
  });
  it.each([
    [-70, 'onnext'],
    [70, 'onprevious']
  ] as const)('commits %s once on release', (dx, key) => {
    const { send, options } = setup();
    send('pointerdown', 100);
    send('pointermove', 100 + dx);
    send('pointermove', 100 + dx * 2);
    expect(options[key]).not.toHaveBeenCalled();
    send('pointerup', 100 + dx);
    send('pointerup', 100 + dx);
    expect(options[key]).toHaveBeenCalledTimes(1);
    expect(options.onopen).not.toHaveBeenCalled();
  });
  it('preserves swipe up; ignores down, short and diagonal gestures', () => {
    const { send, options } = setup();
    for (const [dx, dy] of [
      [0, 60],
      [30, 0],
      [60, 60]
    ]) {
      send('pointerdown', 100, 100);
      send('pointermove', 100 + dx, 100 + dy);
      send('pointerup', 100 + dx, 100 + dy);
    }
    expect(options.onnext).not.toHaveBeenCalled();
    expect(options.onprevious).not.toHaveBeenCalled();
    expect(options.onopen).not.toHaveBeenCalled();
    send('pointerdown', 100, 100);
    send('pointerup', 100, 60);
    expect(options.onopen).toHaveBeenCalledOnce();
  });
  it('swallows drag clicks but permits keyboard clicks and the next tap', () => {
    const { node, send } = setup();
    send('pointerdown', 100);
    send('pointermove', 30);
    send('pointerup', 30);
    const click = (detail: number) => {
      const e = Object.assign(new Event('click', { cancelable: true }), { detail });
      node.dispatchEvent(e);
      return e.defaultPrevented;
    };
    expect(click(0)).toBe(false);
    expect(click(1)).toBe(true);
    send('pointerdown', 100);
    send('pointerup', 100);
    expect(click(1)).toBe(false);
  });
  it.each(['pointercancel', 'lostpointercapture'])('does not skip on %s', (end) => {
    const { send, options } = setup();
    send('pointerdown', 100);
    send('pointermove', 20);
    send(end, 20);
    send('pointerup', 20);
    expect(options.onnext).not.toHaveBeenCalled();
  });
  it('ignores mouse, multiple fingers and an open long-press menu', () => {
    const { send, options, action } = setup();
    send('pointerdown', 100, 0, { pointerType: 'mouse' });
    send('pointerup', 0);
    send('pointerdown', 100);
    send('pointerdown', 100, 0, { isPrimary: false, pointerId: 2 });
    send('pointerup', 0);
    send('pointerdown', 100);
    action.update({ ...options, disabled: true });
    send('pointerup', 0);
    expect(options.onnext).not.toHaveBeenCalled();
    action.destroy();
  });
  it('expires suppression when a cancelled drag produces no click', () => {
    vi.useFakeTimers();
    const { node, send, action } = setup();
    send('pointerdown', 100);
    send('pointermove', 20);
    send('pointercancel', 20);
    vi.advanceTimersByTime(801);
    const click = Object.assign(new Event('click', { cancelable: true }), { detail: 1 });
    node.dispatchEvent(click);
    expect(click.defaultPrevented).toBe(false);
    action.destroy();
  });

  it('reports the raw distance of horizontal moves, after the slop only', () => {
    const { send, options } = setup();
    send('pointerdown', 100, 100);
    send('pointermove', 105, 100);
    expect(options.ondrag).not.toHaveBeenCalled();
    send('pointermove', 70, 104);
    send('pointermove', 40, 104);
    expect(options.ondrag.mock.calls).toEqual([[-30], [-60]]);
  });

  it('does not report a vertical drag', () => {
    const { send, options } = setup();
    send('pointerdown', 100, 100);
    send('pointermove', 104, 60);
    send('pointerup', 104, 60);
    expect(options.ondrag).not.toHaveBeenCalled();
    expect(options.onsettle).not.toHaveBeenCalled();
    expect(options.onopen).toHaveBeenCalledOnce();
  });

  it('keeps tracking a slow horizontal reversal through the starting point', () => {
    const { send, options } = setup();
    send('pointerdown', 100, 100);
    for (const offset of [20, 10, 5, 0, -5, -10, -20]) {
      send('pointermove', 100 + offset, 104);
    }
    expect(options.ondrag.mock.calls).toEqual([[20], [10], [5], [0], [-5], [-10], [-20]]);
    send('pointerup', 80, 104);
    expect(options.onsettle).toHaveBeenCalledOnce();
    expect(options.onnext).not.toHaveBeenCalled();
  });

  it('settles when a drag lets go short of the threshold', () => {
    const { send, options } = setup();
    send('pointerdown', 100);
    send('pointermove', 70);
    send('pointerup', 70);
    expect(options.onsettle).toHaveBeenCalledOnce();
    expect(options.onnext).not.toHaveBeenCalled();
  });

  it('settles when the browser takes the touch away', () => {
    const { send, options } = setup();
    send('pointerdown', 100);
    send('pointermove', 20);
    send('pointercancel', 20);
    expect(options.onsettle).toHaveBeenCalledOnce();
    expect(options.onnext).not.toHaveBeenCalled();
  });

  it('does not settle when the drag commits', () => {
    const { send, options } = setup();
    send('pointerdown', 100);
    send('pointermove', 30);
    send('pointerup', 30);
    expect(options.onnext).toHaveBeenCalledOnce();
    expect(options.onsettle).not.toHaveBeenCalled();
  });
});

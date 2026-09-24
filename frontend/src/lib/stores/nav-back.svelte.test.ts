import { describe, expect, it, vi } from 'vitest';
import { navBack } from './nav-back.svelte';

describe('navBack', () => {
  it('publishes the latest action and clears it on cleanup', () => {
    const back = vi.fn();
    const clear = navBack.set(back);
    navBack.current?.();
    expect(back).toHaveBeenCalledOnce();
    clear();
    expect(navBack.current).toBeNull();
  });

  it("does not let an outgoing nav bar's cleanup wipe the incoming one's action", () => {
    const outgoing = navBack.set(() => {});
    const incoming = vi.fn();
    const clearIncoming = navBack.set(incoming);
    outgoing();
    expect(navBack.current).toBe(incoming);
    clearIncoming();
    expect(navBack.current).toBeNull();
  });
});

import { untrack } from 'svelte';

/**
 * The active nav bar's Back action, published for the one thing that needs it without rendering
 * the nav bar: the installed app's edge-swipe-back gesture. PageToolbarV2 publishes its action
 * while it shows a back button; the gesture runs exactly what tapping the button would, so the
 * two can never disagree about where Back goes.
 *
 * Usage, in the nav bar:
 *   $effect(() => navBack.set(back ? () => void tabMemory.goBack(back) : null));
 */

let current = $state.raw<(() => void) | null>(null);

export const navBack = {
  get current(): (() => void) | null {
    return current;
  },
  /**
   * Publish `fn` (or clear with null). The returned cleanup clears it only if it is still the
   * published action — when two nav bars overlap during a navigation, the outgoing one's cleanup
   * must not wipe the incoming one's action.
   */
  set(fn: (() => void) | null): () => void {
    untrack(() => {
      current = fn;
    });
    return () => {
      untrack(() => {
        if (current === fn) current = null;
      });
    };
  }
};

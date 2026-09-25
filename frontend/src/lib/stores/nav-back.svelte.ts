import { untrack } from 'svelte';

/**
 * The active nav bar's Back action, published for the one thing that needs it without rendering
 * the nav bar: the installed app's edge-swipe-back gesture. PageToolbarV2 publishes its action
 * while it shows a back button; the gesture runs exactly what tapping the button would, so the
 * two can never disagree about where Back goes.
 *
 * The action may return a promise that settles once the page it goes back to is on screen, as
 * `tabMemory.goBack` does; the gesture holds the page it is leaving until then.
 *
 * Usage, in the nav bar:
 *   $effect(() => navBack.set(back ? () => tabMemory.goBack(back) : null));
 */

export type NavBackAction = () => void | Promise<void>;

let current = $state.raw<NavBackAction | null>(null);

export const navBack = {
  get current(): NavBackAction | null {
    return current;
  },
  /**
   * Publish `fn` (or clear with null). The returned cleanup clears it only if it is still the
   * published action — when two nav bars overlap during a navigation, the outgoing one's cleanup
   * must not wipe the incoming one's action.
   */
  set(fn: NavBackAction | null): () => void {
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

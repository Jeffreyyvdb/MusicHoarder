import { untrack } from 'svelte';

function claims() {
  let count = $state(0);
  return {
    get held(): boolean {
      return count > 0;
    },
    claim(): () => void {
      untrack(() => {
        count += 1;
      });
      let released = false;
      return () => {
        if (released) return;
        released = true;
        untrack(() => {
          count = Math.max(0, count - 1);
        });
      };
    }
  };
}

const media = claims();
const tv = claims();

/**
 * The page-surface facts a URL cannot tell (`surfaceFor` in `#lib/nav.ts` derives the rest), for the
 * installed app's status bar. Counters of claims rather than flags, so an overlay that remounts (or
 * two that overlap during a transition) cannot clear the other's claim.
 *
 * Usage, in the overlay:
 *   $effect(() => (open ? themeSurface.claimMedia() : undefined));
 */
export const themeSurface = {
  get media(): boolean {
    return media.held;
  },
  claimMedia: media.claim,
  get tv(): boolean {
    return tv.held;
  },
  claimTv: tv.claim
};

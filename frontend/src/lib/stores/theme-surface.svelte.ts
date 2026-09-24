import { untrack } from 'svelte';

/**
 * The one page-surface fact a URL cannot tell: Now Playing is open. Its media appearance is dark
 * over the cover whatever the app theme, so while it is up the installed app's status bar (painted
 * from theme-color) must be black too. Everything else about the surface is derived from the URL
 * by `surfaceFor` in `$lib/nav`; this holds only the override.
 *
 * A counter of claims rather than a flag, so an overlay that remounts (or two that overlap during
 * a transition) cannot clear the other's claim.
 *
 * Usage, in the overlay:
 *   $effect(() => (open ? themeSurface.claimMedia() : undefined));
 */

let mediaClaims = $state(0);

export const themeSurface = {
  get media(): boolean {
    return mediaClaims > 0;
  },
  /** Claim the media surface. Returns the release; calling it more than once is harmless. */
  claimMedia(): () => void {
    untrack(() => {
      mediaClaims += 1;
    });
    let released = false;
    return () => {
      if (released) return;
      released = true;
      untrack(() => {
        mediaClaims = Math.max(0, mediaClaims - 1);
      });
    };
  }
};

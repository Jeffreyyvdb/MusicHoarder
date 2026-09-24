import { untrack } from 'svelte';

/**
 * Who owns the bottom slot on a phone: the tab bar (the default), a view's own toolbar (an Inbox
 * decision toolbar hides the tab bar the way `hidesBottomBarWhenPushed` does), or nothing.
 *
 * Claims stack: the last one wins, and releasing a claim restores whatever was there before it —
 * so two views that both claim (a detail replaced by the next detail while the first unmounts)
 * cannot leave the slot in the wrong state whatever order they mount and unmount in. AppShellV2
 * reads `kind` into `data-mh-bar`, which is what the content padding and the MiniPlayer's offset
 * derive from.
 *
 * Usage, in the component that owns the toolbar:
 *   $effect(() => bottomBar.claim('toolbar'));   // the returned release is the effect cleanup
 */

export type BottomBarKind = 'tabs' | 'toolbar' | 'none';

type Claim = { token: object; kind: Exclude<BottomBarKind, 'tabs'> };

let claims = $state.raw<Claim[]>([]);

export const bottomBar = {
  get kind(): BottomBarKind {
    return claims.at(-1)?.kind ?? 'tabs';
  },
  /** Take the slot. Returns the release; calling it more than once is harmless. */
  claim(kind: Exclude<BottomBarKind, 'tabs'>): () => void {
    const token = {};
    // Untracked: claim() is called from effects, and reading `claims` there would make the effect
    // depend on the very state it writes — re-running, releasing and re-claiming forever.
    untrack(() => {
      claims = [...claims, { token, kind }];
    });
    return () => {
      untrack(() => {
        if (claims.some((c) => c.token === token)) claims = claims.filter((c) => c.token !== token);
      });
    };
  }
};

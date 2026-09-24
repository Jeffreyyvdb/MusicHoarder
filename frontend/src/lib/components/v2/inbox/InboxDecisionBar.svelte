<script lang="ts">
  import type { Snippet } from 'svelte';
  import { bottomBar } from '$lib/stores/bottom-bar.svelte';
  import { DYNAMIC_TYPE_CHANGE } from '$lib/dynamic-type';

  // The compact decision toolbar of a pushed Inbox detail (Tag review, Duplicates, AI flagged).
  // It takes the tab bar's slot rather than stacking above it — UIKit's hidesBottomBarWhenPushed,
  // the Photos photo-view model — so a phone never shows three floating layers (tab bar, mini
  // player, decisions) at once. The claim is the effect's return value: it is released when the
  // detail unmounts, and the tab bar comes back. The MiniPlayer re-docks above this bar and
  // --mh-content-pad stays right, because the bar has the tab bar's height and offset.
  //
  // Mount it only while a compact detail is showing; it is md:hidden as a belt-and-braces guard
  // (md+ keeps the in-flow action bar under the detail pane).
  //
  // Larger Dynamic Type: the capsule's width is fixed by the screen, so its labels can outgrow it
  // ("Keep recommended" at 150% is wider than the bar). When they do, the bar sets `data-tight`
  // and the buttons fall back — secondary ones to a glyph with the label kept for VoiceOver
  // (`in-data-tight:sr-only` on the label, `hidden in-data-tight:block` on the glyph), the
  // prominent one to a truncated label (`in-data-tight:min-w-0 in-data-tight:shrink` on the
  // button, `truncate` on the label). Nothing is ever pushed off the screen.
  type Props = {
    /** Accessible name of the toolbar ("Review decision", …). */
    label: string;
    /** 44pt capsule buttons; the one prominent action goes last (trailing), with `ml-auto`. */
    children: Snippet;
  };
  const { label, children }: Props = $props();

  $effect(() => bottomBar.claim('toolbar'));

  let bar = $state<HTMLElement | null>(null);

  // Measured with the fallback off, in one synchronous pass (no frame shows the overflow): the
  // buttons never shrink until tight, so an overflow is the honest signal that the labels do not
  // fit. Re-measured when the bar resizes (rotation), when the text size changes, and when a
  // label changes (a spinner, the AI flagged trailing action arriving).
  $effect(() => {
    const el = bar;
    if (!el) return;
    const measure = () => {
      el.removeAttribute('data-tight');
      if (el.scrollWidth > el.clientWidth + 1) el.setAttribute('data-tight', '');
    };
    measure();
    const frame = requestAnimationFrame(measure); // after web fonts settle the first layout
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    const mo = new MutationObserver(measure);
    mo.observe(el, { childList: true, subtree: true, characterData: true });
    window.addEventListener(DYNAMIC_TYPE_CHANGE, measure);
    return () => {
      cancelAnimationFrame(frame);
      ro.disconnect();
      mo.disconnect();
      window.removeEventListener(DYNAMIC_TYPE_CHANGE, measure);
    };
  });
</script>

<div
  bind:this={bar}
  role="toolbar"
  aria-label={label}
  class="mh-glass mh-chrome fixed right-[max(16px,env(safe-area-inset-right))] bottom-(--mh-tabbar-offset) left-[max(16px,env(safe-area-inset-left))] z-40 flex h-(--mh-tabbar-h) items-center gap-1 rounded-full px-2 md:hidden"
>
  {@render children()}
</div>

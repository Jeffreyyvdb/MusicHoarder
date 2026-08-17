<script lang="ts">
  import type { Component } from 'svelte';
  import { cn, scrollStripToActive } from '$lib/utils';

  type Tab = {
    id: string;
    label: string;
    /** Route tabs link; second-level tabs (Settings, Spotify) use `onselect` instead. */
    href?: string;
    icon?: Component;
    /** Show a pulse dot (the live conveyor). */
    live?: boolean;
    /** Numeric/string count on the right; null/undefined hides it. */
    count?: number | string | null;
  };

  type Props = {
    tabs: Tab[];
    /** id of the active tab. Empty when the group matched but no tab did. */
    active: string;
    /** Accessible name for the strip, e.g. "Listen views". */
    label: string;
    /** Whether the pipeline is currently running (drives the live pulse). */
    running?: boolean;
    /**
     * Present for tab sets that are not routes — the strip renders <button>s and
     * hands the id back instead of navigating.
     */
    onselect?: (id: string) => void;
    /** Placement is the caller's business; the strip only owns the pills. */
    class?: string;
  };

  const { tabs, active, label, running = false, onselect, class: className }: Props = $props();

  // The strip lives in fixed-width chrome (the top bar, a page toolbar), so it
  // overflows well before a phone runs out of room — eight Manage tabs don't fit
  // beside Search/Add/Theme. Scroll the active tab into view after a change.
  let scroller = $state<HTMLElement | null>(null);

  // Clipped against the top bar's action cluster, a cut-off pill reads as broken
  // rather than scrollable. Fade whichever edge has tabs hidden behind it — only
  // that edge, so a short group (or a strip scrolled to its start) never fades a
  // pill that is fully visible.
  let fadeStart = $state(false);
  let fadeEnd = $state(false);
  function measureEdges(el: HTMLElement) {
    fadeStart = el.scrollLeft > 1;
    fadeEnd = el.scrollLeft + el.clientWidth < el.scrollWidth - 1;
  }

  $effect(() => {
    const el = scroller;
    void active;
    void tabs;
    if (!el) return;
    const centre = () => {
      scrollStripToActive(el, el.querySelector<HTMLElement>('[data-active]'));
      measureEdges(el);
    };
    // Once now (layout is usually already settled on a client-side navigation)
    // and once after the next frame, for the mount case where it isn't.
    centre();
    const frame = requestAnimationFrame(centre);

    // The strip shares the top bar with the action cluster, so its width is not
    // final on the frame after mount — the first centring attempt can see a
    // still-wide, not-yet-overflowing strip and no-op, stranding the active tab
    // off-screen on a phone. Re-centre whenever its own width actually changes;
    // guarding on the width means a user's manual sideways scroll is never
    // yanked back.
    let lastWidth = el.clientWidth;
    const ro = new ResizeObserver(() => {
      if (el.clientWidth !== lastWidth) {
        lastWidth = el.clientWidth;
        centre();
      } else {
        measureEdges(el);
      }
    });
    ro.observe(el);
    for (const child of el.children) ro.observe(child);
    return () => {
      cancelAnimationFrame(frame);
      ro.disconnect();
    };
  });
  // Spelled out as whole literals rather than composed at runtime: Tailwind
  // scans source text for candidates, so an interpolated arbitrary value would
  // never make it into the stylesheet.
  const maskClass = $derived(
    fadeStart && fadeEnd
      ? '[mask-image:linear-gradient(to_right,transparent,black_2rem,black_calc(100%-2rem),transparent)]'
      : fadeStart
        ? '[mask-image:linear-gradient(to_right,transparent,black_2rem)]'
        : fadeEnd
          ? '[mask-image:linear-gradient(to_right,black_calc(100%-2rem),transparent)]'
          : undefined
  );
</script>

{#snippet body(tab: Tab, isActive: boolean)}
  {#if tab.live && running}
    <span class="bg-primary mh-v2-pulse size-1.5 shrink-0 rounded-full"></span>
  {/if}
  <span>{tab.label}</span>
  {#if tab.count != null}
    <span
      class={cn(
        'text-nav-count rounded-full px-1.5 py-px tabular-nums',
        isActive ? 'bg-primary/15 text-primary' : 'bg-muted text-muted-foreground'
      )}>{typeof tab.count === 'number' ? tab.count.toLocaleString() : tab.count}</span
    >
  {/if}
{/snippet}

<nav
  bind:this={scroller}
  class={cn('no-scrollbar flex items-center overflow-x-auto scroll-px-4', maskClass, className)}
  onscroll={(e) => measureEdges(e.currentTarget)}
  aria-label={label}
>
  <!-- Apple-style segmented control (same idiom as the song-panel tabs): a soft
       capsule track with the active segment as a raised pill. The bar stays
       count-less and dimension-stable (constraint) — switching tabs only moves
       the pill, never resizes the bar. Sized to 32px so it sits inside the
       48px top bar next to the h-8 Search/Add buttons. -->
  <div class="bg-foreground/5 flex shrink-0 items-center gap-1 rounded-full p-0.5">
    {#each tabs as tab (tab.id)}
      {@const isActive = tab.id === active}
      {@const pill = cn(
        'flex shrink-0 items-center gap-1.5 rounded-full px-2.5 py-1 text-xs font-medium whitespace-nowrap transition-colors sm:px-3.5 sm:text-nav',
        'focus-visible:ring-ring/60 outline-none focus-visible:ring-2',
        isActive ? 'bg-background text-foreground shadow-sm' : 'text-muted-foreground hover:text-foreground'
      )}
      {#if onselect}
        <button
          type="button"
          onclick={() => onselect(tab.id)}
          data-active={isActive || undefined}
          aria-current={isActive ? 'page' : undefined}
          class={pill}
        >
          {@render body(tab, isActive)}
        </button>
      {:else}
        <a
          href={tab.href}
          data-active={isActive || undefined}
          aria-current={isActive ? 'page' : undefined}
          class={pill}
        >
          {@render body(tab, isActive)}
        </a>
      {/if}
    {/each}
  </div>
</nav>

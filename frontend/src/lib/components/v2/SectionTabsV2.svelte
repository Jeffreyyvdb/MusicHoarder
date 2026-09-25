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
     * Present for tab sets that are not routes — the strip renders a tablist of <button>s and
     * hands the id back instead of navigating.
     */
    onselect?: (id: string) => void;
    /** Placement is the caller's business; the strip only owns the pills. */
    class?: string;
  };

  const { tabs, active, label, running = false, onselect, class: className }: Props = $props();

  // Two modes, two semantics. Route tabs (the desktop top bar) are navigation: a <nav> of links,
  // the current one marked aria-current="page". Second-level tabs (a Settings pane, a Spotify
  // view) are not pages, so they are a tablist of buttons with aria-selected — announcing them as
  // "current page" told VoiceOver users they had navigated when they had only switched a view.
  const isTablist = $derived(!!onselect);

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

  // Tablist keyboard contract (automatic activation, like the segmented control): arrows move
  // the selection and focus with wrap-around, Home/End jump to the ends. Only the selected tab is
  // in the Tab order.
  let buttons: HTMLButtonElement[] = $state([]);
  function onkeydown(event: KeyboardEvent) {
    if (!onselect || tabs.length === 0) return;
    const current = Math.max(
      0,
      tabs.findIndex((t) => t.id === active)
    );
    const steps: Record<string, number> = { ArrowRight: 1, ArrowLeft: -1 };
    let next = -1;
    if (event.key in steps) next = (current + steps[event.key] + tabs.length) % tabs.length;
    else if (event.key === 'Home') next = 0;
    else if (event.key === 'End') next = tabs.length - 1;
    if (next < 0) return;
    event.preventDefault();
    buttons[next]?.focus();
    if (tabs[next].id !== active) onselect(tabs[next].id);
  }
</script>

{#snippet body(tab: Tab, isActive: boolean)}
  {#if tab.live && running}
    <span class="bg-primary mh-v2-pulse size-1.5 shrink-0 rounded-full" aria-hidden="true"></span>
    <span class="sr-only">Pipeline running</span>
  {/if}
  <span>{tab.label}</span>
  {#if tab.count != null}
    <!-- Muted on the track; on the raised pill the count keeps the label's colour, since the
         muted grey on the dark #636366 thumb would fall to about 2.3:1. -->
    <span
      class={cn(
        'text-nav-count rounded-full px-1.5 py-px font-normal tabular-nums',
        isActive ? 'bg-foreground/10' : 'bg-muted text-muted-foreground'
      )}>{typeof tab.count === 'number' ? tab.count.toLocaleString() : tab.count}</span
    >
  {/if}
{/snippet}

<svelte:element
  this={isTablist ? 'div' : 'nav'}
  bind:this={scroller}
  class={cn('no-scrollbar flex items-center overflow-x-auto scroll-px-4', maskClass, className)}
  onscroll={(e: Event) => measureEdges(e.currentTarget as HTMLElement)}
  aria-label={isTablist ? undefined : label}
  data-scroll-x=""
>
  <!-- Apple-style segmented track: a soft capsule with the active segment as a raised pill (the
       same tokens as the SegmentedControl primitive, which takes over wherever a set has five
       segments or fewer). Unlike that control it scrolls, so it can carry Settings' seven
       sections. The bar stays dimension-stable — switching tabs only moves the pill, never
       resizes the bar. 32px visual so it sits in the 48px desktop bar beside the h-8 buttons; on
       touch each pill's hit area grows to 44pt with an `after:` pseudo, not a taller track. -->
  <div
    class="bg-muted flex shrink-0 items-center gap-0.5 rounded-full p-0.5"
    role={isTablist ? 'tablist' : undefined}
    aria-label={isTablist ? label : undefined}
    onkeydown={isTablist ? onkeydown : undefined}
  >
    {#each tabs as tab, i (tab.id)}
      {@const isActive = tab.id === active}
      {@const pill = cn(
        'relative flex min-h-7 shrink-0 items-center gap-1.5 rounded-full px-3 whitespace-nowrap transition-colors',
        'text-footnote font-medium sm:text-nav',
        'after:absolute after:inset-x-0 after:inset-y-0 pointer-coarse:after:-inset-y-2',
        'focus-visible:ring-ring outline-none focus-visible:ring-2',
        isActive
          ? 'bg-segmented-thumb text-foreground shadow-[0_1px_2px_rgb(0_0_0/0.12),0_0_0_0.5px_rgb(0_0_0/0.04)]'
          : 'text-muted-foreground hover:text-foreground'
      )}
      {#if onselect}
        <button
          bind:this={buttons[i]}
          type="button"
          role="tab"
          onclick={() => onselect(tab.id)}
          data-active={isActive || undefined}
          aria-selected={isActive}
          tabindex={isActive || (!active && i === 0) ? 0 : -1}
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
</svelte:element>

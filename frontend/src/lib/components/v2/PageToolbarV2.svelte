<script lang="ts">
  import type { Component, Snippet } from 'svelte';
  import { cn } from '$lib/utils';
  import SectionTabsV2 from '$lib/components/v2/SectionTabsV2.svelte';

  // The one page-header idiom. Before this, every page hand-rolled its own band
  // in one of three dialects, costing 76–290px of chrome for a title, a
  // paragraph nobody reads twice, and a couple of buttons.
  //
  // Two rules make that unrepeatable:
  //   • Fixed h-11. Not padding-derived — a `py-*` bar silently grows when a
  //     child is taller than expected (a Badge, a Switch), and the savings leak
  //     back out one page at a time.
  //   • It never wraps. The `filters` region scrolls sideways instead, so a
  //     narrow screen can't turn one bar into three lines.
  //
  // `filterRow` is the one sanctioned exception, and it does not break either
  // rule: the header keeps its h-11 and its single line, and the snippet renders
  // in a *separate* band below it, at every width. It exists because a page with
  // a search box and seven chips leaves about two chips visible on a 375px
  // screen — and on a laptop the same chips squeeze the search input and the
  // meta line down to nothing. A filter you have to swipe to discover is one you
  // don't use. Costs ~40px of chrome, so pass it only where the chips are the
  // page's primary control.
  type Tab = { id: string; label: string; count?: number | string | null };

  type Props = {
    /** Small leading glyph — usually the page's nav icon. */
    icon?: Component;
    /** The page name. Short — this is a label, not a sentence. */
    title: string;
    /** Muted one-liner: counts, totals, status. Never prose. */
    meta?: string;
    /**
     * Width the meta appears at. Use 'lg' whenever the bar also carries a text
     * input, or the meta starves the input on a laptop.
     */
    metaFrom?: 'sm' | 'lg';
    /** Second-level tabs (Settings sections, Spotify views) — not routes. */
    tabs?: Tab[];
    activeTab?: string;
    onselectTab?: (id: string) => void;
    /** Toggles and search. Scrolls horizontally when it overflows. */
    filters?: Snippet;
    /**
     * Rendered in its own scrolling band below the bar, at every width. Use it for the
     * page's primary filter set so the header keeps only identity, search and actions.
     * Pass `undefined` on views with no filters — an empty band is still a bordered
     * 40px stripe.
     */
    filterRow?: Snippet;
    /** Buttons. Never scrolls, never wraps. */
    actions?: Snippet;
  };

  const {
    icon: Icon,
    title,
    meta,
    metaFrom = 'sm',
    tabs,
    activeTab,
    onselectTab,
    filters,
    filterRow,
    actions
  }: Props = $props();
</script>

<header
  class="border-border bg-background flex h-11 shrink-0 items-center gap-2 border-b px-4 sm:gap-3 sm:px-7"
>
  {#if Icon}
    <Icon class="text-muted-foreground size-4 shrink-0" aria-hidden="true" />
  {/if}

  <div class="flex min-w-0 shrink items-baseline gap-2">
    <h1 class="text-nav truncate font-semibold tracking-[-0.01em]">{title}</h1>
    {#if meta}
      <span
        class={cn(
          'text-muted-foreground text-nav-xs hidden truncate whitespace-nowrap tabular-nums',
          metaFrom === 'lg' ? 'lg:inline' : 'sm:inline'
        )}>{meta}</span
      >
    {/if}
  </div>

  {#if tabs?.length}
    <SectionTabsV2
      class="min-w-0 shrink"
      {tabs}
      active={activeTab ?? ''}
      label="{title} sections"
      onselect={onselectTab}
    />
  {/if}

  {#if filters}
    <div class="no-scrollbar flex min-w-0 flex-1 items-center gap-2 overflow-x-auto">
      {@render filters()}
    </div>
  {:else}
    <span class="flex-1"></span>
  {/if}

  {#if actions}
    <div class="flex shrink-0 items-center gap-1.5">{@render actions()}</div>
  {/if}
</header>

{#if filterRow}
  <div
    class="no-scrollbar border-border bg-background flex shrink-0 items-center gap-2 overflow-x-auto border-b px-4 py-1.5 sm:gap-3 sm:px-7"
  >
    {@render filterRow()}
  </div>
{/if}

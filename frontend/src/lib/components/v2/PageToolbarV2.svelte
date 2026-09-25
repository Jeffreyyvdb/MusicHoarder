<script lang="ts">
  import type { Component, Snippet } from 'svelte';
  import { page } from '$app/state';
  import { ChevronLeft, Ellipsis } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { SegmentedControl } from '$lib/components/ui/segmented-control';
  import SectionTabsV2 from '$lib/components/v2/SectionTabsV2.svelte';
  import AccountButton from '$lib/components/v2/AccountButton.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { isTabRoot, surfaceFor, type NavBack } from '$lib/nav';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';
  import { navBack } from '$lib/stores/nav-back.svelte';
  import { cn } from '$lib/utils';

  // The page's navigation bar — the one page-header idiom, now shaped like UIKit's.
  //
  // Compact (below md) it is an iOS navigation bar with a large title:
  //   1. a sticky 44pt bar (with 4px of air under the status bar): a glass Back circle, an
  //      inline title that fades in once the large title has scrolled under the bar, and the
  //      trailing items (actions + More in one glass capsule, then the account avatar on a tab
  //      root). The bar itself has no background — a scroll-edge band fades in behind it when
  //      collapsed, the iOS 26 look.
  //   2. the large title + subtitle (the `meta`, which phones never used to see), then
  //   3. search, the segmented tabs and the chip bands.
  // 2 and 3 scroll away with the content, on the compositor: no scroll listeners, no height
  // animation. That only works from INSIDE the scroller, so this component renders several
  // top-level nodes (no wrapper) and pages place it as the first child of their primary scroll
  // container — the sticky bar sticks to that scroller's top. Rendered outside a scroller (the
  // old placement) it still looks right: the title just never scrolls away, so it never
  // collapses.
  //
  // md+ it stays a macOS-style toolbar: one h-12 row (title + meta · tabs · search, actions,
  // More) over a hairline, the chip bands in a strip beneath it, all sticky together. No large
  // title and no avatar there (the desktop top bar carries the account button), and Back only
  // when a page passes it: a desktop window has the sidebar and the browser's own Back.
  //
  // Two rules from the old bar survive at every width: the bar row is a fixed height (a
  // padding-derived bar silently grows the first time a child is taller than expected), and chip
  // bands scroll sideways rather than wrap.
  type Tab = { id: string; label: string; count?: number | string | null };

  type Props = {
    /** Ignored — decorative page glyphs are gone. Kept so existing call sites compile. */
    icon?: Component;
    /** The page name. Short — this is a label, not a sentence. */
    title: string;
    /** Muted one-liner: counts, totals, status. Never prose. The subtitle on a phone. */
    meta?: string;
    /**
     * Width the meta appears at on the desktop bar. Use 'lg' whenever the bar also carries a
     * search field, or the meta starves it on a laptop. A long meta never squeezes the title (it
     * truncates first), so that alone is not a reason. Phones always show it (as the subtitle).
     */
    metaFrom?: 'sm' | 'lg';
    /**
     * Second-level tabs (Spotify views, Settings sections) — not routes. Up to five render as
     * a segmented control; more fall back to the scrolling SectionTabsV2 strip; one renders
     * nothing.
     */
    tabs?: Tab[];
    activeTab?: string;
    onselectTab?: (id: string) => void;
    /** Toggles (FilterChips). A sideways-scrolling band under the title / under the bar. */
    filters?: Snippet;
    /**
     * The page's primary chip set, in its own sideways-scrolling band. Pass `undefined` on views
     * with no filters rather than an empty snippet.
     */
    filterRow?: Snippet;
    /**
     * Bar buttons. On a phone they sit in one glass capsule and are normalised to 44pt icon
     * items, so pass at most two, icon-only (`<Button variant="ghost" size="icon"
     * aria-label="…">`), with anything else in `more`. The desktop bar renders them as given.
     */
    actions?: Snippet;
    /**
     * Where Back goes. Omitted: on a phone, the tab stack's previous page (else the page's
     * hierarchical parent); none on desktop. `null` suppresses it. An object overrides it at
     * every width.
     */
    back?: NavBack | null;
    /** DropdownMenu items for a trailing More (…) pull-down. The bar renders the trigger. */
    more?: Snippet;
    /** A search field (usually `SearchField`): under the large title on a phone, trailing on md+. */
    search?: Snippet;
    /** Show the phone's large title (default). Off: the inline title shows at rest. */
    largeTitle?: boolean;
    /**
     * With `largeTitle={false}`: the page's own title element (a pushed detail's hero heading)
     * stands in for the large title. The inline title and the scroll edge stay hidden while it
     * is on screen and fade in once it has scrolled under the bar — Apple Music's album page, so
     * the name is not shown twice at rest. Pass the bound element; `null` until it mounts is fine.
     */
    collapseAfter?: HTMLElement | null;
    /**
     * Spoken before the title, never shown: what a terse title is a position in. A pushed Inbox
     * detail titled "5 of 10" passes "Tag review", so VoiceOver announces "Tag review, 5 of 10".
     * A subtitle that only repeats it is then hidden from VoiceOver.
     */
    titleLabel?: string;
    /**
     * The page sits on the grouped background (#F2F2F7 in light), which the scroll edge and the
     * desktop bar must match. Defaults to the page's own surface (`surfaceFor`).
     */
    grouped?: boolean;
  };

  // eslint-disable-next-line svelte/no-unused-props -- `icon` is accepted and deliberately unused
  const {
    title,
    meta,
    metaFrom = 'sm',
    tabs,
    activeTab,
    onselectTab,
    filters,
    filterRow,
    actions,
    back,
    more,
    search,
    largeTitle = true,
    collapseAfter,
    titleLabel,
    grouped
  }: Props = $props();

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);
  const user = $derived(page.data.user);

  const onGrouped = $derived(grouped ?? surfaceFor(page.url, compact) === 'grouped');
  const pageBg = $derived(onGrouped ? 'var(--background-grouped)' : 'var(--background)');

  // Back: an explicit target wins at every width and `null` switches it off. Otherwise a phone
  // goes where the tab's stack says (the page you actually came from, else the hierarchical
  // parent); a desktop bar shows none.
  const backTarget = $derived<NavBack | null>(
    back !== undefined ? back : compact ? tabMemory.backTarget(page.url, user) : null
  );

  // The installed app's edge swipe runs exactly what the button would. Published only while
  // there is somewhere to go, so a bar without Back never clears another bar's action.
  $effect(() => {
    const target = backTarget;
    if (!target) return;
    return navBack.set(() => tabMemory.goBack(target));
  });

  function goBack(event: MouseEvent, target: NavBack) {
    // Let modified clicks (new tab / window) through to the link.
    if (event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey) {
      return;
    }
    event.preventDefault();
    void tabMemory.goBack(target);
  }

  // A tab root carries the account avatar; members reach Settings, account switching and Sign
  // out only through it, so it must be on every root.
  const showAccount = $derived(isTabRoot(page.url, user));

  const tabCount = $derived(tabs?.length ?? 0);
  const segmentItems = $derived(
    (tabs ?? []).map((t) => ({ value: t.id, label: t.label, count: t.count ?? undefined }))
  );

  // ── collapse ────────────────────────────────────────────────────────────────
  // ONE IntersectionObserver on the large title, rooted at the scroller the bar sticks in, with
  // the bar's height taken off the top: the moment the title has slid entirely under the bar,
  // the bar is "collapsed" — inline title and scroll edge fade in. Without a large title a 1px
  // sentinel under the bar stands in for it, so the scroll edge still appears as soon as content
  // passes under the bar. Outside a scroller there is nothing to observe against, and the bar
  // simply stays expanded.
  let navbarEl = $state<HTMLElement | null>(null);
  let titleEl = $state<HTMLElement | null>(null);
  let collapsed = $state(false);

  // A bits-ui ScrollArea viewport only turns `overflow-y: scroll` on once its scrollbar has
  // mounted, which is after this bar's effects run, so it is recognised by its attribute too.
  function scrollParentOf(el: HTMLElement): HTMLElement | null {
    for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
      if (p.hasAttribute('data-scroll-area-viewport')) return p;
      const { overflowY } = getComputedStyle(p);
      if (overflowY === 'auto' || overflowY === 'scroll' || overflowY === 'overlay') return p;
    }
    return null;
  }

  // Passed at all (even while still null) means the page's heading, not the sentinel, decides.
  const followsPageHeading = $derived(!largeTitle && collapseAfter !== undefined);

  $effect(() => {
    const bar = navbarEl;
    const heading = followsPageHeading ? collapseAfter : titleEl;
    if (!compact || !bar || !heading) {
      collapsed = false;
      return;
    }
    let io: IntersectionObserver | null = null;
    // A frame late: a page's own scroller can finish styling itself after this effect (see
    // above), and the bar's height is only final once it has laid out.
    const frame = requestAnimationFrame(() => {
      const root = scrollParentOf(bar);
      if (!root) return;
      io = new IntersectionObserver(
        (entries) => {
          const entry = entries[entries.length - 1];
          // Collapsed only when the title left through the TOP (under the bar), never because
          // a tall page pushed it below the fold.
          const top = entry.rootBounds?.top ?? 0;
          collapsed = !entry.isIntersecting && entry.boundingClientRect.top < top;
        },
        { root, rootMargin: `-${bar.offsetHeight}px 0px 0px 0px`, threshold: 0 }
      );
      io.observe(heading);
    });
    return () => {
      cancelAnimationFrame(frame);
      io?.disconnect();
      collapsed = false;
    };
  });

  // ── --mh-navbar-h ───────────────────────────────────────────────────────────
  // A sticky sub-header further down the same scroller (a list's column header, a filter strip)
  // has to stick below the bar, not slide behind it. Publish the bar's live height on the
  // scroller so it can use `top: var(--mh-navbar-h, 0px)` — 48px on a phone, the bar plus its
  // chip bands on desktop.
  $effect(() => {
    const bar = navbarEl;
    if (!bar) return;
    let root: HTMLElement | null = null;
    let ro: ResizeObserver | null = null;
    const frame = requestAnimationFrame(() => {
      root = scrollParentOf(bar);
      if (!root) return;
      const target = root;
      ro = new ResizeObserver(() =>
        target.style.setProperty('--mh-navbar-h', `${bar.offsetHeight}px`)
      );
      ro.observe(bar);
    });
    return () => {
      cancelAnimationFrame(frame);
      ro?.disconnect();
      root?.style.removeProperty('--mh-navbar-h');
    };
  });

  // ── trailing items (compact) ────────────────────────────────────────────────
  // Actions and More share one glass capsule. A page whose actions are all hidden at this width
  // (labels and inputs that only show from sm) would leave an empty glass sliver, so the capsule
  // only takes its material while something in it has a box.
  let itemsEl = $state<HTMLElement | null>(null);
  let itemsEmpty = $state(false);
  $effect(() => {
    const el = itemsEl;
    if (!el) return;
    const measure = () => (itemsEmpty = el.getBoundingClientRect().width < 1);
    measure();
    const ro = new ResizeObserver(measure);
    ro.observe(el);
    return () => ro.disconnect();
  });
</script>

{#snippet moreMenu(triggerClass: string)}
  <DropdownMenu.Root>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <Button {...props} variant="ghost" size="icon" class={triggerClass} aria-label="More">
          <Ellipsis />
        </Button>
      {/snippet}
    </DropdownMenu.Trigger>
    <DropdownMenu.Content align="end" class="min-w-56">
      {@render more?.()}
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/snippet}

{#snippet tabControl(segmentClass: string, stripClass: string)}
  {#if tabs && tabCount > 1}
    {#if tabCount <= 5}
      <SegmentedControl
        items={segmentItems}
        value={activeTab}
        label="{title} sections"
        onValueChange={(id) => onselectTab?.(id)}
        class={segmentClass}
      />
    {:else}
      <SectionTabsV2
        class={stripClass}
        {tabs}
        active={activeTab ?? ''}
        label="{title} sections"
        onselect={onselectTab}
      />
    {/if}
  {/if}
{/snippet}

{#if compact}
  <!-- 1. The bar. Sticky against the page's scroller; `--page-bg` feeds the scroll edge. -->
  <div
    bind:this={navbarEl}
    data-mh-navbar=""
    data-collapsed={collapsed || undefined}
    class="group/navbar sticky top-0 z-20 shrink-0 pt-1"
    style:--page-bg={pageBg}
  >
    <div aria-hidden="true" class="scroll-edge"></div>
    <div class="relative flex h-11 items-center gap-2 px-4">
      <!-- Equal-growth sides (never narrower than their items) keep the inline title centred
           until one side is too wide, then it gives way — UINavigationBar's rule. -->
      <div class="flex min-w-max flex-1 basis-0 items-center justify-start">
        {#if backTarget}
          <a
            href={backTarget.href}
            aria-label="Back to {backTarget.label}"
            onclick={(e) => goBack(e, backTarget)}
            class="mh-glass mh-chrome text-foreground focus-visible:ring-ring grid size-11 place-items-center rounded-full transition-transform duration-150 ease-[cubic-bezier(0.23,1,0.32,1)] outline-none focus-visible:ring-2 active:scale-[0.94]"
          >
            <!-- Nudged left a hair: a chevron's visual centre is right of its box's. -->
            <ChevronLeft
              class="size-[22px] -translate-x-px"
              strokeWidth={2.25}
              aria-hidden="true"
            />
          </a>
        {/if}
      </div>

      {#if largeTitle}
        <!-- A visual echo of the heading below; the <h1> is the large title. -->
        <div
          aria-hidden="true"
          class="text-headline min-w-0 truncate text-center opacity-0 transition-opacity duration-150 ease-[cubic-bezier(0.23,1,0.32,1)] group-data-[collapsed]/navbar:opacity-100"
        >
          {title}
        </div>
      {:else}
        <!-- Following a page heading it stays a real <h1> (it names the page for VoiceOver) and is
             only hidden from sight until that heading has gone under the bar. -->
        <div
          class={cn(
            'flex min-w-0 flex-col items-center text-center',
            followsPageHeading &&
              'opacity-0 transition-opacity duration-150 ease-[cubic-bezier(0.23,1,0.32,1)] group-data-[collapsed]/navbar:opacity-100'
          )}
        >
          <h1 class="text-headline max-w-full truncate">
            {#if titleLabel}<span class="sr-only">{`${titleLabel}, `}</span>{/if}{title}
          </h1>
          {#if meta}
            <p
              aria-hidden={titleLabel && meta === titleLabel ? 'true' : undefined}
              class="text-caption-1 text-muted-foreground max-w-full truncate font-normal tabular-nums"
            >
              {meta}
            </p>
          {/if}
        </div>
      {/if}

      <div class="flex min-w-max flex-1 basis-0 items-center justify-end gap-2">
        {#if actions || more}
          <div
            bind:this={itemsEl}
            class={cn(
              'nav-items flex h-11 items-center rounded-full',
              !itemsEmpty && 'mh-glass mh-chrome'
            )}
          >
            {@render actions?.()}
            {#if more}
              {@render moreMenu('')}
            {/if}
          </div>
        {/if}
        {#if showAccount}
          <AccountButton />
        {/if}
      </div>
    </div>
  </div>

  <!-- 2. The large title, which scrolls away with the content. -->
  <!-- Tight on purpose (a phone header at rest stays short): the title's 41pt line and the subtitle's 20pt line already carry
       their own leading, so the block adds only a hair under them. -->
  {#if largeTitle}
    <div class="shrink-0 px-4 pb-0.5">
      <h1 bind:this={titleEl} class="text-large-title line-clamp-2 break-words">{title}</h1>
      {#if meta}
        <p class="text-subheadline text-muted-foreground tabular-nums">{meta}</p>
      {/if}
    </div>
  {:else}
    <div bind:this={titleEl} aria-hidden="true" class="-mb-px h-px shrink-0"></div>
  {/if}

  <!-- 3. The controls. They scroll away too (UISearchController's hide-on-scroll); a page that
       filters says "N of M" in its subtitle, so the state is never lost with the field. -->
  {#if search}
    <!-- UISearchBar's 36pt field; its 44pt hit area overhangs the band's padding (see <style>). -->
    <div class={cn('nav-search shrink-0 px-4 pb-2', largeTitle ? 'pt-0' : 'pt-2')}>
      {@render search()}
    </div>
  {/if}
  {#if tabs && tabCount > 1}
    <div class="shrink-0 px-4 pt-1 pb-2">
      {@render tabControl('', '-mx-4 px-4')}
    </div>
  {/if}
  {#if filters}
    <!-- py-1.5: a chip's 44pt hit area reaches 6px past its 32px pill, and a sideways scroller
         clips anything that pokes out of it. -->
    <div
      data-scroll-x=""
      class="no-scrollbar flex shrink-0 scroll-px-4 items-center gap-2 overflow-x-auto px-4 py-1.5"
    >
      {@render filters()}
    </div>
  {/if}
  {#if filterRow}
    <div
      data-scroll-x=""
      class="no-scrollbar flex shrink-0 scroll-px-4 items-center gap-2 overflow-x-auto px-4 py-1.5"
    >
      {@render filterRow()}
    </div>
  {/if}
{:else}
  <header
    bind:this={navbarEl}
    data-mh-navbar=""
    class="border-separator sticky top-0 z-20 shrink-0 border-b bg-(--page-bg)"
    style:--page-bg={pageBg}
  >
    <div class="flex h-12 items-center gap-3 px-7">
      <!-- shrink-0 + a max width: the tabs and the search give way first, and a long title still
           truncates at 45% rather than pushing the never-shrinking actions off the edge. -->
      <div class="flex max-w-[45%] min-w-0 shrink-0 items-center gap-1">
        {#if backTarget}
          <Button
            variant="ghost"
            size="icon"
            href={backTarget.href}
            aria-label="Back to {backTarget.label}"
            title="Back to {backTarget.label}"
            class="-ml-2"
            onclick={(e: MouseEvent) => goBack(e, backTarget)}
          >
            <ChevronLeft class="size-5" />
          </Button>
        {/if}
        <!-- The title never shrinks for the meta: the meta truncates (to nothing if it must)
             first, and only a title wider than the whole cap truncates itself. -->
        <div class="flex min-w-0 items-baseline gap-2">
          <h1 class="max-w-full shrink-0 truncate text-[17px] leading-[22px] font-semibold">
            {#if titleLabel}<span class="sr-only">{`${titleLabel}, `}</span>{/if}{title}
          </h1>
          {#if meta}
            <span
              class={cn(
                'text-muted-foreground text-nav-xs hidden min-w-0 truncate whitespace-nowrap tabular-nums',
                metaFrom === 'lg' ? 'lg:inline' : 'md:inline'
              )}>{meta}</span
            >
          {/if}
        </div>
      </div>

      <div class="flex min-w-0 flex-1 justify-center">
        {@render tabControl('w-auto', 'min-w-0')}
      </div>

      {#if search || actions || more}
        <div class="flex min-w-0 shrink-0 items-center gap-1.5">
          {#if search}
            <div class="w-56 min-w-32 shrink lg:w-64">{@render search()}</div>
          {/if}
          {@render actions?.()}
          {#if more}
            {@render moreMenu('')}
          {/if}
        </div>
      {/if}
    </div>
    {#if filters}
      <div data-scroll-x="" class="no-scrollbar flex items-center gap-2 overflow-x-auto px-7 pb-2">
        {@render filters()}
      </div>
    {/if}
    {#if filterRow}
      <div data-scroll-x="" class="no-scrollbar flex items-center gap-2 overflow-x-auto px-7 pb-2">
        {@render filterRow()}
      </div>
    {/if}
  </header>
{/if}

<style>
  /* The scroll edge: no bar background — a band behind the bar that is solid page colour at
     the top, fades out over the 16px below the bar, and softly blurs what passes under it. It
     fades in only once the large title has gone (data-collapsed on the bar). */
  .scroll-edge {
    position: absolute;
    inset-inline: 0;
    top: 0;
    height: calc(100% + 16px);
    pointer-events: none;
    opacity: 0;
    transition: opacity 150ms cubic-bezier(0.23, 1, 0.32, 1);
    background: linear-gradient(
      to bottom,
      var(--page-bg) 0,
      var(--page-bg) 20px,
      color-mix(in oklab, var(--page-bg) 70%, transparent) 75%,
      transparent
    );
    -webkit-backdrop-filter: blur(10px);
    backdrop-filter: blur(10px);
    -webkit-mask-image: linear-gradient(#000 60%, transparent);
    mask-image: linear-gradient(#000 60%, transparent);
  }
  [data-collapsed] > .scroll-edge {
    opacity: 1;
  }
  /* Reduce Transparency / Increase Contrast: a solid bar the height of the bar with a hairline,
     instead of a blurred fade. */
  @media (prefers-reduced-transparency: reduce), (prefers-contrast: more) {
    .scroll-edge {
      height: 100%;
      background: var(--page-bg);
      -webkit-backdrop-filter: none;
      backdrop-filter: none;
      -webkit-mask-image: none;
      mask-image: none;
      box-shadow: inset 0 calc(-1 * var(--hairline, 1px)) 0 var(--separator);
    }
  }

  /* Bar items on a phone. Whatever buttons a page passes become 44pt glass-capsule items: no
     fill of their own (the capsule is the material), foreground glyphs at 20px, a soft press
     fill. The one prominent action (a default, tint-filled Button) keeps its fill. A Button used
     as a menu trigger carries the trigger's data-slot instead of "button", so both are matched,
     and so is a hand-rolled <button>/<a> passed straight in (its own pill border and card fill
     would otherwise draw a capsule inside the capsule). Unlayered, so it beats the Button's
     utility classes without !important. */
  .nav-items :global(:is([data-slot='button'], [data-slot='dropdown-menu-trigger'])),
  .nav-items > :global(:is(button, a)) {
    height: 44px;
    min-width: 44px;
    padding-inline: 10px;
    border-color: transparent;
    border-radius: 9999px;
    box-shadow: none;
  }
  .nav-items
    :global(:is([data-slot='button'], [data-slot='dropdown-menu-trigger']):not(.bg-primary)),
  .nav-items > :global(:is(button, a):not(.bg-primary)) {
    background-color: transparent;
    color: var(--foreground);
  }
  .nav-items
    :global(:is([data-slot='button'], [data-slot='dropdown-menu-trigger']):not(.bg-primary):hover),
  .nav-items > :global(:is(button, a):not(.bg-primary):hover),
  .nav-items
    :global(
      :is([data-slot='button'], [data-slot='dropdown-menu-trigger']):not(
          .bg-primary
        )[aria-expanded='true']
    ) {
    background-color: var(--accent);
  }
  .nav-items :global(:is([data-slot='button'], [data-slot='dropdown-menu-trigger']) svg),
  .nav-items > :global(:is(button, a) svg) {
    width: 20px;
    height: 20px;
  }
  /* The search band's field (compact only): drawn 36pt tall, as UISearchBar is, so the header
     at rest stays short, while the input keeps a 44pt hit area — the box stays 44pt and
     overhangs the band's padding by 4px each way, and the capsule and its focus ring are painted
     4px in from its edges. */
  .nav-search :global([data-slot='search-field']) {
    margin-block: -4px;
    background-color: transparent;
    box-shadow: none;
    isolation: isolate;
  }
  .nav-search :global([data-slot='search-field'])::before {
    content: '';
    position: absolute;
    inset: 4px 0;
    z-index: -1;
    border-radius: 9999px;
    background-color: var(--input);
    transition: box-shadow 150ms;
  }
  .nav-search :global([data-slot='search-field']:has(input:focus-visible))::before {
    box-shadow: 0 0 0 3px color-mix(in oklab, var(--ring) 50%, transparent);
  }

  /* A status dot or a word (the pipeline's live dot) keeps clear of the capsule's round ends. */
  .nav-items > :global(:not([data-slot='button'], [data-slot='dropdown-menu-trigger'], button, a)) {
    margin-inline: 12px;
  }
</style>

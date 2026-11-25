<script lang="ts">
  import { untrack, type Snippet } from 'svelte';
  import { Users } from '@lucide/svelte';
  import { EmptyState } from '#lib/components/ui/empty-state/index.ts';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import { IsMobile } from '#lib/hooks/is-mobile.svelte.ts';
  import { cn } from '#lib/utils.ts';
  import { getArtistImageUrl, type ArtistPortrait, type GroupSummary } from '#lib/api-client.ts';
  import { carryBuiltTiles, watchNearViewport } from '#lib/viewport-fill.ts';

  // Two presentations of one list. A phone gets the iOS indexed list (Contacts, Music's Artists):
  // round portraits, sticky letter headers, and a vertical A–Z index down the trailing edge that
  // JUMPS to a letter. Desktop keeps the portrait grid and its letter row, which FILTERS. Which
  // artists are listed (Primary/All, Unreleased only) is the page's "View options" menu.

  /** Below this many artists an index is more chrome than it's worth — everything fits on a screen
      or two without it — so a phone hides it (desktop always shows its letter row). */
  const PHONE_INDEX_THRESHOLD = 20;

  type Props = {
    groups: GroupSummary[];
    /** href builder for an artist (links into `/library?artist=…`). */
    hrefFor: (group: GroupSummary) => string;
    isLoading?: boolean;
    /**
     * Cached portraits from the bulk `GET /api/artists/images` map, keyed by display label. A
     * cached artist points straight at the CDN URL (no per-tile redirect through the proxy);
     * one without a row yet falls back to {@link getArtistImageUrl}.
     */
    portraits?: ReadonlyMap<string, ArtistPortrait>;
    /**
     * What an empty list shows — the page knows why (a search miss) and what would undo it. An
     * empty desktop letter is told apart from it here: that one offers "Show all".
     */
    empty?: Snippet;
    /**
     * Hides the phone's A–Z index while something above the list is on screen (the Artists page's
     * shelves): the index is fixed down the trailing edge and would sit over it, taking its taps and
     * sideways swipes. It returns once that has scrolled away and the list fills the screen.
     */
    indexSuppressed?: boolean;
  };
  const {
    groups,
    hrefFor,
    isLoading = false,
    portraits,
    empty,
    indexSuppressed = false
  }: Props = $props();

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  const ALL_LETTERS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');
  /** The phone index: A–Z, then "#" for names that start with anything else (iOS puts it last). */
  const INDEX = [...ALL_LETTERS, '#'];

  function firstLetter(label: string): string {
    const c = label.trim()[0]?.toUpperCase() ?? '';
    return /[A-Z]/.test(c) ? c : '#';
  }

  const presentLetters = $derived.by(() => {
    const set = new Set<string>();
    for (const g of groups) set.add(firstLetter(g.label));
    return set;
  });

  // ── desktop: letter filter ─────────────────────────────────────────────────
  let letter = $state<'all' | string>('all');
  const filtered = $derived.by(() => {
    if (letter === 'all') return groups;
    return groups.filter((g) => firstLetter(g.label) === letter);
  });

  // ── phone: sections + index ────────────────────────────────────────────────
  const sections = $derived(
    INDEX.map((l) => ({
      letter: l,
      groups: groups.filter((g) => firstLetter(g.label) === l)
    })).filter((s) => s.groups.length > 0)
  );
  const showIndex = $derived(compact && groups.length >= PHONE_INDEX_THRESHOLD);

  const sectionEls: Record<string, HTMLElement | undefined> = {};
  const indexButtons: Record<string, HTMLButtonElement | undefined> = {};
  let lastJump: string | null = null;

  function jump(l: string) {
    if (!presentLetters.has(l)) return;
    lastJump = l;
    // The section (not its sticky header, whose box moves while it is stuck) carries a
    // scroll-margin of the nav bar's height, so the letter lands just under the bar.
    sectionEls[l]?.scrollIntoView({ block: 'start' });
  }

  // Scrubbing: drag a finger down the index and the list follows, the way the iOS index does. The
  // letter under the finger is found from the buttons' own boxes (the column is centred, so they
  // don't fill it evenly). Absent letters are inert — the list stays where it was.
  let scrubbing = false;
  function letterAt(y: number): string | null {
    for (const l of INDEX) {
      const r = indexButtons[l]?.getBoundingClientRect();
      if (r && y >= r.top && y < r.bottom) return l;
    }
    return null;
  }
  function onIndexDown(e: PointerEvent) {
    scrubbing = true;
    lastJump = null;
    (e.currentTarget as HTMLElement).setPointerCapture(e.pointerId);
    const l = letterAt(e.clientY);
    if (l) jump(l);
  }
  function onIndexMove(e: PointerEvent) {
    if (!scrubbing) return;
    const l = letterAt(e.clientY);
    if (l && l !== lastJump) jump(l);
  }
  function onIndexUp() {
    scrubbing = false;
  }

  // ── windowing ──────────────────────────────────────────────────────────────
  // Every artist keeps its link in the DOM (full scroll height, working A–Z jump, find-in-page on
  // the names), but the heavy inside — the portrait <Cover>/<img> — is built only for the first
  // screenful and then as tiles come within reach of the viewport (`#lib/viewport-fill.ts`, which
  // also holds tiles back while the list is flinging fast). With "Show featured artists" a large
  // library lists thousands of rows; mounting a portrait per row queued hundreds of off-screen
  // image requests that starved the tiles actually on screen.
  const FIRST_FILL = $derived(compact ? 20 : 36);
  let containerEl = $state<HTMLElement | null>(null);
  /** The list the watcher is built over: the letter-filtered one on desktop, all groups on a phone. */
  const watchList = $derived(compact ? groups : filtered);
  let builtKeys = $state.raw<ReadonlySet<string>>(
    carryBuiltTiles(
      new Set(),
      untrack(() => watchList),
      untrack(() => FIRST_FILL)
    )
  );
  $effect(() => {
    const container = containerEl;
    const list = watchList;
    const firstFill = FIRST_FILL;
    // Before the watcher check: a list that emptied has no container, and must still be forgotten.
    untrack(() => (builtKeys = carryBuiltTiles(builtKeys, list, firstFill)));
    if (!container) return;
    // A new watcher per list: a changed list can hold links that did not exist a moment ago.
    const watcher = watchNearViewport(container, {
      attribute: 'data-artist-key',
      onnear: (keys) =>
        untrack(() => {
          const fresh = keys.filter((key) => !builtKeys.has(key));
          if (fresh.length > 0) builtKeys = new Set([...builtKeys, ...fresh]);
        })
    });
    return () => watcher.destroy();
  });

  function portraitUrlFor(label: string): string {
    return portraits?.get(label)?.url ?? getArtistImageUrl(label);
  }
</script>

{#snippet emptyList()}
  {#if empty}
    {@render empty()}
  {:else}
    <EmptyState icon={Users} title="No artists" />
  {/if}
{/snippet}

{#if compact}
  {#if isLoading && groups.length === 0}
    <ul aria-hidden="true">
      {#each Array(10) as _, i (i)}
        <li class="flex min-h-14 items-center gap-3 px-4">
          <Skeleton class="size-11 shrink-0 rounded-full" />
          <div class="flex-1 space-y-1.5">
            <Skeleton class="h-3.5 w-1/2" />
            <Skeleton class="h-3 w-1/3" />
          </div>
        </li>
      {/each}
    </ul>
  {:else if groups.length === 0}
    {@render emptyList()}
  {:else}
    <div class={cn(showIndex && 'pr-7')} bind:this={containerEl}>
      {#each sections as section (section.letter)}
        <section
          bind:this={() => sectionEls[section.letter], (el) => (sectionEls[section.letter] = el)}
          aria-labelledby="artists-letter-{section.letter}"
          class="scroll-mt-[var(--mh-navbar-h,0px)]"
        >
          <!-- Sticks under the nav bar while its letter's artists scroll past. -->
          <h2
            id="artists-letter-{section.letter}"
            class="text-subheadline text-muted-foreground bg-background sticky top-[var(--mh-navbar-h,0px)] z-[5] px-4 py-1 font-semibold"
          >
            {section.letter}
          </h2>
          <ul>
            {#each section.groups as group, gi (group.key)}
              {@const built = builtKeys.has(group.key)}
              <!-- Every row keeps its link and text; only the portrait is deferred until the row
                   is within reach (there is no per-row virtual window here, so rows off screen skip
                   layout and paint: with "Show featured artists" a large library lists thousands). -->
              <li class="[contain-intrinsic-size:auto_56px] [content-visibility:auto]">
                <a
                  href={hrefFor(group)}
                  data-artist-key={group.key}
                  class={cn(
                    'focus-visible:ring-ring active:bg-accent relative flex min-h-14 items-center gap-3 px-4 py-1.5 transition-colors duration-100 focus-visible:ring-2 focus-visible:outline-hidden focus-visible:ring-inset',
                    // Inset hairline from the name (16 + 44 portrait + 12), none after a section's last.
                    gi < section.groups.length - 1 &&
                      "after:bg-separator after:absolute after:right-0 after:bottom-0 after:left-[72px] after:h-(--hairline) after:content-['']"
                  )}
                >
                  {#if built}
                    <Cover
                      artist={group.coverArtist}
                      title={group.coverTitle}
                      coverUrl={portraitUrlFor(group.label)}
                      fallbackUrl={group.coverUrl}
                      size={44}
                      corner={22}
                      caption={false}
                      dprCap={3}
                      class="shrink-0"
                    />
                  {:else}
                    <div class="bg-muted size-11 shrink-0 rounded-full" aria-hidden="true"></div>
                  {/if}
                  <span class="flex min-w-0 flex-1 flex-col">
                    <span class="text-body truncate">{group.label}</span>
                    <span class="text-subheadline text-muted-foreground truncate tabular-nums">
                      {group.albumCount} album{group.albumCount === 1 ? '' : 's'} · {group.trackCount}
                      track{group.trackCount === 1 ? '' : 's'}
                    </span>
                  </span>
                </a>
              </li>
            {/each}
          </ul>
        </section>
      {/each}
    </div>

    {#if showIndex && !indexSuppressed}
      <!-- The index: fixed to the trailing edge between the nav bar and the tab bar, a 44pt-wide
           hit column. touch-none so a scrub moves the list rather than the page. -->
      <nav
        aria-label="Artist index"
        class="fixed top-[calc(env(safe-area-inset-top)+56px)] right-[max(0px,env(safe-area-inset-right))] bottom-(--mh-content-pad) z-10 flex w-11 touch-none flex-col items-center justify-center select-none"
        onpointerdown={onIndexDown}
        onpointermove={onIndexMove}
        onpointerup={onIndexUp}
        onpointercancel={onIndexUp}
      >
        {#each INDEX as l (l)}
          {@const present = presentLetters.has(l)}
          <button
            bind:this={() => indexButtons[l], (el) => (indexButtons[l] = el)}
            type="button"
            tabindex={present ? 0 : -1}
            aria-disabled={!present || undefined}
            aria-label={l === '#' ? 'Jump to other names' : `Jump to ${l}`}
            onclick={() => jump(l)}
            class={cn(
              // 16pt a letter (the iOS index pitch), shrinking evenly on a short screen. Centred in
              // the column, the run of 27 clears the search field on a 393×852 phone.
              'text-caption-2 flex h-4 min-h-0 w-full shrink items-center justify-center leading-none font-semibold focus-visible:underline focus-visible:outline-hidden',
              present ? 'text-primary' : 'text-muted-foreground-dim'
            )}
          >
            {l}
          </button>
        {/each}
      </nav>
    {/if}
  {/if}
{:else}
  <!-- Desktop: the letter row filters the grid below it. -->
  <div
    role="group"
    aria-label="Filter by letter"
    class="border-border mb-4 flex flex-wrap items-center gap-0.5 border-b pb-3.5 text-[11px]"
  >
    <button
      type="button"
      onclick={() => (letter = 'all')}
      aria-pressed={letter === 'all'}
      class={cn(
        'hover:bg-muted text-foreground inline-flex min-h-7 min-w-7 items-center justify-center rounded-md px-2 transition-colors',
        letter === 'all' && 'bg-muted font-semibold'
      )}
    >
      All
    </button>
    {#each ALL_LETTERS as L (L)}
      {@const present = presentLetters.has(L)}
      <button
        type="button"
        disabled={!present}
        aria-pressed={letter === L}
        onclick={() => present && (letter = L)}
        class={cn(
          'inline-flex min-h-7 min-w-7 items-center justify-center rounded-md px-2 transition-colors',
          letter === L && 'bg-muted font-semibold',
          // A letter you can pick reads as text; one with no artists recedes (the phone index's rule).
          present ? 'text-foreground hover:bg-muted' : 'text-muted-foreground-dim cursor-default'
        )}
      >
        {L}
      </button>
    {/each}
  </div>

  {#if isLoading && groups.length === 0}
    <!-- Skeleton tiles in the real grid, not a spinner or a sentence. -->
    <div class="grid grid-cols-4 gap-x-4 gap-y-6 md:grid-cols-5 lg:grid-cols-6 xl:grid-cols-7">
      {#each Array(14) as _, i (i)}
        <div class="flex flex-col items-center gap-2 p-1">
          <Skeleton class="aspect-square w-full rounded-full" />
          <div class="w-full space-y-1.5 px-0.5">
            <Skeleton class="mx-auto h-3 w-3/4" />
            <Skeleton class="mx-auto h-3 w-1/2" />
          </div>
        </div>
      {/each}
    </div>
  {:else if groups.length === 0}
    {@render emptyList()}
  {:else if filtered.length === 0}
    <!-- A letter whose artists the search has narrowed away: say which letter, and undo it. -->
    <EmptyState
      icon={Users}
      title={`No artists under “${letter}”`}
      hint="Your search leaves none that start with this letter."
      action={{ label: 'Show all letters', onclick: () => (letter = 'all') }}
    />
  {:else}
    <div
      class="grid grid-cols-4 gap-x-4 gap-y-6 md:grid-cols-5 lg:grid-cols-6 xl:grid-cols-7"
      bind:this={containerEl}
    >
      {#each filtered as group (group.key)}
        {@const built = builtKeys.has(group.key)}
        <a
          href={hrefFor(group)}
          data-artist-key={group.key}
          class="group focus-visible:ring-ring flex flex-col items-center gap-2 rounded-lg p-1 transition-transform [contain-intrinsic-size:auto_11rem] [content-visibility:auto] hover:-translate-y-0.5 focus-visible:ring-2 focus-visible:ring-offset-2 focus-visible:outline-hidden"
          aria-label={`Browse ${group.label}`}
        >
          {#if built}
            <Cover
              artist={group.coverArtist}
              title={group.coverTitle}
              coverUrl={portraitUrlFor(group.label)}
              fallbackUrl={group.coverUrl}
              size={160}
              corner={80}
              caption={false}
              interactive
              class="aspect-square !h-auto !w-full !rounded-full shadow-[0_2px_10px_rgba(0,0,0,0.12)] hover:shadow-[0_8px_24px_rgba(0,0,0,0.18)] dark:shadow-[0_4px_14px_rgba(0,0,0,0.5)] dark:hover:shadow-[0_10px_28px_rgba(0,0,0,0.6)]"
            />
          {:else}
            <!-- Not built yet (see `builtKeys`): the link is already a working tile of the right
                 size, with the name kept for find-in-page. -->
            <div class="bg-muted aspect-square w-full rounded-full" aria-hidden="true"></div>
          {/if}
          <div class="w-full min-w-0 px-0.5 text-center">
            <p class="truncate text-[12.5px] font-medium">{group.label}</p>
            <!-- No `truncate` here on purpose (vis-10): the album count alone reads as the whole
                 story when the track count clips off, so this wraps to a second line instead. -->
            <p class="text-muted-foreground text-[11.5px] tabular-nums">
              {group.albumCount} album{group.albumCount === 1 ? '' : 's'} · {group.trackCount} track{group.trackCount ===
              1
                ? ''
                : 's'}
            </p>
          </div>
        </a>
      {/each}
    </div>
  {/if}
{/if}

<script lang="ts">
  import { Check, ChevronRight, ListFilter, Loader2, TriangleAlert } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Button } from '$lib/components/ui/button';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import { Switch } from '$lib/components/ui/switch';
  import { SegmentedControl } from '$lib/components/ui/segmented-control';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import FilterChip from '$lib/components/v2/FilterChip.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import {
    ApiError,
    fetchHistory,
    type HistoryCategory,
    type HistoryRawChange,
    type HistorySummary
  } from '$lib/api-client';
  import { formatDayLabel, formatRelativeTime, localDayKey } from '$lib/formatters';
  import {
    HISTORY_CATEGORIES,
    HISTORY_RANGES,
    HISTORY_TINT_BADGE,
    HISTORY_TINT_EDGE,
    historyFilterSummary,
    historyIcon,
    isDefaultHistoryFilter,
    type HistoryRangeKey
  } from '$lib/history';
  import { cn } from '$lib/utils';

  // The History feed. A desktop keeps its chip bands in the toolbar (range, Problems, the ten
  // categories — one click each). A phone cannot fit three sideways-scrolling rows of chips
  // above the feed, so it gets one Filter button in the nav bar that opens a sheet (Range as a
  // segmented control, Problems as a switch, the categories as a checklist with their counts),
  // and the subtitle under the title says what the feed is currently showing. The checklist
  // leaves out the categories' blurbs (the desktop chips keep them as tooltips): eleven two-line
  // rows made the sheet a scroll of its own, and the names with their icons already say it.
  // Filters apply as they change, like Mail's; the sheet's Done just closes it.

  /** Feed rows carry every underlying occurrence; render a slice and count the rest. */
  const MAX_EXPANDED_CHANGES = 20;

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  let range = $state<HistoryRangeKey>('7');
  let customFrom = $state<string>('');
  let customTo = $state<string>('');
  let categories = $state<Set<HistoryCategory>>(new Set());
  let problemsOnly = $state(false);
  let filtersOpen = $state(false);

  let summaries = $state<HistorySummary[]>([]);
  let counts = $state<Partial<Record<HistoryCategory, number>>>({});
  let nextCursor = $state<string | null>(null);
  let totalEvents = $state(0);
  let loading = $state(true);
  let loadingMore = $state(false);
  let error = $state<string | null>(null);
  /** The endpoint is admin-only while the nav deliberately shows this page to the demo account. */
  let forbidden = $state(false);
  let expanded = $state<Set<string>>(new Set());

  // The {from,to} ISO window for the current range. `null` for a half-filled custom range.
  const dateWindow = $derived.by((): { from?: string; to?: string } | null => {
    if (range === 'custom') {
      if (!customFrom || !customTo) return null;
      // Inclusive end-of-day for `to` so the whole day is covered.
      return {
        from: new Date(customFrom).toISOString(),
        to: new Date(`${customTo}T23:59:59.999`).toISOString()
      };
    }
    const to = new Date();
    const from = new Date();
    if (range === '1') from.setHours(0, 0, 0, 0);
    else from.setDate(from.getDate() - Number(range));
    return { from: from.toISOString(), to: to.toISOString() };
  });

  const selected = $derived([...categories]);
  const anythingInWindow = $derived(Object.values(counts).some((n) => (n ?? 0) > 0));
  const problemCount = $derived(summaries.filter((s) => s.tint === 'warn' || s.tint === 'err').length);

  const filterState = $derived({ range, customFrom, customTo, problemsOnly, categories });
  const filtersActive = $derived(!isDefaultHistoryFilter(filterState));

  // Phones carry the filters in a sheet, so the subtitle names them; the desktop's chips already
  // show them, so its meta is just the count. An account the feed refuses (the demo) gets no
  // figure at all — "0 events · Last 7 days" over "History is for administrators" contradicts it.
  const headerMeta = $derived.by(() => {
    if (forbidden) return undefined;
    const events = loading
      ? null
      : `${totalEvents.toLocaleString()} event${totalEvents === 1 ? '' : 's'}`;
    if (!compact) return events ?? undefined;
    return [events, historyFilterSummary(filterState)].filter(Boolean).join(' · ');
  });

  /** Rows grouped under a "Today / Yesterday / Tue 12 Aug" header, in the viewer's own timezone. */
  const days = $derived.by(() => {
    const groups: { key: string; label: string; rows: HistorySummary[] }[] = [];
    for (const s of summaries) {
      const key = localDayKey(s.latestWrittenAtUtc);
      const last = groups.at(-1);
      if (last?.key === key) last.rows.push(s);
      else groups.push({ key, label: formatDayLabel(s.latestWrittenAtUtc), rows: [s] });
    }
    return groups;
  });

  // Last-writer-wins guard: switching range and category in quick succession fires overlapping
  // requests, and without this the slower one paints over the newer selection.
  let reqSeq = 0;
  let inFlight: AbortController | null = null;

  async function load() {
    const w = dateWindow;
    if (w === null) {
      summaries = [];
      loading = false;
      return;
    }
    const seq = ++reqSeq;
    inFlight?.abort();
    const controller = new AbortController();
    inFlight = controller;
    loading = true;
    try {
      const res = await fetchHistory(
        { ...w, category: selected, problems: problemsOnly || undefined },
        controller.signal
      );
      if (seq !== reqSeq) return;
      summaries = res.summaries;
      counts = res.categoryCounts ?? {};
      nextCursor = res.nextCursor ?? null;
      totalEvents = res.totalEventsInWindow;
      expanded = new Set();
      error = null;
      forbidden = false;
    } catch (e) {
      if (seq !== reqSeq || (e instanceof DOMException && e.name === 'AbortError')) return;
      if (e instanceof ApiError && e.status === 403) {
        forbidden = true;
        summaries = [];
        error = null;
      } else {
        error = e instanceof Error ? e.message : 'Failed to load history';
      }
    } finally {
      if (seq === reqSeq) loading = false;
    }
  }

  async function loadMore() {
    const w = dateWindow;
    if (w === null || nextCursor == null) return;
    loadingMore = true;
    try {
      const res = await fetchHistory({
        ...w,
        category: selected,
        problems: problemsOnly || undefined,
        cursor: nextCursor
      });
      // De-dupe on append. The feed merges ten sources into one timestamp-ordered list, and a
      // duplicate key in the {#each} below is a white screen, not a cosmetic glitch — this repo has
      // shipped that crash twice.
      const seen = new Set(summaries.map((s) => s.id));
      summaries = [...summaries, ...res.summaries.filter((s) => !seen.has(s.id))];
      nextCursor = res.nextCursor ?? null;
    } catch (e) {
      if (!(e instanceof DOMException && e.name === 'AbortError')) {
        error = e instanceof Error ? e.message : 'Failed to load more';
      }
    } finally {
      loadingMore = false;
    }
  }

  function toggleExpanded(id: string) {
    const next = new Set(expanded);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    expanded = next;
  }

  function toggleCategory(id: HistoryCategory) {
    const next = new Set(categories);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    categories = next;
  }

  function resetFilters() {
    range = '7';
    customFrom = '';
    customTo = '';
    problemsOnly = false;
    categories = new Set();
  }

  // Reload whenever any part of the query changes.
  $effect(() => {
    void dateWindow;
    void selected;
    void problemsOnly;
    void load();
  });

  const rangeItems = HISTORY_RANGES.map((r) => ({ value: r.key, label: r.label }));
  // A date input's value is yyyy-mm-dd; "today" in the viewer's own zone bounds both pickers.
  const today = $derived(localDayKey(new Date().toISOString()));

  function artistHref(s: HistorySummary): string | null {
    return s.albumArtist ? `/library?artist=${encodeURIComponent(s.albumArtist)}` : null;
  }

  // `${artistLower}::${titleLower}` is the shape the library resolves alongside a folder key —
  // see albumKeyForSong. The feed carries display names only, which is exactly what that shape wants.
  function albumHref(s: HistorySummary): string | null {
    if (!s.album) return null;
    const key = `${(s.albumArtist ?? '').toLowerCase()}::${s.album.toLowerCase()}`;
    return `/library?album=${encodeURIComponent(key)}`;
  }

  /**
   * Whether opening the row would show anything. Several entries — a scan run, a version change, a
   * playlist export — say everything they have in the headline and its subtitle, and a chevron that
   * reveals a restatement of the headline is worse than no chevron.
   */
  function canExpand(s: HistorySummary): boolean {
    return s.changes.some((c) => c.songId != null || !!c.trackTitle || !!c.detail || c.newValue != null);
  }

  /** Turns a backend field key ("albumartist") into a readable word ("Album artist"). */
  function humanizeField(field: string): string {
    const spaced = field.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/[_-]+/g, ' ');
    const lower = spaced.toLowerCase();
    return lower.charAt(0).toUpperCase() + lower.slice(1);
  }

  function describeChange(c: HistoryRawChange): string {
    // Derived entries carry prose rather than a field diff; only destination writes have old → new.
    if (c.oldValue == null && c.newValue == null) return c.detail ?? humanizeField(c.field);
    const field = humanizeField(c.field);
    if (c.oldValue == null && c.newValue != null) return `${field} set to "${c.newValue}"`;
    if (c.oldValue != null && c.newValue == null) return `${field} removed (was "${c.oldValue}")`;
    return `${field} changed from "${c.oldValue ?? '—'}" to "${c.newValue ?? '—'}"`;
  }

  // The sheet's date fields: 16px (so focusing one never zooms the page) in a 36pt pill, inside a
  // label that reaches the row's full 44pt — the row's own 8px padding is pulled back under it.
  // The field itself is the 44pt target (a tap on a wrapping label did not always reach the
  // picker), while the fill it draws stays iOS's 36pt compact-picker pill: the pill is the label's
  // ::before, inset 4px top and bottom, and the input over it is transparent.
  const DATE_TARGET =
    'relative -my-2 flex h-11 items-center before:pointer-events-none before:absolute before:inset-x-0 before:inset-y-1 before:rounded-lg before:bg-muted has-[input:focus-visible]:before:ring-3 before:ring-ring/50';
  const DATE_FIELD = 'relative h-11 bg-transparent px-2.5 text-base outline-none';

  // An in-row text link (track, album, artist): 44pt tall on touch via a pseudo-element.
  const INLINE_LINK =
    'relative outline-none hover:underline focus-visible:underline pointer-coarse:after:absolute pointer-coarse:after:-inset-y-3 pointer-coarse:after:-inset-x-1';
</script>

<!-- ── desktop chip bands (a phone uses the sheet below) ───────────────────── -->
{#snippet rangeBand()}
  {#each HISTORY_RANGES as r (r.key)}
    <FilterChip pressed={range === r.key} onclick={() => (range = r.key)}>{r.label}</FilterChip>
  {/each}
  {#if range === 'custom'}
    <input
      type="date"
      bind:value={customFrom}
      max={customTo || today}
      aria-label="From date"
      class="bg-input text-nav-sm focus-visible:ring-ring/50 h-8 shrink-0 rounded-full px-3 outline-none focus-visible:ring-3"
    />
    <span class="text-muted-foreground text-nav-xs shrink-0" aria-hidden="true">→</span>
    <input
      type="date"
      bind:value={customTo}
      min={customFrom || undefined}
      max={today}
      aria-label="To date"
      class="bg-input text-nav-sm focus-visible:ring-ring/50 h-8 shrink-0 rounded-full px-3 outline-none focus-visible:ring-3"
    />
  {/if}
  <span class="bg-separator mx-1 h-5 w-px shrink-0" aria-hidden="true"></span>
  <!-- Severity is a different axis from subsystem, so it sits with the range rather than among
       the category chips — and stays reachable when eleven of those overflow their band. -->
  <FilterChip
    pressed={problemsOnly}
    onclick={() => (problemsOnly = !problemsOnly)}
    icon={TriangleAlert}
    title="Only the failures and warnings">Problems</FilterChip
  >
{/snippet}

{#snippet categoryBand()}
  <FilterChip
    pressed={categories.size === 0}
    onclick={() => (categories = new Set())}
    title="Every kind of change">All</FilterChip
  >
  {#each HISTORY_CATEGORIES as c (c.id)}
    <FilterChip
      pressed={categories.has(c.id)}
      onclick={() => toggleCategory(c.id)}
      icon={c.icon}
      count={counts[c.id] ?? 0}
      title={c.blurb}>{c.label}</FilterChip
    >
  {/each}
{/snippet}

{#snippet message(title: string, body?: string)}
  <div class="mx-auto max-w-md px-6 py-14 text-center">
    <p class="text-headline md:text-sm md:font-medium">{title}</p>
    {#if body}
      <p class="text-callout text-muted-foreground mt-1 md:text-sm">{body}</p>
    {/if}
  </div>
{/snippet}

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1">
    <!-- No filters for an account the feed refuses: there is nothing for them to narrow. -->
    <PageToolbarV2
      title="History"
      meta={headerMeta}
      grouped
      filters={compact || forbidden ? undefined : rangeBand}
      filterRow={compact || forbidden ? undefined : categoryBand}
    >
      {#snippet actions()}
        {#if compact && !forbidden}
          <Button
            variant="ghost"
            size="icon"
            class="relative"
            aria-label={filtersActive ? 'Filters, some applied' : 'Filters'}
            aria-haspopup="dialog"
            onclick={() => (filtersOpen = true)}
          >
            <ListFilter aria-hidden="true" />
            {#if filtersActive}
              <!-- A tint dot on the glyph says "filtered" at a glance, as iOS fills the icon. -->
              <span
                class="bg-primary ring-background absolute top-2 right-2 size-2 rounded-full ring-2"
                aria-hidden="true"
              ></span>
            {/if}
          </Button>
        {/if}
      {/snippet}
    </PageToolbarV2>

    <div class="mx-auto flex w-full max-w-3xl flex-col gap-6 pt-2 pb-8 md:px-7 md:pt-6">
      {#if forbidden}
        {@render message(
          'History is for administrators',
          'This page shows everything MusicHoarder has done to a library — the account you are signed in with does not own one.'
        )}
      {:else if error}
        <GroupedList.Section>
          <GroupedList.Row
            icon={TriangleAlert}
            iconClass="bg-destructive/12 text-destructive-text"
            label="Couldn't load history"
            sublabel={error}
          >
            {#snippet trailing()}
              <Button variant="ghost" class="text-primary hover:text-primary h-11 md:h-8" onclick={() => void load()}>
                Retry
              </Button>
            {/snippet}
          </GroupedList.Row>
        </GroupedList.Section>
      {:else if range === 'custom' && dateWindow === null}
        <div class="flex flex-col items-center">
          {@render message('Pick a start and end date')}
          {#if compact}
            <Button variant="gray" size="pill" class="text-primary -mt-8" onclick={() => (filtersOpen = true)}>
              Choose dates
            </Button>
          {/if}
        </div>
      {:else if loading}
        <GroupedList.Section>
          {#each Array(6) as _, i (i)}
            <div class="flex items-center gap-3 px-4 py-3">
              <Skeleton class="size-[29px] shrink-0 rounded-[7px]" />
              <div class="min-w-0 flex-1 space-y-1.5">
                <Skeleton class="h-4 w-2/3" />
                <Skeleton class="h-3 w-1/3" />
              </div>
              <Skeleton class="h-3 w-10 shrink-0" />
            </div>
          {/each}
        </GroupedList.Section>
      {:else if summaries.length === 0}
        {#if problemsOnly && !anythingInWindow}
          {@render message(
            'Nothing went wrong in this range',
            'No failed downloads, builds, lookups or syncs. Turn off Problems to see everything else.'
          )}
        {:else if anythingInWindow}
          {@render message(
            'Nothing in the categories you picked',
            'There is activity in this range, just not of this kind. Choose All to see it.'
          )}
        {:else}
          {@render message(
            'Nothing happened in this range',
            'History covers everything MusicHoarder does on its own — downloading, identifying, building, fetching lyrics and videos — as well as what you do by hand. Try a wider range.'
          )}
        {/if}
      {:else}
        {#if problemCount > 0 && !problemsOnly}
          <GroupedList.Section>
            <GroupedList.Row
              onclick={() => (problemsOnly = true)}
              icon={TriangleAlert}
              iconClass="bg-warning/15 text-warning-text"
              chevron
            >
              <span class="text-body text-warning-text md:text-sm">
                {problemCount} of these need a look
              </span>
            </GroupedList.Row>
          </GroupedList.Section>
        {/if}

        {#each days as day (day.key)}
          <!-- The day wrapper bounds the sticky header, so each day's title rides under the nav
               bar until the next day's pushes it off. On a phone the bar has no fill of its own
               (only the scroll edge, which fades out over its lower part), so the pinned header
               carries an opaque band up under that fade: without it the row scrolling past shows
               through between the bar's title and the header. -->
          <section class="relative">
            <h2
              class="bg-background-grouped text-footnote text-muted-foreground max-md:before:bg-background-grouped sticky top-(--mh-navbar-h,0px) z-10 px-8 pt-1 pb-1.5 max-md:before:pointer-events-none max-md:before:absolute max-md:before:inset-x-0 max-md:before:bottom-full max-md:before:h-4 md:px-4"
            >
              {day.label}
            </h2>
            <GroupedList.Section>
              {#each day.rows as s (s.id)}
                {@const Icon = historyIcon(s.kind, s.category)}
                {@const isOpen = expanded.has(s.id)}
                {@const shown = s.changes.slice(0, MAX_EXPANDED_CHANGES)}
                {@const subtitle = s.detail || [s.albumArtist, s.album].filter(Boolean).join(' — ')}
                {@const expandable = canExpand(s)}
                <GroupedList.Row
                  onclick={expandable ? () => toggleExpanded(s.id) : undefined}
                  aria-expanded={expandable ? isOpen : undefined}
                  icon={Icon}
                  iconClass={HISTORY_TINT_BADGE[s.tint]}
                  label={s.headline}
                  sublabel={subtitle || undefined}
                >
                  {#if HISTORY_TINT_EDGE[s.tint]}
                    <!-- A problem's leading edge, positioned against the row itself. -->
                    <span
                      aria-hidden="true"
                      class={cn('absolute inset-y-0 left-0 w-[3px]', HISTORY_TINT_EDGE[s.tint])}
                    ></span>
                  {/if}
                  {#snippet trailing()}
                    <span class="flex items-center gap-1.5">
                      <span class="text-footnote text-muted-foreground whitespace-nowrap md:text-xs"
                        >{formatRelativeTime(s.latestWrittenAtUtc)}</span
                      >
                      {#if expandable}
                        <ChevronRight
                          aria-hidden="true"
                          class={cn(
                            'text-muted-foreground-dim -mr-1 size-4 transition-transform duration-200 ease-[cubic-bezier(0.23,1,0.32,1)]',
                            isOpen && 'rotate-90'
                          )}
                          strokeWidth={2.5}
                        />
                      {/if}
                    </span>
                  {/snippet}
                </GroupedList.Row>

                {#if isOpen && expandable}
                  <!-- The opened entry, indented to the row's label. A sibling of the row (not
                       inside its button) so the track and album links stay real links. -->
                  <div
                    class="after:bg-separator relative pr-4 pb-3 pl-[57px] after:absolute after:right-0 after:bottom-0 after:left-[57px] after:h-(--hairline) last:after:hidden"
                  >
                    {#if s.detail && (s.albumArtist || s.album)}
                      <p class="text-footnote text-muted-foreground mb-2 md:text-xs">
                        {[s.albumArtist, s.album].filter(Boolean).join(' — ')}
                      </p>
                    {/if}
                    <ul class="text-subheadline space-y-1.5 md:text-sm">
                      {#each shown as c, ci (ci)}
                        <li class="flex flex-wrap items-baseline gap-x-1.5">
                          {#if c.songId != null}
                            <a href={`/track/${c.songId}`} class={cn('text-foreground font-medium', INLINE_LINK)}>
                              {c.trackTitle ?? `#${c.songId}`}
                            </a>
                            <span class="text-muted-foreground" aria-hidden="true">—</span>
                          {:else if c.trackTitle}
                            <span class="text-foreground font-medium">{c.trackTitle}</span>
                            <span class="text-muted-foreground" aria-hidden="true">—</span>
                          {/if}
                          <span class="text-muted-foreground">{describeChange(c)}</span>
                        </li>
                      {/each}
                    </ul>
                    {#if s.changes.length > shown.length}
                      <p class="text-footnote text-muted-foreground mt-2 md:text-xs">
                        and {(s.changes.length - shown.length).toLocaleString()} more
                      </p>
                    {/if}

                    {#if artistHref(s) || albumHref(s)}
                      <div class="text-subheadline mt-3 flex flex-wrap gap-x-5 gap-y-2 md:text-xs">
                        {#if albumHref(s)}
                          <a href={albumHref(s)} class={cn('text-primary', INLINE_LINK)}>Open {s.album}</a>
                        {/if}
                        {#if artistHref(s)}
                          <a href={artistHref(s)} class={cn('text-primary', INLINE_LINK)}
                            >All of {s.albumArtist}</a
                          >
                        {/if}
                      </div>
                    {/if}
                  </div>
                {/if}
              {/each}
            </GroupedList.Section>
          </section>
        {/each}

        {#if nextCursor != null}
          <GroupedList.Section>
            <GroupedList.Row onclick={loadMore} disabled={loadingMore}>
              <span class="text-body flex items-center gap-2 md:text-sm {loadingMore ? 'text-muted-foreground' : 'text-primary'}">
                {#if loadingMore}<Loader2 class="size-4 animate-spin" aria-hidden="true" /> Loading…{:else}Load older{/if}
              </span>
            </GroupedList.Row>
          </GroupedList.Section>
        {/if}
      {/if}
    </div>
  </ScrollArea>
</div>

<!-- Phone filters. Changes apply as they are made; Done closes, Reset returns to the defaults. -->
{#if compact}
  <BottomSheet.Root bind:open={filtersOpen} title="Filters">
    {#snippet leading()}
      {#if filtersActive}
        <BottomSheet.Action onclick={resetFilters}>Reset</BottomSheet.Action>
      {/if}
    {/snippet}
    {#snippet trailing()}
      <BottomSheet.Action prominent onclick={() => (filtersOpen = false)}>Done</BottomSheet.Action>
    {/snippet}

    <div class="flex flex-col gap-6 pt-2">
      <div class="px-4">
        <SegmentedControl items={rangeItems} bind:value={range} label="Range" />
      </div>

      {#if range === 'custom'}
        <!-- Native date pickers (iOS's wheel), 16px so focusing one never zooms the page. The
             field is 44pt tall and draws iOS's 36pt pill behind it (DATE_TARGET). -->
        <GroupedList.Section header="Custom range">
          <GroupedList.Row label="From">
            {#snippet trailing()}
              <label class={DATE_TARGET}>
                <input
                  type="date"
                  bind:value={customFrom}
                  max={customTo || today}
                  aria-label="From date"
                  class={DATE_FIELD}
                />
              </label>
            {/snippet}
          </GroupedList.Row>
          <GroupedList.Row label="To">
            {#snippet trailing()}
              <label class={DATE_TARGET}>
                <input
                  type="date"
                  bind:value={customTo}
                  min={customFrom || undefined}
                  max={today}
                  aria-label="To date"
                  class={DATE_FIELD}
                />
              </label>
            {/snippet}
          </GroupedList.Row>
        </GroupedList.Section>
      {/if}

      <GroupedList.Section footer="Only the failures and warnings.">
        <GroupedList.Row label="Problems only">
          {#snippet trailing()}
            <Switch bind:checked={problemsOnly} aria-label="Problems only" />
          {/snippet}
        </GroupedList.Row>
      </GroupedList.Section>

      <GroupedList.Section header="Categories" footer="Counts cover the chosen range.">
        <GroupedList.Row
          onclick={() => (categories = new Set())}
          aria-pressed={categories.size === 0}
          label="All"
        >
          {#snippet trailing()}
            {#if categories.size === 0}
              <Check class="text-primary size-5" strokeWidth={2.5} aria-hidden="true" />
            {/if}
          {/snippet}
        </GroupedList.Row>
        {#each HISTORY_CATEGORIES as c (c.id)}
          <GroupedList.Row
            onclick={() => toggleCategory(c.id)}
            aria-pressed={categories.has(c.id)}
            icon={c.icon}
            label={c.label}
            value={(counts[c.id] ?? 0).toLocaleString()}
          >
            {#snippet trailing()}
              <!-- A fixed-width slot, so the counts line up whether or not a row is ticked. -->
              <span class="flex w-5 justify-end">
                {#if categories.has(c.id)}
                  <Check class="text-primary size-5" strokeWidth={2.5} aria-hidden="true" />
                {/if}
              </span>
            {/snippet}
          </GroupedList.Row>
        {/each}
      </GroupedList.Section>
    </div>
  </BottomSheet.Root>
{/if}

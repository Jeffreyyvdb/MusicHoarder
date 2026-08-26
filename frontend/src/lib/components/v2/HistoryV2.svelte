<script lang="ts">
  import { ChevronRight, History, TriangleAlert } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Button } from '$lib/components/ui/button';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import FilterChip from '$lib/components/v2/FilterChip.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
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
    HISTORY_TINT_BADGE,
    HISTORY_TINT_EDGE,
    historyIcon
  } from '$lib/history';
  import { cn } from '$lib/utils';

  type RangeKey = '1' | '7' | '30' | 'custom';

  /** Feed rows carry every underlying occurrence; render a slice and count the rest. */
  const MAX_EXPANDED_CHANGES = 20;

  let range = $state<RangeKey>('7');
  let customFrom = $state<string>('');
  let customTo = $state<string>('');
  let categories = $state<Set<HistoryCategory>>(new Set());
  let problemsOnly = $state(false);

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

  const headerMeta = $derived(
    loading ? undefined : `${totalEvents.toLocaleString()} event${totalEvents === 1 ? '' : 's'}`
  );

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

  // Reload whenever any part of the query changes.
  $effect(() => {
    void dateWindow;
    void selected;
    void problemsOnly;
    void load();
  });

  const RANGES: { key: RangeKey; label: string }[] = [
    { key: '1', label: 'Today' },
    { key: '7', label: '7 days' },
    { key: '30', label: '30 days' },
    { key: 'custom', label: 'Custom' }
  ];

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
</script>

<div class="flex min-h-0 flex-1 flex-col">
  <PageToolbarV2 icon={History} title="Library history" meta={headerMeta}>
    {#snippet filters()}
      {#each RANGES as r (r.key)}
        <FilterChip pressed={range === r.key} onclick={() => (range = r.key)}>{r.label}</FilterChip>
      {/each}
      {#if range === 'custom'}
        <input
          type="date"
          bind:value={customFrom}
          aria-label="From date"
          class="border-border bg-card text-nav-sm h-8 shrink-0 rounded-full border px-3"
        />
        <span class="text-muted-foreground text-nav-xs shrink-0">→</span>
        <input
          type="date"
          bind:value={customTo}
          aria-label="To date"
          class="border-border bg-card text-nav-sm h-8 shrink-0 rounded-full border px-3"
        />
      {/if}
      <span class="bg-border mx-1 h-5 w-px shrink-0"></span>
      <!-- Severity is a different axis from subsystem, so it sits with the range rather than among
           the category chips — and stays reachable when eleven of those overflow their band. -->
      <FilterChip
        pressed={problemsOnly}
        onclick={() => (problemsOnly = !problemsOnly)}
        icon={TriangleAlert}
        title="Only the failures and warnings">Problems</FilterChip
      >
    {/snippet}

    {#snippet filterRow()}
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
  </PageToolbarV2>

  <ScrollArea class="min-h-0 flex-1">
    <div class="px-4 py-4 sm:px-7 sm:py-5">
      {#if forbidden}
        <div class="border-border rounded-lg border border-dashed px-6 py-12 text-center">
          <p class="text-sm font-medium">History is for administrators</p>
          <p class="text-muted-foreground mx-auto mt-1 max-w-md text-sm">
            This page shows everything MusicHoarder has done to a library — the account you are
            signed in with does not own one.
          </p>
        </div>
      {:else if error}
        <div
          class="border-destructive/40 bg-destructive/10 text-destructive flex flex-wrap items-center gap-3 rounded-md border px-4 py-3 text-sm"
        >
          <span class="min-w-0 flex-1">{error}</span>
          <Button variant="outline" size="sm" onclick={() => void load()}>Retry</Button>
        </div>
      {:else if range === 'custom' && dateWindow === null}
        <p class="text-muted-foreground text-sm">Pick a start and end date.</p>
      {:else if loading}
        <ul class="space-y-2">
          {#each Array(6) as _, i (i)}
            <li class="border-border bg-card flex items-center gap-3 rounded-lg border px-4 py-3">
              <Skeleton class="size-8 shrink-0 rounded-md" />
              <div class="min-w-0 flex-1 space-y-1.5">
                <Skeleton class="h-4 w-2/3" />
                <Skeleton class="h-3 w-1/3" />
              </div>
              <Skeleton class="h-3 w-10 shrink-0" />
            </li>
          {/each}
        </ul>
      {:else if summaries.length === 0}
        <div class="border-border rounded-lg border border-dashed px-6 py-12 text-center">
          {#if problemsOnly && !anythingInWindow}
            <p class="text-sm font-medium">Nothing went wrong in this range</p>
            <p class="text-muted-foreground mx-auto mt-1 max-w-md text-sm">
              No failed downloads, builds, lookups or syncs. Turn off Problems to see everything else.
            </p>
          {:else if anythingInWindow}
            <p class="text-sm font-medium">Nothing in the categories you picked</p>
            <p class="text-muted-foreground mx-auto mt-1 max-w-md text-sm">
              There is activity in this range, just not of this kind. Press All to see it.
            </p>
          {:else}
            <p class="text-sm font-medium">Nothing happened in this range</p>
            <p class="text-muted-foreground mx-auto mt-1 max-w-md text-sm">
              History covers everything MusicHoarder does on its own — downloading, identifying,
              building, fetching lyrics and videos — as well as what you do by hand. Try a wider range.
            </p>
          {/if}
        </div>
      {:else}
        {#if problemCount > 0 && !problemsOnly}
          <button
            type="button"
            class="text-nav-sm mb-3 inline-flex items-center gap-1.5 text-amber-600 hover:underline dark:text-amber-400"
            onclick={() => (problemsOnly = true)}
          >
            <TriangleAlert class="size-3.5" aria-hidden="true" />
            {problemCount} of these need a look
          </button>
        {/if}

        {#each days as day (day.key)}
          <section class="mb-4 last:mb-0">
            <h2
              class="bg-background text-muted-foreground text-nav-xs sticky top-0 z-10 -mx-1 mb-2 px-1 py-1 font-medium tracking-wide uppercase"
            >
              {day.label}
            </h2>
            <ul class="space-y-2">
              {#each day.rows as s (s.id)}
                {@const Icon = historyIcon(s.kind, s.category)}
                {@const isOpen = expanded.has(s.id)}
                {@const shown = s.changes.slice(0, MAX_EXPANDED_CHANGES)}
                {@const subtitle = s.detail || [s.albumArtist, s.album].filter(Boolean).join(' — ')}
                {@const expandable = canExpand(s)}
                <li
                  class={cn(
                    'border-border bg-card rounded-lg border border-l-2',
                    HISTORY_TINT_EDGE[s.tint]
                  )}
                >
                  <button
                    type="button"
                    class={cn(
                      'flex w-full items-center gap-3 rounded-lg px-4 py-3 text-left transition-colors',
                      expandable && 'hover:bg-muted/50 active:bg-muted'
                    )}
                    disabled={!expandable}
                    onclick={() => toggleExpanded(s.id)}
                  >
                    <span
                      class={cn(
                        'grid size-8 shrink-0 place-items-center rounded-md',
                        HISTORY_TINT_BADGE[s.tint]
                      )}
                    >
                      <Icon class="size-4" aria-hidden="true" />
                    </span>
                    <div class="min-w-0 flex-1">
                      <div class="truncate text-sm font-medium">{s.headline}</div>
                      {#if subtitle}
                        <div class="text-muted-foreground mt-0.5 truncate text-xs">{subtitle}</div>
                      {/if}
                    </div>
                    <span class="text-muted-foreground shrink-0 text-xs"
                      >{formatRelativeTime(s.latestWrittenAtUtc)}</span
                    >
                    <ChevronRight
                      class={cn(
                        'size-4 shrink-0 transition-transform',
                        expandable ? 'text-foreground/70' : 'invisible',
                        isOpen && 'rotate-90'
                      )}
                    />
                  </button>

                  {#if isOpen && expandable}
                    <div class="border-border border-t px-4 py-3">
                      {#if s.detail && (s.albumArtist || s.album)}
                        <p class="text-muted-foreground mb-2 text-xs">
                          {[s.albumArtist, s.album].filter(Boolean).join(' — ')}
                        </p>
                      {/if}
                      <ul class="space-y-1.5 text-sm">
                        {#each shown as c, ci (ci)}
                          <li class="text-foreground/90 flex flex-wrap items-baseline gap-x-1.5">
                            {#if c.songId != null}
                              <a
                                href={`/track/${c.songId}`}
                                class="text-foreground font-medium hover:underline"
                              >
                                {c.trackTitle ?? `#${c.songId}`}
                              </a>
                              <span class="text-muted-foreground">—</span>
                            {:else if c.trackTitle}
                              <span class="text-foreground font-medium">{c.trackTitle}</span>
                              <span class="text-muted-foreground">—</span>
                            {/if}
                            <span>{describeChange(c)}</span>
                          </li>
                        {/each}
                      </ul>
                      {#if s.changes.length > shown.length}
                        <p class="text-muted-foreground mt-2 text-xs">
                          and {(s.changes.length - shown.length).toLocaleString()} more
                        </p>
                      {/if}

                      {#if artistHref(s) || albumHref(s)}
                        <div class="mt-3 flex flex-wrap gap-3 text-xs">
                          {#if albumHref(s)}
                            <a href={albumHref(s)} class="text-primary hover:underline"
                              >Open {s.album}</a
                            >
                          {/if}
                          {#if artistHref(s)}
                            <a href={artistHref(s)} class="text-primary hover:underline"
                              >All of {s.albumArtist}</a
                            >
                          {/if}
                        </div>
                      {/if}
                    </div>
                  {/if}
                </li>
              {/each}
            </ul>
          </section>
        {/each}

        {#if nextCursor != null}
          <div class="mt-4 flex justify-center">
            <Button onclick={loadMore} disabled={loadingMore} variant="outline" size="sm">
              {loadingMore ? 'Loading…' : 'Load older'}
            </Button>
          </div>
        {/if}
      {/if}
    </div>
  </ScrollArea>
</div>

<script lang="ts">
  import { CalendarClock, ChevronRight, Disc3, Image, Tags, Users } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Button } from '$lib/components/ui/button';
  import { fetchHistory, type HistorySummary } from '$lib/api-client';

  type RangeKey = '1' | '7' | '30' | 'custom';

  let range = $state<RangeKey>('7');
  let customFrom = $state<string>('');
  let customTo = $state<string>('');

  let summaries = $state<HistorySummary[]>([]);
  let nextCursor = $state<string | null>(null);
  let totalEvents = $state(0);
  let loading = $state(true);
  let loadingMore = $state(false);
  let error = $state<string | null>(null);
  let expanded = $state<Set<string>>(new Set());

  // The {from,to} ISO dateWindow for the current range selection. `null` for an incomplete custom range.
  const dateWindow = $derived.by((): { from?: string; to?: string } | null => {
    if (range === 'custom') {
      if (!customFrom || !customTo) return null;
      // Inclusive end-of-day for the `to` date so the whole day is covered.
      return { from: new Date(customFrom).toISOString(), to: new Date(`${customTo}T23:59:59.999`).toISOString() };
    }
    const to = new Date();
    const from = new Date();
    if (range === '1') from.setHours(0, 0, 0, 0);
    else from.setDate(from.getDate() - Number(range));
    return { from: from.toISOString(), to: to.toISOString() };
  });

  async function load() {
    const w = dateWindow;
    if (w === null) {
      summaries = [];
      loading = false;
      return;
    }
    loading = true;
    try {
      const res = await fetchHistory(w);
      summaries = res.summaries;
      nextCursor = res.nextCursor ?? null;
      totalEvents = res.totalEventsInWindow;
      expanded = new Set();
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load history';
    } finally {
      loading = false;
    }
  }

  async function loadMore() {
    const w = dateWindow;
    if (w === null || nextCursor == null) return;
    loadingMore = true;
    try {
      const res = await fetchHistory({ ...w, cursor: nextCursor });
      summaries = [...summaries, ...res.summaries];
      nextCursor = res.nextCursor ?? null;
    } catch {
      // keep what we have
    } finally {
      loadingMore = false;
    }
  }

  function toggle(id: string) {
    const next = new Set(expanded);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    expanded = next;
  }

  // Reload whenever the dateWindow changes (range buttons or custom dates).
  $effect(() => {
    void dateWindow;
    void load();
  });

  const ICONS = { consolidation: Disc3, 'artist-rename': Users, 'year-correction': CalendarClock, cover: Image, tags: Tags };
  function iconFor(kind: string) {
    return ICONS[kind as keyof typeof ICONS] ?? Tags;
  }

  function relTime(iso: string): string {
    const then = new Date(iso).getTime();
    const secs = Math.round((Date.now() - then) / 1000);
    if (secs < 60) return 'just now';
    const mins = Math.round(secs / 60);
    if (mins < 60) return `${mins} min ago`;
    const hours = Math.round(mins / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.round(hours / 24);
    if (days < 30) return `${days}d ago`;
    return new Date(iso).toLocaleDateString([], { month: 'short', day: 'numeric' });
  }

  const RANGES: { key: RangeKey; label: string }[] = [
    { key: '1', label: 'Today' },
    { key: '7', label: '7 days' },
    { key: '30', label: '30 days' },
    { key: 'custom', label: 'Custom' }
  ];
</script>

<div class="flex min-h-0 flex-1 flex-col">
  <header class="flex flex-wrap items-center justify-between gap-4 border-b border-border px-6 py-4">
    <div>
      <h1 class="text-lg font-semibold">Library history</h1>
      <p class="text-sm text-muted-foreground">
        Every change MusicHoarder wrote to your destination library — what Navidrome sees differently.
      </p>
    </div>
    <div class="flex flex-wrap items-center gap-2">
      <div class="flex items-center gap-1 rounded-md border border-border p-0.5">
        {#each RANGES as r (r.key)}
          <button
            type="button"
            onclick={() => (range = r.key)}
            class="rounded px-2.5 py-1 text-xs font-medium transition-colors {range === r.key
              ? 'bg-primary text-primary-foreground'
              : 'text-muted-foreground hover:text-foreground'}"
          >
            {r.label}
          </button>
        {/each}
      </div>
      {#if range === 'custom'}
        <input
          type="date"
          bind:value={customFrom}
          class="rounded-md border border-border bg-background px-2 py-1 text-xs"
        />
        <span class="text-muted-foreground text-xs">→</span>
        <input
          type="date"
          bind:value={customTo}
          class="rounded-md border border-border bg-background px-2 py-1 text-xs"
        />
      {/if}
    </div>
  </header>

  <ScrollArea class="min-h-0 flex-1">
    <div class="px-6 py-6">
      {#if error}
        <div class="rounded-md border border-red-500/40 bg-red-500/10 px-4 py-3 text-sm text-red-400">
          {error}
        </div>
      {:else if range === 'custom' && dateWindow === null}
        <p class="text-sm text-muted-foreground">Pick a start and end date.</p>
      {:else if loading}
        <p class="text-sm text-muted-foreground">Loading…</p>
      {:else if summaries.length === 0}
        <div class="rounded-lg border border-dashed border-border px-6 py-12 text-center">
          <p class="text-sm font-medium">No changes in this range</p>
          <p class="mx-auto mt-1 max-w-md text-sm text-muted-foreground">
            History records what gets written to the destination library when albums are built or
            re-tagged. Run a build or re-tag an album to see entries here.
          </p>
        </div>
      {:else}
        <p class="mb-3 text-xs text-muted-foreground">
          {summaries.length} change{summaries.length === 1 ? '' : 's'} · {totalEvents} field write{totalEvents === 1 ? '' : 's'}
        </p>
        <ul class="space-y-2">
          {#each summaries as s (s.id)}
            {@const Icon = iconFor(s.kind)}
            {@const isOpen = expanded.has(s.id)}
            <li class="rounded-lg border border-border bg-card">
              <button
                type="button"
                class="flex w-full items-center gap-3 px-4 py-3 text-left"
                onclick={() => toggle(s.id)}
              >
                <span class="grid size-8 shrink-0 place-items-center rounded-md bg-muted text-muted-foreground">
                  <Icon class="size-4" />
                </span>
                <div class="min-w-0 flex-1">
                  <div class="truncate text-sm font-medium">{s.headline}</div>
                  <div class="mt-0.5 truncate text-xs text-muted-foreground">
                    {#if s.albumArtist}{s.albumArtist}{#if s.album} — {/if}{/if}{s.album ?? ''}
                  </div>
                </div>
                <span class="shrink-0 font-mono text-[10px] text-muted-foreground">{relTime(s.latestWrittenAtUtc)}</span>
                <ChevronRight class="size-4 shrink-0 text-muted-foreground transition-transform {isOpen ? 'rotate-90' : ''}" />
              </button>

              {#if isOpen}
                <div class="border-t border-border px-4 py-3">
                  <ul class="space-y-1 font-mono text-xs">
                    {#each s.changes as c, ci (ci)}
                      <li class="flex flex-wrap items-baseline gap-x-2">
                        {#if c.songId != null}
                          <a href={`/track/${c.songId}`} class="text-foreground hover:underline">
                            {c.trackTitle ?? `#${c.songId}`}
                          </a>
                        {/if}
                        <span class="text-muted-foreground">{c.field}:</span>
                        <span class="text-red-400">{c.oldValue ?? '∅'}</span>
                        <span class="text-muted-foreground">→</span>
                        <span class="text-emerald-400">{c.newValue ?? '∅'}</span>
                      </li>
                    {/each}
                  </ul>
                </div>
              {/if}
            </li>
          {/each}
        </ul>

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

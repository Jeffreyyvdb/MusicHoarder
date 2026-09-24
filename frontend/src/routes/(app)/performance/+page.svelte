<script lang="ts">
  import {
    Camera,
    ChevronRight,
    ChevronsUpDown,
    Loader2,
    Minus,
    TrendingDown,
    TrendingUp,
    TriangleAlert
  } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Button } from '$lib/components/ui/button';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import Sparkline from '$lib/components/performance/Sparkline.svelte';
  import { describeSeriesDelta } from '$lib/components/performance/delta';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import {
    fetchSnapshots,
    fetchSnapshot,
    fetchSnapshotCompare,
    captureSnapshot,
    type SnapshotSummary,
    type SnapshotDetail,
    type SnapshotCompare
  } from '$lib/api-client';
  import { cn } from '$lib/utils';

  let snapshots = $state<SnapshotSummary[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let capturing = $state(false);

  let detailId = $state<number | null>(null);
  let detail = $state<SnapshotDetail | null>(null);
  let detailError = $state(false);

  let fromId = $state<number | null>(null);
  let toId = $state<number | null>(null);
  let compare = $state<SnapshotCompare | null>(null);

  async function load() {
    loading = true;
    try {
      snapshots = await fetchSnapshots();
      error = null;
      if (snapshots.length >= 2) {
        // Default compare = previous → latest.
        toId = snapshots[snapshots.length - 1].id;
        fromId = snapshots[snapshots.length - 2].id;
        await loadCompare();
      }
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load snapshots';
    } finally {
      loading = false;
    }
  }

  async function loadCompare() {
    if (fromId == null || toId == null || fromId === toId) {
      compare = null;
      return;
    }
    try {
      compare = await fetchSnapshotCompare(fromId, toId);
    } catch {
      compare = null;
    }
  }

  async function openDetail(id: number) {
    // Clear the previous row's diff first, or the newly opened row shows it until the fetch
    // returns; and only the latest request may land, so a slow earlier one cannot overwrite it.
    detail = null;
    detailError = false;
    detailId = detailId === id ? null : id;
    if (detailId == null) return;
    try {
      const result = await fetchSnapshot(id);
      if (detailId === id) detail = result;
    } catch {
      if (detailId === id) detailError = true;
    }
  }

  async function capture() {
    capturing = true;
    try {
      await captureSnapshot();
      await load();
    } finally {
      capturing = false;
    }
  }

  $effect(() => {
    void load();
  });

  // --- chart series (oldest → newest, matching the snapshots order) ---
  const labels = $derived(snapshots.map((s) => fmtShort(s.capturedAtUtc)));
  const matchRateSeries = $derived(snapshots.map((s) => (s.matchRate != null ? s.matchRate * 100 : null)));
  const needsReviewSeries = $derived(snapshots.map((s) => s.needsReview));
  const failedSeries = $derived(snapshots.map((s) => s.failed));
  const avgAiSeries = $derived(snapshots.map((s) => s.avgAiScore ?? null));
  const confidenceSeries = $derived(
    snapshots.map((s) => (s.avgMatchConfidence != null ? s.avgMatchConfidence * 100 : null))
  );

  function fmtShort(iso: string): string {
    const d = new Date(iso);
    return d.toLocaleString([], { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
  }
  function pct(v: number): string {
    return `${Math.round(v)}%`;
  }
  function count(v: number): string {
    return String(Math.round(v));
  }
  function score(v: number): string {
    return Math.round(v).toString();
  }

  // `higherIsBetter` is each chart's polarity: a rise in Match rate is good news, a rise in Failed
  // is bad news, and the delta line says which in words as well as colour.
  const charts = $derived([
    { title: 'Match rate', series: matchRateSeries, color: 'var(--color-primary)', format: pct, yMin: 0, yMax: 100, higherIsBetter: true },
    { title: 'Avg AI score', series: avgAiSeries, color: 'var(--chart-3)', format: score, yMin: 0, yMax: 100, higherIsBetter: true },
    // Not --chart-4: that amber is 3.22:1 on the light page, under the 3:1-plus-margin a thin
    // 0.8-unit line needs. --chart-5 is 5.07:1 light and unused by the other four series.
    { title: 'Needs review', series: needsReviewSeries, color: 'var(--chart-5)', format: count, yMin: 0, higherIsBetter: false },
    { title: 'Failed', series: failedSeries, color: 'var(--color-destructive)', format: count, yMin: 0, higherIsBetter: false },
    { title: 'Avg match confidence', series: confidenceSeries, color: 'var(--chart-2)', format: pct, yMin: 0, yMax: 100, higherIsBetter: true }
  ]);

  /** "EnrichmentRun" → "Enrichment run": the capture trigger as a sentence-case word. */
  function triggerWord(s: SnapshotSummary): string {
    if (s.triggerLabel) return s.triggerLabel;
    const spaced = s.trigger.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
    return spaced.charAt(0).toUpperCase() + spaced.slice(1);
  }

  function snapshotLabel(id: number | null): string {
    const s = snapshots.find((x) => x.id === id);
    return s ? `${fmtShort(s.capturedAtUtc)} · ${s.version ?? 'dev'}` : 'Choose';
  }

  // A pop-up button in a list row, iOS-style: the row shows the choice, and a transparent native
  // <select> laid over the whole row opens the system picker (the wheel on an iPhone). 16px, so
  // focusing it never zooms the page.
  const OVERLAY_SELECT =
    'absolute inset-0 h-full w-full cursor-pointer appearance-none opacity-0 text-base';
</script>

{#snippet versionPicker(label: string, aria: string, value: number | null, onpick: (id: number) => void)}
  <GroupedList.Row
    {label}
    class="hover:bg-accent has-[select:focus-visible]:ring-ring/50 has-[select:focus-visible]:ring-3 has-[select:focus-visible]:ring-inset"
  >
    {#snippet trailing()}
      <span class="text-body text-muted-foreground flex min-w-0 items-center gap-1 md:text-sm">
        <span class="truncate tabular-nums">{snapshotLabel(value)}</span>
        <ChevronsUpDown class="size-4 shrink-0" aria-hidden="true" />
      </span>
      <select
        class={OVERLAY_SELECT}
        aria-label={aria}
        value={value ?? undefined}
        onchange={(e) => onpick(Number(e.currentTarget.value))}
      >
        {#each snapshots as s (s.id)}
          <option value={s.id}
            >{fmtShort(s.capturedAtUtc)} · {s.version ?? 'dev'} · {s.configHash.slice(0, 8)}</option
          >
        {/each}
      </select>
    {/snippet}
  </GroupedList.Row>
{/snippet}

{#snippet songList(title: string, rows: SnapshotCompare['regressed'], total: number, worse: boolean, footer?: string)}
  <GroupedList.Section headingLevel={2} {footer} class="min-w-0">
    {#snippet header()}
      <span class="inline-flex items-center gap-1.5">
        {#if worse}
          <TrendingDown class="text-destructive-text size-3.5" aria-hidden="true" />
        {:else}
          <TrendingUp class="text-primary size-3.5" aria-hidden="true" />
        {/if}
        {title} · <span class="tabular-nums">{total.toLocaleString()}</span>
      </span>
    {/snippet}
    {#if rows.length === 0}
      <p class="text-body text-muted-foreground px-4 py-5 text-center md:text-sm">None</p>
    {:else}
      {#each rows as r (r.songId)}
        <GroupedList.Row
          href={`/track/${r.songId}`}
          label={`${r.artist ?? '—'} — ${r.title ?? r.fileName}`}
          sublabel={r.reasons.join(' · ') || undefined}
          chevron
        />
      {/each}
    {/if}
  </GroupedList.Section>
{/snippet}

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1">
    <PageToolbarV2
      title="Performance"
      meta={loading
        ? undefined
        : `${snapshots.length.toLocaleString()} version${snapshots.length === 1 ? '' : 's'} captured`}
      grouped
    >
      {#snippet actions()}
        <Button variant="gray" class="rounded-full" onclick={capture} disabled={capturing}>
          {#if capturing}
            <Loader2 class="animate-spin" aria-hidden="true" />
          {:else}
            <Camera aria-hidden="true" />
          {/if}
          <!-- The phone bar shows the glyph alone; the words stay its accessible name. -->
          <span class="max-md:sr-only">{capturing ? 'Capturing…' : 'Capture now'}</span>
        </Button>
      {/snippet}
    </PageToolbarV2>

    <div class="mx-auto flex w-full max-w-6xl flex-col gap-7 pt-2 pb-8 md:gap-6 md:px-7 md:pt-6">
      {#if error}
        <GroupedList.Section>
          <GroupedList.Row
            icon={TriangleAlert}
            iconClass="bg-destructive/12 text-destructive-text"
            label="Couldn't load snapshots"
            sublabel={error}
          />
        </GroupedList.Section>
      {/if}

      {#if loading}
        <div class="mx-4 grid grid-cols-1 gap-3 sm:grid-cols-2 md:mx-0 xl:grid-cols-3">
          {#each Array(5) as _, i (i)}
            <div class="bg-card rounded-xl p-4">
              <Skeleton class="h-4 w-24" />
              <Skeleton class="mt-2 h-7 w-16" />
              <Skeleton class="mt-3 h-14 w-full" />
            </div>
          {/each}
        </div>
      {:else if snapshots.length === 0 && !error}
        <div class="mx-auto flex max-w-md flex-col items-center px-6 py-14 text-center">
          <p class="text-headline md:text-sm md:font-medium">No snapshots yet</p>
          <p class="text-callout text-muted-foreground mt-1 md:text-sm">
            Run an enrichment or AI grading pass — a performance snapshot is captured automatically when
            it finishes. You can also capture one now to set a baseline.
          </p>
          <Button onclick={capture} disabled={capturing} size="pill" class="mt-5 md:h-9 md:text-sm">
            <Camera aria-hidden="true" />
            Capture baseline
          </Button>
        </div>
      {:else if snapshots.length > 0}
        <!-- Timeline: one card per series, titled with its latest value and a verdict on the
             change since the previous capture. -->
        <section aria-labelledby="perf-timeline">
          <h2 id="perf-timeline" class="text-footnote text-muted-foreground px-8 pb-1.5 md:px-4">
            Timeline · {snapshots.length} version{snapshots.length === 1 ? '' : 's'}
          </h2>
          <div class="mx-4 grid grid-cols-1 gap-3 sm:grid-cols-2 md:mx-0 xl:grid-cols-3">
            {#each charts as c (c.title)}
              {@const latest = c.series[c.series.length - 1]}
              {@const d = describeSeriesDelta(c.series, { format: c.format, higherIsBetter: c.higherIsBetter })}
              <div class="bg-card rounded-xl p-4">
                <div class="text-subheadline text-muted-foreground font-medium md:text-sm">{c.title}</div>
                <div class="text-title-2 mt-0.5 tabular-nums md:text-2xl md:font-semibold">
                  {latest != null ? c.format(latest) : '—'}
                </div>
                <div
                  class={cn(
                    'text-footnote mt-0.5 mb-2 flex items-center gap-1 md:text-xs',
                    d.tone === 'worse' ? 'text-destructive-text' : 'text-muted-foreground'
                  )}
                >
                  {#if d.direction === 'up'}
                    <TrendingUp class={cn('size-3.5', d.tone === 'better' && 'text-primary')} aria-hidden="true" />
                  {:else if d.direction === 'down'}
                    <TrendingDown class={cn('size-3.5', d.tone === 'better' && 'text-primary')} aria-hidden="true" />
                  {:else}
                    <Minus class="size-3.5" aria-hidden="true" />
                  {/if}
                  <span>{d.text}</span>
                </div>
                <Sparkline
                  name={c.title}
                  values={c.series}
                  {labels}
                  color={c.color}
                  format={c.format}
                  yMin={c.yMin}
                  yMax={c.yMax}
                />
              </div>
            {/each}
          </div>
        </section>

        <!-- Compare two versions -->
        {#if snapshots.length >= 2}
          <GroupedList.Section headingLevel={2} header="Compare versions">
            {@render versionPicker('From', 'Compare from version', fromId, (id) => {
              fromId = id;
              void loadCompare();
            })}
            {@render versionPicker('To', 'Compare to version', toId, (id) => {
              toId = id;
              void loadCompare();
            })}
          </GroupedList.Section>

          {#if compare}
            <div class="grid grid-cols-1 items-start gap-7 lg:grid-cols-2 lg:gap-6">
              {@render songList('Regressed', compare.regressed, compare.regressedCount, true)}
              {@render songList(
                'Improved',
                compare.improved,
                compare.improvedCount,
                false,
                `Compared ${compare.comparedSongs.toLocaleString()} songs present in both versions.`
              )}
            </div>
          {/if}
        {/if}

        <!-- Version log: newest first; a row opens its config diff. -->
        <GroupedList.Section headingLevel={2} header="Version log">
          {#each [...snapshots].reverse() as s (s.id)}
            {@const isOpen = detailId === s.id}
            <GroupedList.Row
              onclick={() => openDetail(s.id)}
              aria-expanded={isOpen}
              label={fmtShort(s.capturedAtUtc)}
              sublabel={`${triggerWord(s)} · ${s.version ?? 'dev'}`}
            >
              <span class="text-footnote mt-0.5 flex flex-wrap gap-x-3 tabular-nums md:text-xs">
                <span class="text-foreground">{s.matched} matched</span>
                <span class={s.needsReview > 0 ? 'text-warning-text' : 'text-muted-foreground'}
                  >{s.needsReview} review</span
                >
                <span class={s.failed > 0 ? 'text-destructive-text' : 'text-muted-foreground'}
                  >{s.failed} failed</span
                >
                {#if s.avgAiScore != null}<span class="text-muted-foreground">AI {Math.round(s.avgAiScore)}</span>{/if}
              </span>
              {#snippet trailing()}
                <span class="flex items-center gap-1.5">
                  <span class="text-footnote text-muted-foreground font-mono md:text-[11px]" title="Config hash"
                    >{s.configHash.slice(0, 8)}</span
                  >
                  <ChevronRight
                    aria-hidden="true"
                    strokeWidth={2.5}
                    class={cn(
                      'text-muted-foreground-dim -mr-1 size-4 transition-transform duration-200 ease-[cubic-bezier(0.23,1,0.32,1)]',
                      isOpen && 'rotate-90'
                    )}
                  />
                </span>
              {/snippet}
            </GroupedList.Row>

            {#if isOpen}
              <div
                class="after:bg-separator relative px-4 pb-3 after:absolute after:right-0 after:bottom-0 after:left-4 after:h-(--hairline) last:after:hidden"
              >
                {#if !detail}
                  <p class="text-footnote text-muted-foreground flex items-center gap-1.5 md:text-xs">
                    {#if detailError}
                      Couldn't load this version's config.
                    {:else}
                      <Loader2 class="size-3.5 animate-spin" aria-hidden="true" /> Loading…
                    {/if}
                  </p>
                {:else if detail.configDiff.length === 0}
                  <p class="text-footnote text-muted-foreground md:text-xs">
                    {detail.previousSnapshotId == null
                      ? 'First snapshot — no previous version to diff.'
                      : 'No pipeline config changes vs the previous snapshot.'}
                  </p>
                {:else}
                  <p class="text-footnote text-muted-foreground mb-1 font-medium md:text-xs">
                    Config changes vs previous
                  </p>
                  <ul class="text-footnote space-y-1 font-mono break-all md:text-xs">
                    {#each detail.configDiff as d (d.key)}
                      <li>
                        <span class="text-muted-foreground">{d.key}:</span>
                        <span class="text-destructive-text line-through decoration-1">{d.from ?? '∅'}</span>
                        <span class="text-muted-foreground" aria-label="changed to">→</span>
                        <span class="text-foreground font-semibold">{d.to ?? '∅'}</span>
                      </li>
                    {/each}
                  </ul>
                {/if}
              </div>
            {/if}
          {/each}
        </GroupedList.Section>
      {/if}
    </div>
  </ScrollArea>
</div>

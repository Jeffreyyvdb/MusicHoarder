<script lang="ts">
  import { TrendingUp, TrendingDown, Camera, Minus, GitCompareArrows } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Button } from '$lib/components/ui/button';
  import Sparkline from '$lib/components/performance/Sparkline.svelte';
  import {
    fetchSnapshots,
    fetchSnapshot,
    fetchSnapshotCompare,
    captureSnapshot,
    type SnapshotSummary,
    type SnapshotDetail,
    type SnapshotCompare
  } from '$lib/api-client';

  let snapshots = $state<SnapshotSummary[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let capturing = $state(false);

  let detailId = $state<number | null>(null);
  let detail = $state<SnapshotDetail | null>(null);

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
    detailId = detailId === id ? null : id;
    if (detailId == null) {
      detail = null;
      return;
    }
    try {
      detail = await fetchSnapshot(id);
    } catch {
      detail = null;
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

  const charts = $derived([
    { title: 'Match rate', series: matchRateSeries, color: '#10b981', format: pct, yMin: 0, yMax: 100 },
    { title: 'Avg AI score', series: avgAiSeries, color: '#6366f1', format: score, yMin: 0, yMax: 100 },
    { title: 'Needs review', series: needsReviewSeries, color: '#f59e0b', format: count, yMin: 0 },
    { title: 'Failed', series: failedSeries, color: '#ef4444', format: count, yMin: 0 },
    { title: 'Avg match confidence', series: confidenceSeries, color: '#0ea5e9', format: pct, yMin: 0, yMax: 100 }
  ]);

  function statusClass(status: string): string {
    switch (status) {
      case 'Matched':
        return 'text-emerald-500';
      case 'NeedsReview':
        return 'text-amber-500';
      case 'Failed':
        return 'text-red-500';
      default:
        return 'text-muted-foreground';
    }
  }
</script>

<div class="flex h-full min-h-0 flex-col">
  <header class="flex items-center justify-between gap-4 border-b border-border px-6 py-4">
    <div>
      <h1 class="text-lg font-semibold">Pipeline performance</h1>
      <p class="text-sm text-muted-foreground">
        Quality of every enrichment version over time. A snapshot is captured automatically after each
        run; compare two to see which songs regressed.
      </p>
    </div>
    <Button onclick={capture} disabled={capturing} variant="outline" size="sm">
      <Camera class="mr-2 size-4" />
      {capturing ? 'Capturing…' : 'Capture now'}
    </Button>
  </header>

  <ScrollArea class="min-h-0 flex-1">
    <div class="space-y-8 px-6 py-6">
      {#if error}
        <div class="rounded-md border border-red-500/40 bg-red-500/10 px-4 py-3 text-sm text-red-400">
          {error}
        </div>
      {/if}

      {#if loading}
        <p class="text-sm text-muted-foreground">Loading…</p>
      {:else if snapshots.length === 0}
        <div class="rounded-lg border border-dashed border-border px-6 py-12 text-center">
          <p class="text-sm font-medium">No snapshots yet</p>
          <p class="mx-auto mt-1 max-w-md text-sm text-muted-foreground">
            Run an enrichment or AI grading pass — a performance snapshot is captured automatically when
            it finishes. You can also capture one now to set a baseline.
          </p>
          <Button onclick={capture} disabled={capturing} size="sm" class="mt-4">
            <Camera class="mr-2 size-4" />
            Capture baseline
          </Button>
        </div>
      {:else}
        <!-- Timeline charts -->
        <section>
          <h2 class="mb-3 text-sm font-semibold text-muted-foreground">
            Timeline · {snapshots.length} version{snapshots.length === 1 ? '' : 's'}
          </h2>
          <div class="grid grid-cols-1 gap-4 sm:grid-cols-2 xl:grid-cols-3">
            {#each charts as c (c.title)}
              {@const latest = c.series[c.series.length - 1]}
              {@const prev = c.series.length >= 2 ? c.series[c.series.length - 2] : null}
              {@const delta = latest != null && prev != null ? latest - prev : null}
              <div class="rounded-lg border border-border bg-card p-4">
                <div class="flex items-baseline justify-between">
                  <span class="text-sm font-medium">{c.title}</span>
                  <span class="text-lg font-semibold tabular-nums">
                    {latest != null ? c.format(latest) : '—'}
                  </span>
                </div>
                {#if delta != null && Math.abs(delta) > 0.01}
                  <div
                    class="mb-1 flex items-center gap-1 text-xs {delta > 0
                      ? 'text-emerald-500'
                      : 'text-red-500'}"
                  >
                    {#if delta > 0}<TrendingUp class="size-3" />{:else}<TrendingDown class="size-3" />{/if}
                    {c.format(Math.abs(delta))} vs previous
                  </div>
                {:else}
                  <div class="mb-1 flex items-center gap-1 text-xs text-muted-foreground">
                    <Minus class="size-3" /> no change
                  </div>
                {/if}
                <Sparkline
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
          <section>
            <h2 class="mb-3 flex items-center gap-2 text-sm font-semibold text-muted-foreground">
              <GitCompareArrows class="size-4" /> Compare versions
            </h2>
            <div class="flex flex-wrap items-center gap-2 text-sm">
              <select
                class="rounded-md border border-border bg-background px-2 py-1"
                bind:value={fromId}
                onchange={loadCompare}
              >
                {#each snapshots as s (s.id)}
                  <option value={s.id}>{fmtShort(s.capturedAtUtc)} · {s.version ?? 'dev'}</option>
                {/each}
              </select>
              <span class="text-muted-foreground">→</span>
              <select
                class="rounded-md border border-border bg-background px-2 py-1"
                bind:value={toId}
                onchange={loadCompare}
              >
                {#each snapshots as s (s.id)}
                  <option value={s.id}>{fmtShort(s.capturedAtUtc)} · {s.version ?? 'dev'}</option>
                {/each}
              </select>
            </div>

            {#if compare}
              <div class="mt-3 grid grid-cols-1 gap-4 lg:grid-cols-2">
                <!-- Regressed -->
                <div class="rounded-lg border border-border bg-card">
                  <div class="flex items-center gap-2 border-b border-border px-4 py-2">
                    <TrendingDown class="size-4 text-red-500" />
                    <span class="text-sm font-medium">Regressed</span>
                    <span class="ml-auto text-sm tabular-nums text-muted-foreground"
                      >{compare.regressedCount}</span
                    >
                  </div>
                  {#if compare.regressed.length === 0}
                    <p class="px-4 py-6 text-center text-sm text-muted-foreground">None 🎉</p>
                  {:else}
                    <ul class="divide-y divide-border">
                      {#each compare.regressed as r (r.songId)}
                        <li class="px-4 py-2 text-sm">
                          <a href={`/review?song=${r.songId}`} class="font-medium hover:underline">
                            {r.artist ?? '—'} — {r.title ?? r.fileName}
                          </a>
                          <div class="mt-0.5 flex flex-wrap gap-x-3 text-xs text-muted-foreground">
                            {#each r.reasons as reason, ri (ri)}<span>{reason}</span>{/each}
                          </div>
                        </li>
                      {/each}
                    </ul>
                  {/if}
                </div>

                <!-- Improved -->
                <div class="rounded-lg border border-border bg-card">
                  <div class="flex items-center gap-2 border-b border-border px-4 py-2">
                    <TrendingUp class="size-4 text-emerald-500" />
                    <span class="text-sm font-medium">Improved</span>
                    <span class="ml-auto text-sm tabular-nums text-muted-foreground"
                      >{compare.improvedCount}</span
                    >
                  </div>
                  {#if compare.improved.length === 0}
                    <p class="px-4 py-6 text-center text-sm text-muted-foreground">None</p>
                  {:else}
                    <ul class="divide-y divide-border">
                      {#each compare.improved as r (r.songId)}
                        <li class="px-4 py-2 text-sm">
                          <a href={`/review?song=${r.songId}`} class="font-medium hover:underline">
                            {r.artist ?? '—'} — {r.title ?? r.fileName}
                          </a>
                          <div class="mt-0.5 flex flex-wrap gap-x-3 text-xs text-muted-foreground">
                            {#each r.reasons as reason, ri (ri)}<span>{reason}</span>{/each}
                          </div>
                        </li>
                      {/each}
                    </ul>
                  {/if}
                </div>
              </div>
              <p class="mt-2 text-xs text-muted-foreground">
                Compared {compare.comparedSongs} songs present in both versions.
              </p>
            {/if}
          </section>
        {/if}

        <!-- Version log -->
        <section>
          <h2 class="mb-3 text-sm font-semibold text-muted-foreground">Version log</h2>
          <ul class="space-y-2">
            {#each [...snapshots].reverse() as s (s.id)}
              <li class="rounded-lg border border-border bg-card">
                <button
                  type="button"
                  class="flex w-full items-center gap-3 px-4 py-3 text-left"
                  onclick={() => openDetail(s.id)}
                >
                  <div class="min-w-0 flex-1">
                    <div class="flex items-center gap-2">
                      <span class="text-sm font-medium">{fmtShort(s.capturedAtUtc)}</span>
                      <span
                        class="rounded bg-muted px-1.5 py-0.5 text-xs text-muted-foreground"
                        title="trigger">{s.trigger}</span
                      >
                      <span class="font-mono text-xs text-muted-foreground">{s.version ?? 'dev'}</span>
                    </div>
                    <div class="mt-0.5 flex flex-wrap gap-x-4 text-xs text-muted-foreground tabular-nums">
                      <span class={statusClass('Matched')}>{s.matched} matched</span>
                      <span class={statusClass('NeedsReview')}>{s.needsReview} review</span>
                      <span class={statusClass('Failed')}>{s.failed} failed</span>
                      {#if s.avgAiScore != null}<span>AI {Math.round(s.avgAiScore)}</span>{/if}
                    </div>
                  </div>
                  <span class="font-mono text-[10px] text-muted-foreground">{s.configHash.slice(0, 8)}</span>
                </button>

                {#if detailId === s.id && detail}
                  <div class="border-t border-border px-4 py-3">
                    {#if detail.configDiff.length === 0}
                      <p class="text-xs text-muted-foreground">
                        {detail.previousSnapshotId == null
                          ? 'First snapshot — no previous version to diff.'
                          : 'No pipeline config changes vs the previous snapshot.'}
                      </p>
                    {:else}
                      <p class="mb-1 text-xs font-medium text-muted-foreground">
                        Config changes vs previous:
                      </p>
                      <ul class="space-y-0.5 font-mono text-xs">
                        {#each detail.configDiff as d (d.key)}
                          <li>
                            <span class="text-muted-foreground">{d.key}:</span>
                            <span class="text-red-400">{d.from ?? '∅'}</span>
                            <span class="text-muted-foreground">→</span>
                            <span class="text-emerald-400">{d.to ?? '∅'}</span>
                          </li>
                        {/each}
                      </ul>
                    {/if}
                  </div>
                {/if}
              </li>
            {/each}
          </ul>
        </section>
      {/if}
    </div>
  </ScrollArea>
</div>

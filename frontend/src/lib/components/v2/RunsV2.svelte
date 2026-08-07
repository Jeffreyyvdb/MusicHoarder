<script lang="ts">
  import { Check, ChevronLeft, Radio, X } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Button } from '$lib/components/ui/button';
  import {
    fetchRun,
    fetchRuns,
    type ApiRun,
    type ApiRunDetail,
    type ApiRunStatus
  } from '$lib/api-client';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { cn } from '$lib/utils';

  // The ledger the backend has been writing since #137 deleted the v1 page that read it. The
  // list is capped at 50 by the API with no paging, so older runs genuinely cannot be reached —
  // the footer says so rather than implying this is everything.
  let runs = $state<ApiRun[]>([]);
  let activeId = $state<string | null>(null);
  let detail = $state<ApiRunDetail | null>(null);
  let loaded = $state(false);
  // Below `lg` the two panes become a drill-down: the list shows until a run is opened, then the
  // detail replaces it. At `lg`+ both are always visible and this is ignored.
  let mobileDetailOpen = $state(false);

  async function loadRuns(): Promise<void> {
    try {
      const next = await fetchRuns();
      runs = next;
      if (activeId === null && next.length > 0) activeId = next[0].id;
    } catch {
      // Keep the last good list rather than blanking the page on one bad poll.
    } finally {
      loaded = true;
    }
  }

  $effect(() => {
    void loadRuns();
    const poll = setInterval(() => void loadRuns(), 5_000);
    return () => clearInterval(poll);
  });

  // Reload the detail whenever the selection changes, on the same cadence, so an in-flight run's
  // counters keep ticking while you watch it.
  $effect(() => {
    const id = activeId;
    if (id === null) {
      detail = null;
      return;
    }
    void (async () => {
      detail = await fetchRun(id);
    })();
    const poll = setInterval(async () => {
      detail = await fetchRun(id);
    }, 5_000);
    return () => clearInterval(poll);
  });

  const runningCount = $derived(runs.filter((r) => r.status === 'running').length);

  function fmtDuration(seconds: number | null | undefined): string {
    if (seconds == null) return '—';
    const s = Math.max(0, Math.round(seconds));
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    return [h, m, s % 60].map((n) => n.toString().padStart(2, '0')).join(':');
  }

  function fmtWhen(iso: string): string {
    const d = new Date(iso);
    const now = new Date();
    const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    if (d.toDateString() === now.toDateString()) return `Today · ${time}`;
    if (new Date(now.getTime() - 86_400_000).toDateString() === d.toDateString())
      return `Yesterday · ${time}`;
    return `${d.toLocaleDateString([], { month: 'short', day: 'numeric' })} · ${time}`;
  }

  /** A running run has no end time yet, so measure against now to keep the clock moving. */
  function liveDuration(run: ApiRun): number | null {
    if (run.durationSeconds != null) return run.durationSeconds;
    if (run.status === 'running') return (Date.now() - new Date(run.startedAtUtc).getTime()) / 1000;
    return null;
  }

  const STATUS_LABEL: Record<ApiRunStatus, string> = {
    running: 'Running',
    completed: 'Completed',
    cancelled: 'Cancelled',
    failed: 'Failed',
    // Written when the process died mid-run — a restart, not a failure the pipeline chose.
    interrupted: 'Interrupted'
  };

  const stageDefs = [
    { key: 'scan', label: 'Scan' },
    { key: 'fingerprint', label: 'Fingerprint' },
    { key: 'enrich', label: 'Match' },
    { key: 'build', label: 'Build' }
  ] as const;

  function stageValue(run: ApiRun, key: (typeof stageDefs)[number]['key']): number {
    switch (key) {
      case 'scan':
        return run.tracksProcessed;
      case 'fingerprint':
        return run.tracksFingerprinted;
      case 'enrich':
        return run.tracksEnriched;
      case 'build':
        return run.tracksCopied;
    }
  }
</script>

<div class="flex min-h-0 flex-1 flex-col overflow-hidden">
  <div class="border-border flex items-end justify-between border-b px-4 py-4 sm:px-7 sm:py-5">
    <div>
      <h1 class="text-2xl font-semibold tracking-tight">Ingest history</h1>
      <p class="text-muted-foreground mt-1 text-xs">
        {#if !loaded}
          Loading…
        {:else}
          {runs.length.toLocaleString()}
          {runs.length === 1 ? 'run' : 'runs'}{runningCount > 0 ? ` · ${runningCount} running` : ''}
        {/if}
      </p>
    </div>
  </div>

  <div class="grid min-h-0 flex-1 lg:grid-cols-[1fr_380px]">
    <!-- List -->
    <ScrollArea class={cn('min-h-0', mobileDetailOpen && 'hidden lg:block')}>
      <div class="px-3.5 pt-3.5 pb-8">
        <div
          class="text-muted-foreground border-border grid grid-cols-[24px_1fr_auto] items-center gap-3 border-b px-3 py-2 text-[10px] font-semibold tracking-wide uppercase md:grid-cols-[24px_1.4fr_1.4fr_70px_80px_60px_60px_80px]"
        >
          <span></span>
          <span>Run</span>
          <span class="hidden md:block">Source</span>
          <span class="hidden md:block">Files</span>
          <span class="hidden md:block">Written</span>
          <span class="hidden md:block">Errors</span>
          <span class="hidden md:block">Review</span>
          <span>Duration</span>
        </div>

        {#if loaded && runs.length === 0}
          <p class="text-muted-foreground py-12 text-center text-sm">
            No ingest runs recorded yet. One is written each time the pipeline processes the source
            library.
          </p>
        {/if}

        {#each runs as r (r.id)}
          <button
            type="button"
            class={cn(
              'grid w-full grid-cols-[24px_1fr_auto] items-center gap-3 rounded-md px-3 py-3 text-left text-xs transition-colors md:grid-cols-[24px_1.4fr_1.4fr_70px_80px_60px_60px_80px]',
              'hover:bg-muted/60 focus-visible:ring-ring/60 outline-none focus-visible:ring-2',
              activeId === r.id && 'bg-primary/10'
            )}
            onclick={() => {
              activeId = r.id;
              mobileDetailOpen = true;
            }}
          >
            <span class="flex items-center justify-center" title={STATUS_LABEL[r.status]}>
              {#if r.status === 'running'}
                <span class="bg-primary mh-v2-pulse size-3.5 rounded-full"></span>
              {:else if r.status === 'completed'}
                <span class="bg-primary text-primary-foreground grid size-3.5 place-items-center rounded-full">
                  <Check class="size-2.5" strokeWidth={3} />
                </span>
              {:else if r.status === 'failed'}
                <span class="bg-destructive text-destructive-foreground grid size-3.5 place-items-center rounded-full">
                  <X class="size-2.5" strokeWidth={3} />
                </span>
              {:else}
                <!-- Cancelled / interrupted: stopped, but not something that went wrong. -->
                <span class="border-muted-foreground/50 size-3.5 rounded-full border-2"></span>
              {/if}
            </span>
            <span class="min-w-0">
              {#if r.triggerLabel}
                <span class="block truncate text-[12.5px] font-medium">{r.triggerLabel}</span>
                <span class="text-muted-foreground block truncate text-[10.5px]">
                  {fmtWhen(r.startedAtUtc)}
                </span>
              {:else}
                <span class="block truncate text-[12.5px] font-medium">{fmtWhen(r.startedAtUtc)}</span>
                <span class="text-muted-foreground block truncate font-mono text-[10.5px]">{r.id}</span>
              {/if}
            </span>
            <span class="text-muted-foreground hidden truncate font-mono text-[11px] md:block">
              {r.sourcePath}
            </span>
            <span class="hidden font-mono md:block">{r.tracksDiscovered.toLocaleString()}</span>
            <span class="hidden font-mono md:block">{r.tracksCopied.toLocaleString()}</span>
            <span class={cn('hidden font-mono md:block', r.tracksFailed > 0 && 'text-destructive')}>
              {r.tracksFailed}
            </span>
            <span class="hidden font-mono md:block">{r.tracksReview}</span>
            <span class="font-mono">{fmtDuration(liveDuration(r))}</span>
          </button>
        {/each}

        {#if runs.length >= 50}
          <p class="text-muted-foreground/80 px-3 pt-4 text-[11px]">
            Showing the 50 most recent runs — the API doesn't page further back, so older runs
            aren't reachable from here.
          </p>
        {/if}
      </div>
    </ScrollArea>

    <!-- Detail -->
    <aside
      class={cn(
        'border-border flex min-h-0 flex-col lg:border-l',
        !mobileDetailOpen && 'hidden lg:flex'
      )}
    >
      {#if detail}
        {@const run = detail}
        <div class="border-border flex items-center gap-2 border-b px-4 py-3 lg:hidden">
          <Button variant="ghost" size="sm" class="gap-1.5" onclick={() => (mobileDetailOpen = false)}>
            <ChevronLeft class="size-4" />
            All runs
          </Button>
        </div>
        <ScrollArea class="min-h-0 flex-1">
          <div class="flex flex-col gap-4 px-4 py-4">
            <div>
              <div class="text-muted-foreground text-[11px]">{STATUS_LABEL[run.status]}</div>
              <h2 class="mt-0.5 text-sm font-semibold">
                {run.triggerLabel ?? fmtWhen(run.startedAtUtc)}
              </h2>
              <div class="text-muted-foreground mt-1 font-mono text-[10.5px] break-all">{run.id}</div>
            </div>

            <dl class="grid grid-cols-2 gap-x-4 gap-y-2 text-[11.5px]">
              <div>
                <dt class="text-muted-foreground">Started</dt>
                <dd class="font-medium">{fmtWhen(run.startedAtUtc)}</dd>
              </div>
              <div>
                <dt class="text-muted-foreground">Duration</dt>
                <dd class="font-mono font-medium">{fmtDuration(liveDuration(run))}</dd>
              </div>
              <div>
                <dt class="text-muted-foreground">Throughput</dt>
                <dd class="font-mono font-medium">
                  {run.throughputPerSec > 0 ? `${run.throughputPerSec.toFixed(2)}/s` : '—'}
                </dd>
              </div>
              <div>
                <dt class="text-muted-foreground">Needs review</dt>
                <dd class="font-mono font-medium">{run.tracksReview.toLocaleString()}</dd>
              </div>
            </dl>

            <div class="flex flex-col gap-2">
              <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">
                Per stage
              </div>
              {#each stageDefs as s (s.key)}
                {@const val = stageValue(run, s.key)}
                {@const pct =
                  run.tracksDiscovered > 0 ? Math.min(100, (val / run.tracksDiscovered) * 100) : 0}
                <div class="grid grid-cols-[1fr_80px_56px] items-center gap-2 text-[11.5px]">
                  <span class="text-muted-foreground">{s.label}</span>
                  <div class="bg-border h-[3px] overflow-hidden rounded-full">
                    <div class="bg-primary h-full" style="width: {pct}%;"></div>
                  </div>
                  <span class="text-muted-foreground text-right font-mono text-[11px]">
                    {val.toLocaleString()}
                  </span>
                </div>
              {/each}
            </div>

            <div class="flex flex-col gap-1.5">
              <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">
                Tail of log
              </div>
              <div class="bg-card border-border rounded-md border px-2.5 py-1.5">
                {#if run.logTail && run.logTail.length > 0}
                  {#each run.logTail as l (l.id)}
                    <div class="grid grid-cols-[1fr_auto] gap-2 py-[3px] text-[10.5px]">
                      <span class="truncate font-mono">
                        <span
                          class={cn(
                            l.type === 'failed'
                              ? 'text-destructive'
                              : l.type === 'review'
                                ? 'text-muted-foreground'
                                : 'text-primary'
                          )}>[{l.type}]</span>
                        <span class="text-muted-foreground">{l.track} — {l.artist}</span>
                      </span>
                      <span class="text-muted-foreground/70 font-mono">{l.time}</span>
                    </div>
                  {/each}
                  <p class="text-muted-foreground/70 px-1 pt-1.5 text-[10px]">
                    The last {run.logTail.length} events only — the run keeps no fuller log.
                  </p>
                {:else}
                  <div class="text-muted-foreground px-1 py-2 text-[11px]">
                    No log captured for this run.
                  </div>
                {/if}
              </div>
            </div>

            {#if run.status === 'running'}
              <div>
                <Button size="sm" class="gap-1.5" onclick={() => pipelineOverlay.setOpen(true)}>
                  <Radio class="size-3.5" />
                  View live pipeline
                </Button>
              </div>
            {/if}
          </div>
        </ScrollArea>
      {:else}
        <div class="text-muted-foreground grid flex-1 place-items-center p-6 text-sm">
          {loaded && runs.length === 0 ? 'Nothing to show yet.' : 'Select a run to see details.'}
        </div>
      {/if}
    </aside>
  </div>
</div>

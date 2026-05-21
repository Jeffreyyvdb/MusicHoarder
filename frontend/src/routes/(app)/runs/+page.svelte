<script lang="ts">
  import { Search, Disc3, Sparkles, PackageCheck, Check, X, Radio } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Button } from '$lib/components/ui/button';
  import { fetchRuns, fetchRun, type ApiRun, type ApiRunDetail } from '$lib/api-client';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import MobileRuns from '$lib/components/mobile/MobileRuns.svelte';
  import { cn } from '$lib/utils';

  const isMobile = new IsMobile();

  let runs = $state<ApiRun[]>([]);
  let activeId = $state<string | null>(null);
  let detail = $state<ApiRunDetail | null>(null);

  async function loadRuns() {
    try {
      runs = await fetchRuns();
      if (activeId === null && runs.length > 0) activeId = runs[0].id;
    } catch {
      // keep last good
    }
  }

  async function loadDetail(id: string) {
    try {
      detail = await fetchRun(id);
    } catch {
      detail = null;
    }
  }

  $effect(() => {
    void loadRuns();
    const poll = setInterval(loadRuns, 5_000);
    return () => clearInterval(poll);
  });

  // Reload detail whenever the selection changes, and refresh it on the same poll
  // cadence as the list so a running run's counters stay live.
  $effect(() => {
    if (activeId === null) {
      detail = null;
      return;
    }
    const id = activeId;
    void loadDetail(id);
    const poll = setInterval(() => void loadDetail(id), 5_000);
    return () => clearInterval(poll);
  });

  const completedCount = $derived(runs.filter((r) => r.status === 'completed').length);
  const runningCount = $derived(runs.filter((r) => r.status === 'running').length);

  function fmtDuration(seconds: number | null | undefined): string {
    if (seconds == null) return '—';
    const s = Math.max(0, Math.round(seconds));
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = s % 60;
    return [h, m, sec].map((n) => n.toString().padStart(2, '0')).join(':');
  }

  function fmtWhen(iso: string): string {
    const d = new Date(iso);
    const now = new Date();
    const sameDay = d.toDateString() === now.toDateString();
    const yesterday = new Date(now.getTime() - 86_400_000).toDateString() === d.toDateString();
    const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    if (sameDay) return `Today · ${time}`;
    if (yesterday) return `Yesterday · ${time}`;
    return `${d.toLocaleDateString([], { month: 'short', day: 'numeric' })} · ${time}`;
  }

  function liveDuration(run: ApiRunDetail): number | null {
    if (run.durationSeconds != null) return run.durationSeconds;
    if (run.status === 'running') return (Date.now() - new Date(run.startedAtUtc).getTime()) / 1000;
    return null;
  }

  const stageDefs = [
    { key: 'scan', label: 'Scan', icon: Search },
    { key: 'fingerprint', label: 'Fingerprint', icon: Disc3 },
    { key: 'enrich', label: 'Enrich', icon: Sparkles },
    { key: 'build', label: 'Build · write', icon: PackageCheck }
  ] as const;

  function stageValue(run: ApiRunDetail, key: string): number {
    switch (key) {
      case 'scan': return run.tracksProcessed;
      case 'fingerprint': return run.tracksFingerprinted;
      case 'enrich': return run.tracksEnriched;
      case 'build': return run.tracksCopied;
      default: return 0;
    }
  }

  const processedPct = $derived(
    detail && detail.tracksDiscovered > 0
      ? Math.min(100, Math.round((detail.tracksProcessed / detail.tracksDiscovered) * 100))
      : 0
  );
</script>

{#if isMobile.current}
  <MobileRuns />
{:else}
  <main class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <div class="border-border flex items-end justify-between border-b px-7 py-5">
      <div>
        <div class="text-muted-foreground font-mono text-[10px] tracking-[0.12em]">PIPELINE · RUNS</div>
        <h1 class="mt-1 text-2xl font-semibold tracking-tight">Ingest history</h1>
        <div class="text-muted-foreground mt-1 text-xs">
          {runs.length} runs · {completedCount} completed · {runningCount} running
        </div>
      </div>
    </div>

    <div class="grid min-h-0 flex-1 grid-cols-1 lg:grid-cols-[1fr_380px]">
      <!-- Runs table -->
      <ScrollArea class="min-h-0">
        <div class="px-3.5 pt-3.5 pb-8">
          <div
            class="text-muted-foreground border-border grid grid-cols-[24px_1.4fr_1.4fr_70px_80px_60px_60px_80px] items-center gap-3 border-b px-3 py-2 text-[10px] font-semibold tracking-wide uppercase"
          >
            <span></span>
            <span>Run</span>
            <span>Source</span>
            <span>Files</span>
            <span>Written</span>
            <span>Errors</span>
            <span>Review</span>
            <span>Duration</span>
          </div>
          {#if runs.length === 0}
            <p class="text-muted-foreground py-12 text-center text-sm">No ingest runs yet.</p>
          {/if}
          {#each runs as r (r.id)}
            <button
              class={cn(
                'grid w-full grid-cols-[24px_1.4fr_1.4fr_70px_80px_60px_60px_80px] items-center gap-3 rounded-md px-3 py-3 text-left text-xs transition-colors',
                'hover:bg-muted/60',
                activeId === r.id && 'bg-primary/10'
              )}
              onclick={() => (activeId = r.id)}
            >
              <span class="flex items-center justify-center">
                {#if r.status === 'running'}
                  <span class="relative grid size-3.5 place-items-center rounded-full bg-amber-500">
                    <span class="size-1.5 animate-pulse rounded-full bg-white"></span>
                  </span>
                {:else if r.status === 'completed'}
                  <span class="bg-primary grid size-3.5 place-items-center rounded-full">
                    <Check class="size-2.5 text-white" strokeWidth={3} />
                  </span>
                {:else}
                  <span class="grid size-3.5 place-items-center rounded-full bg-red-500">
                    <X class="size-2.5 text-white" strokeWidth={3} />
                  </span>
                {/if}
              </span>
              <span class="min-w-0">
                <div class="truncate text-[12.5px] font-medium">{fmtWhen(r.startedAtUtc)}</div>
                <div class="text-muted-foreground truncate font-mono text-[10.5px]">{r.id}</div>
              </span>
              <span class="text-muted-foreground truncate font-mono text-[11px]">{r.sourcePath}</span>
              <span class="font-mono">{r.tracksDiscovered.toLocaleString()}</span>
              <span class="font-mono">{r.tracksCopied.toLocaleString()}</span>
              <span class={cn('font-mono', r.tracksFailed > 0 && 'text-red-500')}>{r.tracksFailed}</span>
              <span class={cn('font-mono', r.tracksReview > 0 && 'text-amber-600 dark:text-amber-500')}>{r.tracksReview}</span>
              <span class="font-mono">{fmtDuration(r.durationSeconds)}</span>
            </button>
          {/each}
        </div>
      </ScrollArea>

      <!-- Detail aside -->
      <aside class="border-border bg-muted/30 flex min-h-0 flex-col border-t lg:border-t-0 lg:border-l">
        {#if detail}
          {@const run = detail}
          <ScrollArea class="min-h-0">
            <div class="flex flex-col gap-3.5 p-6">
              <div class="flex items-center gap-2.5">
                {#if run.status === 'running'}
                  <span class="size-2.5 shrink-0 animate-pulse rounded-full bg-amber-500"></span>
                {:else if run.status === 'completed'}
                  <span class="bg-primary size-2.5 shrink-0 rounded-full"></span>
                {:else}
                  <span class="size-2.5 shrink-0 rounded-full bg-red-500"></span>
                {/if}
                <div class="flex-1 truncate font-mono text-sm font-semibold">{run.id}</div>
                <span
                  class={cn(
                    'rounded-full px-2 py-0.5 text-[10px] font-bold tracking-wide uppercase',
                    run.status === 'running' && 'bg-amber-500/15 text-amber-600 dark:text-amber-500',
                    run.status === 'completed' && 'bg-primary/15 text-primary',
                    (run.status === 'cancelled' || run.status === 'failed') && 'bg-red-500/15 text-red-500'
                  )}
                >{run.status}</span>
              </div>
              <div class="text-muted-foreground -mt-2 truncate font-mono text-[11px]">{run.sourcePath}</div>

              <div class="grid grid-cols-2 gap-2">
                {#each [['STARTED', fmtWhen(run.startedAtUtc)], ['ENDED', run.endedAtUtc ? fmtWhen(run.endedAtUtc) : '—'], ['DURATION', fmtDuration(liveDuration(run))], ['THROUGHPUT', `${run.throughputPerSec} files/s`]] as [k, v] (k)}
                  <div class="bg-card border-border rounded-md border px-3 py-2.5">
                    <div class="text-muted-foreground text-[9.5px] font-semibold tracking-wide">{k}</div>
                    <div class="mt-0.5 truncate font-mono text-[13px] font-medium">{v}</div>
                  </div>
                {/each}
              </div>

              <div class="bg-border h-1.5 overflow-hidden rounded-full">
                <div class="bg-primary h-full transition-[width] duration-300" style="width: {processedPct}%;"></div>
              </div>
              <div class="text-muted-foreground -mt-2 flex justify-between font-mono text-[11px]">
                <span>{run.tracksProcessed.toLocaleString()} / {run.tracksDiscovered.toLocaleString()} processed</span>
                <span>{processedPct}%</span>
              </div>

              <div class="text-muted-foreground mt-1 text-[10px] font-semibold tracking-wide uppercase">Stage breakdown</div>
              <div class="bg-card border-border flex flex-col gap-1.5 rounded-md border px-3 py-2.5">
                {#each stageDefs as s (s.key)}
                  {@const Icon = s.icon}
                  {@const val = stageValue(run, s.key)}
                  {@const pct = run.tracksDiscovered > 0 ? Math.min(100, (val / run.tracksDiscovered) * 100) : 0}
                  <div class="grid grid-cols-[14px_1fr_80px_56px] items-center gap-2 text-[11.5px]">
                    <Icon class="text-primary size-3.5" />
                    <span class="text-muted-foreground">{s.label}</span>
                    <div class="bg-border h-[3px] overflow-hidden rounded-full">
                      <div class="bg-primary h-full" style="width: {pct}%;"></div>
                    </div>
                    <span class="text-muted-foreground text-right font-mono text-[11px]">{val.toLocaleString()}</span>
                  </div>
                {/each}
              </div>

              <div class="text-muted-foreground mt-1 text-[10px] font-semibold tracking-wide uppercase">Tail of log</div>
              <div class="bg-card border-border rounded-md border px-2.5 py-1.5">
                {#if run.logTail && run.logTail.length > 0}
                  {#each run.logTail.slice(0, 10) as l (l.id)}
                    <div class="grid grid-cols-[1fr_auto] gap-2 py-[3px] text-[10.5px]">
                      <span class="truncate font-mono">
                        <span class={cn(
                          l.type === 'failed' ? 'text-red-500' : l.type === 'review' ? 'text-amber-600 dark:text-amber-500' : 'text-primary'
                        )}>[{l.type}]</span>
                        <span class="text-muted-foreground">{l.track} — {l.artist}</span>
                      </span>
                      <span class="text-muted-foreground/70 font-mono">{l.time}</span>
                    </div>
                  {/each}
                {:else}
                  <div class="text-muted-foreground px-1 py-2 text-[11px]">No log captured for this run.</div>
                {/if}
              </div>

              {#if run.status === 'running'}
                <div class="mt-1 flex gap-2">
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
            Select a run to see details.
          </div>
        {/if}
      </aside>
    </div>
  </main>
{/if}

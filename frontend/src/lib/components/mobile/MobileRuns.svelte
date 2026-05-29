<script lang="ts">
  import { onMount } from 'svelte';
  import { Search, Fingerprint, Sparkles, PackageCheck } from '@lucide/svelte';
  import MobileHeader from '$lib/components/mobile/MobileHeader.svelte';
  import { Progress } from '$lib/components/ui/progress/index.js';
  import { fetchRuns, fetchRun, type ApiRun, type ApiRunDetail } from '$lib/api-client';

  let runs = $state<ApiRun[]>([]);
  let openId = $state<string | null>(null);
  let detail = $state<ApiRunDetail | null>(null);

  async function loadRuns() {
    try {
      runs = await fetchRuns();
    } catch {
      // keep last good
    }
  }

  onMount(() => {
    void loadRuns();
    const poll = setInterval(loadRuns, 15000);
    return () => clearInterval(poll);
  });

  $effect(() => {
    if (openId === null) {
      detail = null;
      return;
    }
    const id = openId;
    void (async () => {
      detail = await fetchRun(id);
    })();
  });

  const completedCount = $derived(runs.filter((r) => r.status === 'completed').length);
  const runningCount = $derived(runs.filter((r) => r.status === 'running').length);

  function statusClass(status: ApiRun['status']): 'running' | 'completed' | 'aborted' {
    if (status === 'running') return 'running';
    if (status === 'completed') return 'completed';
    return 'aborted';
  }

  function fmtDuration(seconds: number | null | undefined, run?: ApiRun): string {
    let s = seconds;
    if (s == null && run?.status === 'running') {
      s = (Date.now() - new Date(run.startedAtUtc).getTime()) / 1000;
    }
    if (s == null) return '—';
    s = Math.max(0, Math.round(s));
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = s % 60;
    return [h, m, sec].map((n) => n.toString().padStart(2, '0')).join(':');
  }

  function fmtWhen(iso: string): string {
    const d = new Date(iso);
    const now = new Date();
    const time = d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    if (d.toDateString() === now.toDateString()) return `Today · ${time}`;
    if (new Date(now.getTime() - 86_400_000).toDateString() === d.toDateString()) return `Yesterday · ${time}`;
    return `${d.toLocaleDateString([], { month: 'short', day: 'numeric' })} · ${time}`;
  }

  const stageDefs = [
    { key: 'scan', label: 'Scan', icon: Search },
    { key: 'fingerprint', label: 'Fingerprint', icon: Fingerprint },
    { key: 'enrich', label: 'Enrich', icon: Sparkles },
    { key: 'build', label: 'Build', icon: PackageCheck }
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
</script>

{#if openId && detail}
  {@const run = detail}
  {@const pct = run.tracksDiscovered > 0 ? Math.round((run.tracksProcessed / run.tracksDiscovered) * 100) : 0}
  <div class="mob">
    <MobileHeader back="Runs" onback={() => (openId = null)} title="">
      {#snippet right()}
        <span class="mob-run-status st-{statusClass(run.status)}">{run.status}</span>
      {/snippet}
    </MobileHeader>
    <div class="mob-scroll">
      <div class="px-4 pt-2 pb-4">
        <div class="text-muted-foreground font-mono text-[11px]">{run.id}</div>
        <div class="mt-1 text-xl font-semibold tracking-[-0.02em]">{run.triggerLabel ?? fmtWhen(run.startedAtUtc)}</div>
        <div class="text-muted-foreground mt-1.5 truncate font-mono text-xs">{run.sourcePath}</div>
      </div>

      <div class="mob-grouped">
        {#each [['Started', fmtWhen(run.startedAtUtc)], ['Ended', run.endedAtUtc ? fmtWhen(run.endedAtUtc) : '—'], ['Duration', fmtDuration(run.durationSeconds, run)], ['Throughput', `${run.throughputPerSec} files/s`]] as [k, v] (k)}
          <div class="mob-row">
            <div class="mob-row-meta"><div class="mob-row-t text-[13.5px]">{k}</div></div>
            <span class="text-muted-foreground max-w-[55%] truncate font-mono text-[12.5px]">{v}</span>
          </div>
        {/each}
      </div>

      <div class="px-4 pb-4">
        <Progress value={pct} class="mb-1.5" />
        <div class="text-muted-foreground flex justify-between font-mono text-[11.5px]">
          <span>{run.tracksProcessed.toLocaleString()} / {run.tracksDiscovered.toLocaleString()} processed</span>
          <span>{pct}%</span>
        </div>
      </div>

      <div class="mob-grouped-h">Stages</div>
      <div class="mob-grouped">
        {#each stageDefs as s (s.key)}
          {@const Icon = s.icon}
          {@const val = stageValue(run, s.key)}
          {@const stPct = run.tracksDiscovered > 0 ? Math.min(100, (val / run.tracksDiscovered) * 100) : 0}
          <div class="mob-row">
            <Icon size={14} class="text-primary" />
            <div class="mob-row-meta">
              <div class="mob-row-t text-[13.5px]">{s.label}</div>
              <Progress value={stPct} class="mt-1.5" />
            </div>
            <span class="text-muted-foreground font-mono text-[11.5px]">{val.toLocaleString()}</span>
          </div>
        {/each}
      </div>

      <div class="mob-grouped-h">Tail of log</div>
      <div class="mx-4 mb-6 rounded-[10px] border px-2.5 py-1.5" style="border-color: var(--border);">
        {#if run.logTail && run.logTail.length > 0}
          {#each run.logTail.slice(0, 10) as l (l.id)}
            <div class="grid grid-cols-[1fr_auto] gap-2 py-[3px] text-[10.5px]">
              <span class="truncate font-mono">
                <span class="text-primary">[{l.type}]</span>
                <span class="text-muted-foreground">{l.track} — {l.artist}</span>
              </span>
              <span class="text-muted-foreground/70 font-mono">{l.time}</span>
            </div>
          {/each}
        {:else}
          <div class="text-muted-foreground px-1 py-2 text-[11px]">No log captured for this run.</div>
        {/if}
      </div>
    </div>
  </div>
{:else}
  <div class="mob">
    <MobileHeader title="Runs · history" sub="{completedCount} completed · {runningCount} running" />
    <div class="mob-scroll pt-2.5">
      {#if runs.length === 0}
        <div class="text-muted-foreground px-6 py-16 text-center text-sm">No ingest runs yet.</div>
      {:else}
        {#each runs as r (r.id)}
          {@const pct = r.tracksDiscovered > 0 ? Math.round((r.tracksProcessed / r.tracksDiscovered) * 100) : 0}
          <button class="mob-run-card {r.status === 'running' ? 'active' : ''}" onclick={() => (openId = r.id)}>
            <div class="mob-run-top">
              <span class="mob-run-status st-{statusClass(r.status)}">{r.status}</span>
              <span class="mob-run-when">{fmtWhen(r.startedAtUtc)}</span>
              <span class="mob-run-dur">{fmtDuration(r.durationSeconds, r)}</span>
            </div>
            <div class="mob-run-src">{r.triggerLabel ?? r.sourcePath}</div>
            {#if r.status === 'running'}
              <Progress value={pct} class="mb-2" />
            {/if}
            <div class="mob-run-stats">
              <span><strong>{r.tracksProcessed.toLocaleString()}</strong> / {r.tracksDiscovered.toLocaleString()}</span>
              <span>· <strong>{r.tracksCopied.toLocaleString()}</strong> written</span>
              {#if r.tracksFailed > 0}<span class="err">· {r.tracksFailed} err</span>{/if}
              {#if r.tracksReview > 0}<span class="warn">· {r.tracksReview} review</span>{/if}
            </div>
          </button>
        {/each}
        <div class="h-8"></div>
      {/if}
    </div>
  </div>
{/if}

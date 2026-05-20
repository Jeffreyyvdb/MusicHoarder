<script lang="ts">
  import { onMount } from 'svelte';
  import { Plus, Search, Fingerprint, Sparkles, PackageCheck, FileText } from '@lucide/svelte';
  import MobileHeader from '$lib/components/mobile/MobileHeader.svelte';
  import { fetchOverview, type ApiOverview, type ApiOverviewActivity } from '$lib/api-client';

  let overview = $state<ApiOverview | null>(null);
  let elapsedMin = $state<number | null>(null);
  let openDetail = $state(false);

  async function load() {
    try {
      overview = await fetchOverview();
    } catch {
      // keep last good
    }
  }

  onMount(() => {
    void load();
    const poll = setInterval(load, 15000);
    const tick = setInterval(() => {
      const startedAt = overview?.job?.startedAt;
      elapsedMin = startedAt
        ? Math.floor((Date.now() - new Date(startedAt).getTime()) / 60000)
        : null;
    }, 30000);
    return () => {
      clearInterval(poll);
      clearInterval(tick);
    };
  });

  $effect(() => {
    const startedAt = overview?.job?.startedAt;
    elapsedMin = startedAt ? Math.floor((Date.now() - new Date(startedAt).getTime()) / 60000) : null;
  });

  const job = $derived(overview?.job ?? null);
  const status = $derived<'running' | 'completed'>(job?.status ?? 'completed');
  const discovered = $derived(job?.tracksDiscovered ?? 0);
  const processed = $derived(job?.tracksProcessed ?? 0);
  const copied = $derived(job?.tracksCopied ?? 0);
  const review = $derived(job?.tracksReview ?? 0);
  const failed = $derived(job?.tracksFailed ?? 0);
  const pct = $derived(discovered > 0 ? Math.round((processed / discovered) * 100) : 0);

  const startedLabel = $derived(
    job?.startedAt ? new Date(job.startedAt).toLocaleString() : '—'
  );
  const durationLabel = $derived(elapsedMin !== null ? `${elapsedMin} min` : '—');

  const activity = $derived<ApiOverviewActivity[]>(overview?.recentActivity ?? []);

  const stages = $derived([
    { icon: Search, label: 'Scan', value: job?.tracksProcessed ?? 0, pct: discovered ? (processed / discovered) * 100 : 0 },
    { icon: Fingerprint, label: 'Fingerprint', value: job?.tracksFingerprinted ?? 0, pct: discovered ? ((job?.tracksFingerprinted ?? 0) / discovered) * 100 : 0 },
    { icon: Sparkles, label: 'Enrich', value: job?.tracksEnriched ?? 0, pct: discovered ? ((job?.tracksEnriched ?? 0) / discovered) * 100 : 0 },
    { icon: PackageCheck, label: 'Build', value: copied, pct: (job?.tracksBuildEligible ?? 0) > 0 ? (copied / (job?.tracksBuildEligible ?? 1)) * 100 : 0 }
  ]);
</script>

{#if openDetail}
  <div class="mob">
    <MobileHeader back="Runs" onback={() => (openDetail = false)} title="">
      {#snippet right()}
        <span class="mob-run-status st-{status}">{status}</span>
      {/snippet}
    </MobileHeader>
    <div class="mob-scroll">
      <div class="px-4 pt-2 pb-4">
        <div class="text-muted-foreground font-mono text-[11px]">current session</div>
        <div class="mt-1 text-xl font-semibold tracking-[-0.02em]">{startedLabel}</div>
        <div class="text-muted-foreground mt-1.5 truncate font-mono text-xs">
          {overview?.sourcePath ?? '—'}
        </div>
      </div>

      <div class="mob-grouped">
        {#each [['Started', startedLabel], ['Elapsed', durationLabel], ['Source', overview?.sourcePath ?? '—'], ['Destination', overview?.destinationPath ?? '—']] as [k, v] (k)}
          <div class="mob-row">
            <div class="mob-row-meta"><div class="mob-row-t text-[13.5px]">{k}</div></div>
            <span class="text-muted-foreground max-w-[55%] truncate font-mono text-[12.5px]">{v}</span>
          </div>
        {/each}
      </div>

      <div class="px-4 pb-4">
        <div class="mob-bar mb-1.5"><div class="mob-bar-fill" style="width: {pct}%;"></div></div>
        <div class="text-muted-foreground flex justify-between font-mono text-[11.5px]">
          <span>{processed.toLocaleString()} / {discovered.toLocaleString()} processed</span>
          <span>{pct}%</span>
        </div>
      </div>

      <div class="mob-grouped-h">Stages</div>
      <div class="mob-grouped">
        {#each stages as s (s.label)}
          {@const Icon = s.icon}
          <div class="mob-row">
            <Icon size={14} class="text-primary" />
            <div class="mob-row-meta">
              <div class="mob-row-t text-[13.5px]">{s.label}</div>
              <div class="mob-bar mt-1.5"><div class="mob-bar-fill" style="width: {Math.min(100, s.pct)}%;"></div></div>
            </div>
            <span class="text-muted-foreground font-mono text-[11.5px]">{s.value.toLocaleString()}</span>
          </div>
        {/each}
      </div>

      <div class="mob-grouped-h">Recent activity</div>
      <div class="mx-4 mb-6 rounded-[10px] border px-2.5 py-1.5" style="border-color: var(--border);">
        {#if activity.length === 0}
          <div class="text-muted-foreground px-1 py-2 text-[11px]">No recent activity.</div>
        {:else}
          {#each activity.slice(0, 10) as a (a.id)}
            <div class="grid grid-cols-[1fr_auto] gap-2 py-[3px] text-[10.5px]">
              <span class="truncate font-mono">
                <span class="text-primary">[{a.type}]</span>
                <span class="text-muted-foreground">{a.track} — {a.artist}</span>
              </span>
              <span class="text-muted-foreground/70 font-mono">{a.time}</span>
            </div>
          {/each}
        {/if}
      </div>
    </div>
  </div>
{:else}
  <div class="mob">
    <MobileHeader title="Runs · history" sub="{status === 'running' ? '1 running' : 'idle'} · {failed} failed">
      {#snippet right()}
        <button class="mob-h-btn primary" aria-label="New run"><Plus size={14} strokeWidth={2.5} /></button>
      {/snippet}
    </MobileHeader>
    <div class="mob-scroll pt-2.5">
      {#if !overview}
        <div class="text-muted-foreground px-6 py-16 text-center text-sm">Loading…</div>
      {:else}
        <button class="mob-run-card {status === 'running' ? 'active' : ''}" onclick={() => (openDetail = true)}>
          <div class="mob-run-top">
            <span class="mob-run-status st-{status}">{status}</span>
            <span class="mob-run-when">{startedLabel}</span>
            <span class="mob-run-dur">{durationLabel}</span>
          </div>
          <div class="mob-run-src">{overview.sourcePath}</div>
          {#if status === 'running'}
            <div class="mob-bar mb-2"><div class="mob-bar-fill" style="width: {pct}%;"></div></div>
          {/if}
          <div class="mob-run-stats">
            <span><strong>{processed.toLocaleString()}</strong> / {discovered.toLocaleString()}</span>
            <span>· <strong>{copied.toLocaleString()}</strong> written</span>
            {#if failed > 0}<span class="err">· {failed} err</span>{/if}
            {#if review > 0}<span class="warn">· {review} review</span>{/if}
          </div>
        </button>

        {#if activity.length > 0}
          <div class="mob-grouped-h">Recent activity</div>
          <div class="mob-grouped">
            {#each activity.slice(0, 12) as a (a.id)}
              <div class="mob-row">
                <FileText size={15} class="text-muted-foreground" />
                <div class="mob-row-meta">
                  <div class="mob-row-t">{a.track}</div>
                  <div class="mob-row-s">{a.artist} · {a.type}</div>
                </div>
                <span class="mob-row-r">{a.time}</span>
              </div>
            {/each}
          </div>
        {/if}
      {/if}
    </div>
  </div>
{/if}

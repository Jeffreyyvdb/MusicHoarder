<script lang="ts">
  import {
    downloadQualityExport,
    fetchQualityOverview,
    fetchQualityProgress,
    gradeAllSongs,
    gradeDirectory,
    gradeSong,
    type QualityOverview,
    type QualityVerdict
  } from '$lib/api-client';
  import { cn } from '$lib/utils';
  import { Download, Gauge, Loader2, RefreshCw, Sparkles } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';

  let overview = $state<QualityOverview | null>(null);
  let isLoading = $state(true);
  let error = $state<string | null>(null);
  let busy = $state(false);
  let polling = $state(false);
  let gradingId = $state<number | null>(null);

  async function load() {
    try {
      overview = await fetchQualityOverview();
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load quality overview';
    } finally {
      isLoading = false;
    }
  }

  $effect(() => {
    void load();
  });

  function verdictTint(v: QualityVerdict): string {
    switch (v) {
      case 'Excellent':
        return 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400 border-emerald-500/30';
      case 'Good':
        return 'bg-teal-500/15 text-teal-600 dark:text-teal-400 border-teal-500/30';
      case 'Questionable':
        return 'bg-amber-500/15 text-amber-600 dark:text-amber-400 border-amber-500/30';
      case 'Wrong':
        return 'bg-red-500/15 text-red-600 dark:text-red-400 border-red-500/30';
      default:
        return 'bg-muted text-muted-foreground border-border';
    }
  }

  function scoreColor(score: number): string {
    if (score >= 90) return 'text-emerald-600 dark:text-emerald-400';
    if (score >= 70) return 'text-teal-600 dark:text-teal-400';
    if (score >= 40) return 'text-amber-600 dark:text-amber-400';
    return 'text-red-600 dark:text-red-400';
  }

  const verdictOrder = [
    { key: 'wrong', label: 'Wrong', color: 'bg-red-500' },
    { key: 'questionable', label: 'Questionable', color: 'bg-amber-500' },
    { key: 'good', label: 'Good', color: 'bg-teal-500' },
    { key: 'excellent', label: 'Excellent', color: 'bg-emerald-500' },
    { key: 'ungradeable', label: 'Ungradeable', color: 'bg-muted-foreground/40' }
  ] as const;

  async function pollUntilDone() {
    if (polling) return;
    polling = true;
    try {
      // Poll the grading run until it drains, then refresh the rollups.
      for (let i = 0; i < 600; i++) {
        const p = await fetchQualityProgress();
        if (!p.active) break;
        await new Promise((r) => setTimeout(r, 2000));
      }
      await load();
    } finally {
      polling = false;
    }
  }

  async function onGradeAll() {
    busy = true;
    try {
      const r = await gradeAllSongs();
      toast.success(`Queued ${r.enqueued.toLocaleString()} songs for grading`);
      void pollUntilDone();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Failed to start grading');
    } finally {
      busy = false;
    }
  }

  async function onGradeDirectory(path: string) {
    try {
      const r = await gradeDirectory(path);
      toast.success(`Queued ${r.enqueued.toLocaleString()} songs in this folder`);
      void pollUntilDone();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Failed to grade folder');
    }
  }

  async function onRegrade(songId: number) {
    gradingId = songId;
    try {
      const r = await gradeSong(songId);
      toast.success(`Graded: ${r.verdict ?? r.outcome}${r.score != null ? ` (${r.score})` : ''}`);
      await load();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Grading failed');
    } finally {
      gradingId = null;
    }
  }

  async function onExport(scope: 'song' | 'directory' | 'library', opts: { songId?: number; path?: string } = {}) {
    try {
      await downloadQualityExport(scope, opts);
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Export failed');
    }
  }

  function dirName(path: string): string {
    const segs = path.replace(/\/+$/, '').split('/');
    return segs.slice(-2).join('/') || path;
  }

  const lib = $derived(overview?.library ?? null);
  const coveragePct = $derived(overview ? Math.round(overview.coverage * 100) : 0);
</script>

<div class="flex h-full flex-col overflow-hidden">
  <header class="border-border flex flex-wrap items-center gap-3 border-b px-5 py-4">
    <Gauge class="text-primary size-5 shrink-0" />
    <div class="min-w-0 flex-1">
      <h1 class="truncate text-base font-semibold">AI library quality</h1>
      <p class="text-muted-foreground text-[12px]">
        An LLM grades each enrichment result so you can benchmark and debug the algorithm.
      </p>
    </div>
    <button
      type="button"
      disabled={busy || polling}
      onclick={onGradeAll}
      class="bg-primary text-primary-foreground inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-[12.5px] font-medium transition-opacity hover:opacity-90 disabled:opacity-50"
    >
      {#if busy || polling}
        <Loader2 class="size-3.5 animate-spin" />
      {:else}
        <Sparkles class="size-3.5" />
      {/if}
      Grade all
    </button>
    <button
      type="button"
      onclick={() => onExport('library')}
      class="border-border hover:bg-accent inline-flex items-center gap-1.5 rounded-md border px-3 py-1.5 text-[12.5px] font-medium transition-colors"
    >
      <Download class="size-3.5" /> Export library
    </button>
    <button
      type="button"
      onclick={load}
      aria-label="Refresh"
      class="border-border hover:bg-accent inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12.5px] transition-colors"
    >
      <RefreshCw class={cn('size-3.5', isLoading && 'animate-spin')} />
    </button>
  </header>

  <div class="min-h-0 flex-1 overflow-y-auto px-5 py-4">
    {#if error}
      <div class="rounded-md border border-red-500/30 bg-red-500/10 px-4 py-3 text-[13px] text-red-600 dark:text-red-400">
        {error}
      </div>
    {:else if isLoading}
      <div class="text-muted-foreground flex items-center gap-2 py-10 text-[13px]">
        <Loader2 class="size-4 animate-spin" /> Loading quality rollups…
      </div>
    {:else if overview && lib}
      {#if polling}
        <div class="text-muted-foreground mb-4 flex items-center gap-2 rounded-md border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-[12.5px]">
          <Loader2 class="size-3.5 animate-spin" /> Grading in progress — rollups refresh when it finishes.
        </div>
      {/if}

      <!-- KPIs -->
      <div class="mb-5 grid grid-cols-2 gap-3 sm:grid-cols-4">
        <div class="border-border rounded-lg border p-3">
          <div class="text-muted-foreground text-[10.5px] tracking-wide uppercase">Graded</div>
          <div class="mt-1 text-xl font-semibold">
            {lib.graded.toLocaleString()}<span class="text-muted-foreground text-[12px] font-normal"> / {overview.gradeableTotal.toLocaleString()}</span>
          </div>
        </div>
        <div class="border-border rounded-lg border p-3">
          <div class="text-muted-foreground text-[10.5px] tracking-wide uppercase">Coverage</div>
          <div class="mt-1 text-xl font-semibold">{coveragePct}%</div>
        </div>
        <div class="border-border rounded-lg border p-3">
          <div class="text-muted-foreground text-[10.5px] tracking-wide uppercase">Avg score</div>
          <div class={cn('mt-1 text-xl font-semibold', lib.averageScore != null && scoreColor(lib.averageScore))}>
            {lib.averageScore ?? '—'}
          </div>
        </div>
        <div class="border-border rounded-lg border p-3">
          <div class="text-muted-foreground text-[10.5px] tracking-wide uppercase">Wrong</div>
          <div class="mt-1 text-xl font-semibold text-red-600 dark:text-red-400">{lib.verdicts.wrong.toLocaleString()}</div>
        </div>
      </div>

      <!-- Verdict distribution -->
      {#if lib.graded > 0}
        <div class="mb-2 flex h-2.5 w-full overflow-hidden rounded-full">
          {#each verdictOrder as v (v.key)}
            {@const n = lib.verdicts[v.key]}
            {#if n > 0}
              <div class={v.color} style="width: {(n / lib.graded) * 100}%" title={`${v.label}: ${n}`}></div>
            {/if}
          {/each}
        </div>
        <div class="text-muted-foreground mb-5 flex flex-wrap gap-x-4 gap-y-1 text-[11px]">
          {#each verdictOrder as v (v.key)}
            <span class="inline-flex items-center gap-1.5">
              <span class={cn('size-2 rounded-full', v.color)}></span>{v.label}
              <span class="font-mono">{lib.verdicts[v.key]}</span>
            </span>
          {/each}
        </div>
      {:else}
        <div class="text-muted-foreground mb-5 rounded-md border border-dashed px-4 py-6 text-center text-[13px]">
          Nothing graded yet. Click <span class="font-medium">Grade all</span> to start, or grade a single song from the
          Provenance &amp; review page. Grading needs <code class="font-mono text-[12px]">QualityGrading:ApiKey</code> configured.
        </div>
      {/if}

      <!-- Top issues -->
      {#if lib.topIssues.length > 0}
        <section class="mb-6">
          <h2 class="mb-2 text-[12px] font-semibold tracking-wide uppercase">Most common issues</h2>
          <div class="flex flex-wrap gap-2">
            {#each lib.topIssues as issue (issue.code)}
              <span class="border-border bg-muted/40 inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11.5px]">
                <code class="font-mono">{issue.code}</code>
                <span class="text-muted-foreground font-mono">{issue.count}</span>
              </span>
            {/each}
          </div>
        </section>
      {/if}

      <!-- Worst offenders -->
      {#if overview.worstOffenders.length > 0}
        <section class="mb-6">
          <h2 class="mb-2 text-[12px] font-semibold tracking-wide uppercase">Worst offenders</h2>
          <div class="border-border divide-border divide-y rounded-lg border">
            {#each overview.worstOffenders as o (o.songId)}
              <article class="flex items-start gap-3 px-3 py-2.5">
                <span class={cn('mt-0.5 shrink-0 rounded-md border px-1.5 py-0.5 text-[10px] font-semibold', verdictTint(o.verdict))}>
                  {o.verdict}
                </span>
                <span class={cn('mt-0.5 w-8 shrink-0 text-right font-mono text-[13px] font-semibold', scoreColor(o.score))}>{o.score}</span>
                <div class="min-w-0 flex-1">
                  <div class="truncate text-[13px] font-medium">
                    {o.title || o.fileName}
                    {#if o.artist}<span class="text-muted-foreground font-normal"> · {o.artist}</span>{/if}
                  </div>
                  {#if o.summary}<div class="text-muted-foreground truncate text-[11.5px]">{o.summary}</div>{/if}
                  <div class="text-muted-foreground/80 truncate font-mono text-[10.5px]" title={o.sourcePath}>{o.sourcePath}</div>
                  {#if o.destinationPathPreview}
                    <div class="text-muted-foreground/80 truncate font-mono text-[10.5px]" title={o.destinationPathPreview}>
                      → {o.destinationPathPreview}
                    </div>
                  {/if}
                  {#if o.issues.length > 0}
                    <div class="mt-1 flex flex-wrap gap-1">
                      {#each o.issues as issue (issue.code)}
                        <code class="bg-muted/60 rounded px-1 py-px font-mono text-[10px]">{issue.code}</code>
                      {/each}
                    </div>
                  {/if}
                </div>
                <div class="flex shrink-0 items-center gap-1">
                  <a
                    href={`/review?song=${o.songId}`}
                    class="border-border hover:bg-accent rounded-md border px-2 py-1 text-[11px] transition-colors"
                  >Review</a>
                  <button
                    type="button"
                    disabled={gradingId === o.songId}
                    onclick={() => onRegrade(o.songId)}
                    class="border-border hover:bg-accent inline-flex items-center gap-1 rounded-md border px-2 py-1 text-[11px] transition-colors disabled:opacity-50"
                  >
                    {#if gradingId === o.songId}<Loader2 class="size-3 animate-spin" />{:else}<Sparkles class="size-3" />{/if}
                    Re-grade
                  </button>
                  <button
                    type="button"
                    aria-label="Export song dossier"
                    onclick={() => onExport('song', { songId: o.songId })}
                    class="border-border hover:bg-accent rounded-md border px-2 py-1 text-[11px] transition-colors"
                  >
                    <Download class="size-3" />
                  </button>
                </div>
              </article>
            {/each}
          </div>
        </section>
      {/if}

      <!-- Per-directory -->
      {#if overview.directories.length > 0}
        <section>
          <h2 class="mb-2 text-[12px] font-semibold tracking-wide uppercase">By folder</h2>
          <div class="border-border divide-border divide-y rounded-lg border">
            {#each overview.directories as d (d.directory)}
              <article class="flex items-center gap-3 px-3 py-2">
                <span class={cn('shrink-0 rounded-md border px-1.5 py-0.5 text-[10px] font-semibold', verdictTint(d.worstVerdict))}>
                  {d.worstVerdict}
                </span>
                <div class="min-w-0 flex-1">
                  <div class="truncate text-[12.5px] font-medium" title={d.directory}>{dirName(d.directory)}</div>
                  <div class="text-muted-foreground text-[11px]">
                    {d.rollup.graded} graded · avg {d.rollup.averageScore ?? '—'}
                    {#if d.wrongCount > 0}<span class="text-red-600 dark:text-red-400"> · {d.wrongCount} wrong</span>{/if}
                  </div>
                </div>
                <button
                  type="button"
                  onclick={() => onGradeDirectory(d.directory)}
                  class="border-border hover:bg-accent inline-flex items-center gap-1 rounded-md border px-2 py-1 text-[11px] transition-colors"
                >
                  <Sparkles class="size-3" /> Grade
                </button>
                <button
                  type="button"
                  aria-label="Export folder dossiers"
                  onclick={() => onExport('directory', { path: d.directory })}
                  class="border-border hover:bg-accent rounded-md border px-2 py-1 text-[11px] transition-colors"
                >
                  <Download class="size-3" />
                </button>
              </article>
            {/each}
          </div>
        </section>
      {/if}
    {/if}
  </div>
</div>

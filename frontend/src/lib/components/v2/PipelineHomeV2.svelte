<script lang="ts">
  import {
    Disc3,
    PackageCheck,
    RefreshCw,
    Sparkles,
    Tag,
    ScanLine,
    Copy,
    CheckCheck,
    ChevronRight,
    Clock,
    AlertTriangle,
    Wand2,
    Loader2,
    History
  } from '@lucide/svelte';
  import type { Component } from 'svelte';
  import { goto } from '$app/navigation';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import {
    buildAlbumsFromSongs,
    fetchDuplicates,
    fetchQualityOverview,
    fetchQualityProgress,
    fetchSongs,
    fetchStats,
    mapEnrichmentStatus,
    sortAlbumsByRecency,
    triggerEnrichmentScan,
    type AlbumSummary,
    type ApiOverviewActivity,
    type ApiSong,
    type ApiStats,
    type DuplicatesResponse,
    type QualityOverview,
    type QualityProgress
  } from '$lib/api-client';
  import { isBuiltSong } from '$lib/album-sections';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { cn } from '$lib/utils';
  import PipelineSubNavV2 from './PipelineSubNavV2.svelte';

  // ── data layer (reuses the existing api-client + album-sections) ───────────
  let stats = $state<ApiStats | null>(null);
  let songs = $state<ApiSong[]>([]);
  let quality = $state<QualityOverview | null>(null);
  let qualityProgress = $state<QualityProgress | null>(null);
  let duplicates = $state<DuplicatesResponse | null>(null);
  let loaded = $state(false);
  let rescanning = $state(false);

  async function loadAll() {
    const [stRes, songsRes, qRes, qpRes, dupRes] = await Promise.allSettled([
      fetchStats(),
      fetchSongs(),
      fetchQualityOverview(),
      fetchQualityProgress(),
      fetchDuplicates()
    ]);
    if (stRes.status === 'fulfilled') stats = stRes.value;
    if (songsRes.status === 'fulfilled') songs = songsRes.value;
    // Quality grading may be unconfigured — failure just leaves the KPI as "—".
    if (qRes.status === 'fulfilled') quality = qRes.value;
    if (qpRes.status === 'fulfilled') qualityProgress = qpRes.value;
    if (dupRes.status === 'fulfilled') duplicates = dupRes.value;
    loaded = true;
  }

  $effect(() => {
    void loadAll();
    // Re-poll on the same cadence as the rest of the app so counts stay fresh
    // while a job runs. The SSE-backed conveyor updates independently.
    const poll = setInterval(() => void loadAll(), 8_000);
    return () => clearInterval(poll);
  });

  // ── live pipeline (SSE) ────────────────────────────────────────────────────
  const snap = $derived(pipelineOverlay.snapshot);
  const overview = $derived(pipelineOverlay.overview);
  const rates = $derived(pipelineOverlay.rates);
  const anyRunning = $derived(pipelineOverlay.isAnyRunning);

  // Keep the SSE stream + overview poll alive while the conveyor is mounted,
  // without opening the bottom drawer, so live counts reflect a running job.
  $effect(() => pipelineOverlay.keepLive());

  // ── KPI derivations (all from REAL data; null → skeleton / em-dash) ─────────
  const sourceTotal = $derived(stats?.tracks?.total ?? null);
  const sourceBytes = $derived(stats?.storage?.totalBytes ?? null);

  const inLibrary = $derived.by(() => {
    if (!loaded) return null;
    return songs.filter(isBuiltSong).length;
  });

  const matchedCount = $derived.by(() => {
    if (!loaded) return null;
    return songs.filter((s) => mapEnrichmentStatus(s.enrichmentStatus) === 'complete').length;
  });

  const enrichedPct = $derived.by(() => {
    if (matchedCount == null || sourceTotal == null || sourceTotal === 0) return null;
    return (matchedCount / sourceTotal) * 100;
  });

  // In-flight = files currently mid-pipeline. While a job runs, derive it from
  // the SSE snapshot (discovered minus those that reached the final stage). When
  // nothing is running it's genuinely 0.
  const inFlight = $derived.by(() => {
    if (!snap || !anyRunning) return 0;
    const discovered = snap.discovered ?? 0;
    const done = snap.built ?? 0;
    return Math.max(0, discovered - done);
  });

  // Awaiting-you breakdown — real counts. Tags = needsreview; AI = quality
  // "wrong" verdicts; Duplicates = fingerprint-flagged tracks (read-only count,
  // no resolve endpoint yet — see duplicateCount below).
  const tagReviewCount = $derived.by(() => {
    if (!loaded) return null;
    return songs.filter((s) => {
      const n = mapEnrichmentStatus(s.enrichmentStatus);
      return n === 'needsreview' || n === 'failed';
    }).length;
  });
  const aiFlaggedCount = $derived(quality?.library?.verdicts?.wrong ?? null);
  const awaitingYou = $derived.by(() => {
    if (tagReviewCount == null && aiFlaggedCount == null) return null;
    return (tagReviewCount ?? 0) + (aiFlaggedCount ?? 0);
  });

  const avgQuality = $derived(quality?.library?.averageScore ?? null);
  const qualityGraded = $derived(quality?.library?.graded ?? null);

  // Duplicate tracks flagged by fingerprint dedupe (DB-backed; read-only count).
  const duplicateCount = $derived(duplicates?.totalDuplicates ?? null);

  // Consensus "Decide" count — files that reached a terminal enrichment verdict
  // (matched or sent to review). Live from the SSE snapshot while enriching,
  // otherwise from the whole-library DB counts.
  const decidedCount = $derived.by(() => {
    if (anyRunning && snap) return (snap.enriched ?? 0) + (snap.needsReview ?? 0);
    if (matchedCount == null && tagReviewCount == null) return null;
    return (matchedCount ?? 0) + (tagReviewCount ?? 0);
  });

  // Errors — failed enrichment in the current dataset (not a fabricated 24h window).
  const errorCount = $derived.by(() => {
    if (!loaded) return null;
    return songs.filter((s) => mapEnrichmentStatus(s.enrichmentStatus) === 'failed').length;
  });

  // ── conveyor stages ─────────────────────────────────────────────────────────
  type StageId = 'scan' | 'fingerprint' | 'match' | 'decide' | 'grade' | 'dedupe' | 'library';

  type Stage = {
    id: StageId;
    label: string;
    icon: Component;
    /** Real per-stage data exists for this stage. */
    real: boolean;
    /** Cumulative count processed by this stage (real stages only). */
    count: number | null;
    /** Sub-label under the stage name. */
    sub: string;
    /** Live pulse (real + running). */
    live: boolean;
    /** Flagged needs-attention (decide). */
    warn?: boolean;
  };

  let activeStage = $state<StageId>('match');

  const stages = $derived.by<Stage[]>(() => {
    const s = snap;
    const running = anyRunning;
    return [
      {
        id: 'scan',
        label: 'Scan',
        icon: ScanLine,
        real: true,
        count: s?.scanned ?? null,
        sub: running && rates.scan > 0 ? `${rates.scan.toFixed(0)}/s` : 'index',
        live: running && s?.scan?.status === 'Running'
      },
      {
        id: 'fingerprint',
        label: 'Fingerprint',
        icon: Disc3,
        real: true,
        count: s?.fingerprinted ?? null,
        sub: 'Chromaprint',
        live: running && s?.fingerprint?.status === 'Running'
      },
      {
        id: 'match',
        label: 'Match',
        icon: Tag,
        real: true,
        count: s?.enriched ?? null,
        sub: 'providers',
        live: running && s?.enrich?.status === 'Running'
      },
      {
        id: 'decide',
        label: 'Decide',
        icon: Wand2,
        real: true,
        count: decidedCount,
        sub: 'consensus',
        live: running && s?.enrich?.status === 'Running',
        warn: (tagReviewCount ?? 0) > 0
      },
      {
        id: 'grade',
        label: 'AI grade',
        icon: Sparkles,
        real: true,
        count: qualityGraded,
        sub: 'quality LLM',
        live: qualityProgress?.active === true
      },
      {
        id: 'dedupe',
        label: 'Dedupe',
        icon: Copy,
        real: true,
        count: duplicateCount,
        sub: 'fingerprint',
        live: running && s?.fingerprint?.status === 'Running'
      },
      {
        id: 'library',
        label: 'Library',
        icon: PackageCheck,
        real: true,
        count: s?.built ?? inLibrary,
        sub: 'destination',
        live: running && s?.build?.status === 'Running'
      }
    ];
  });

  const activeStageDef = $derived(stages.find((st) => st.id === activeStage) ?? stages[0]);

  // The biggest real per-stage count, used to scale the progress bars.
  const maxStageCount = $derived(
    Math.max(1, ...stages.filter((st) => st.real && st.count != null).map((st) => st.count ?? 0))
  );

  function barWidth(st: Stage): number {
    if (st.id === 'library') return 100;
    if (!st.real || st.count == null) return 0;
    return Math.min(95, (st.count / maxStageCount) * 95 + 5);
  }

  // Detail chips for the active stage. The SSE stream is count-only, so chips
  // reflect cumulative counts honestly rather than inventing file names. Each
  // stage surfaces the figures that are meaningful to it.
  type StageDetail = { lines: { label: string; value: string }[] };

  const activeStageDetail = $derived.by<StageDetail>(() => {
    const st = activeStageDef;
    const lines: { label: string; value: string }[] = [];

    if (st.id === 'decide') {
      if (decidedCount != null) lines.push({ label: 'Decided', value: decidedCount.toLocaleString() });
      if (matchedCount != null) lines.push({ label: 'Matched', value: matchedCount.toLocaleString() });
      if (tagReviewCount != null) lines.push({ label: 'To review', value: tagReviewCount.toLocaleString() });
      return { lines };
    }
    if (st.id === 'grade') {
      if (qualityGraded != null) lines.push({ label: 'Graded', value: qualityGraded.toLocaleString() });
      if (avgQuality != null) lines.push({ label: 'Avg score', value: avgQuality.toFixed(0) });
      if (aiFlaggedCount != null) lines.push({ label: 'Flagged', value: aiFlaggedCount.toLocaleString() });
      return { lines };
    }
    if (st.id === 'dedupe') {
      if (duplicateCount != null) lines.push({ label: 'Duplicates', value: duplicateCount.toLocaleString() });
      if (duplicates?.groups != null) lines.push({ label: 'Clusters', value: duplicates.groups.toLocaleString() });
      return { lines };
    }

    if (st.count != null) lines.push({ label: 'Processed', value: st.count.toLocaleString() });
    const rate =
      st.id === 'scan'
        ? rates.scan
        : st.id === 'fingerprint'
          ? rates.fingerprint
          : st.id === 'match'
            ? rates.enrich
            : rates.build;
    if (anyRunning && rate > 0) lines.push({ label: 'Throughput', value: `${rate.toFixed(1)}/s` });
    if (st.id === 'library' && inLibrary != null)
      lines.push({ label: 'In library', value: inLibrary.toLocaleString() });
    return { lines };
  });

  // ── live activity log (real RecentActivity from the overview poll) ──────────
  const recent = $derived<ApiOverviewActivity[]>(overview?.recentActivity ?? []);

  function activityTone(type: ApiOverviewActivity['type']): string {
    switch (type) {
      case 'failed':
        return 'text-red-500';
      case 'review':
        return 'text-amber-600 dark:text-amber-500';
      case 'copied':
      case 'enriched':
        return 'text-primary';
      default:
        return 'text-muted-foreground';
    }
  }

  // ── just landed (recently-added albums, reusing album-sections) ─────────────
  const justLanded = $derived.by<AlbumSummary[]>(() => {
    if (!loaded) return [];
    const built = songs.filter(isBuiltSong);
    if (built.length === 0) return [];
    return sortAlbumsByRecency(buildAlbumsFromSongs(built)).slice(0, 6);
  });

  function albumInitials(title: string): string {
    return (
      title
        .split(/\s+/)
        .filter((w) => /[a-z0-9]/i.test(w[0] ?? ''))
        .map((w) => w[0])
        .slice(0, 2)
        .join('')
        .toUpperCase() || '??'
    );
  }

  // ── helpers ──────────────────────────────────────────────────────────────────
  function fmtNum(n: number | null | undefined): string {
    return n == null ? '—' : n.toLocaleString();
  }
  function fmtBytes(bytes: number | null): string {
    if (bytes == null) return '';
    const gib = bytes / 1024 ** 3;
    if (gib >= 1) return `${gib.toFixed(0)} GB`;
    return `${(bytes / 1024 ** 2).toFixed(0)} MB`;
  }

  async function handleRescan() {
    if (rescanning) return;
    rescanning = true;
    try {
      const res = await triggerEnrichmentScan();
      if (res.ok) pipelineOverlay.setOpen(true);
    } finally {
      rescanning = false;
    }
  }

  // ── sub-nav ──────────────────────────────────────────────────────────────────
  const subNavTabs = [
    { id: 'conveyor', label: 'Conveyor', href: '/pipeline', icon: Disc3, live: true },
    { id: 'folders', label: 'By folder', href: '/directories', icon: undefined },
    { id: 'quality', label: 'AI quality', href: '/quality', icon: Sparkles }
  ];

  type NeedCard = {
    id: 'review' | 'dupes' | 'ai';
    icon: Component;
    count: number | null;
    label: string;
    body: string;
    cta: string;
    href: string;
    tone: 'review' | 'dupes' | 'ai';
  };

  const needCards = $derived<NeedCard[]>([
    {
      id: 'review',
      icon: Tag,
      count: tagReviewCount,
      label: 'Tag reviews',
      body: "Providers couldn't agree (or none scored high enough). Pick the right candidate or override the fields manually.",
      cta: 'Review',
      href: '/inbox',
      tone: 'review'
    },
    {
      id: 'dupes',
      icon: Copy,
      count: duplicateCount,
      label: 'Ambiguous duplicates',
      body: "Same fingerprint, can't auto-pick which to keep. Mutable keep-A/B resolution lands with the Inbox section.",
      cta: 'Compare',
      href: '/inbox',
      tone: 'dupes'
    },
    {
      id: 'ai',
      icon: Sparkles,
      count: aiFlaggedCount,
      label: 'AI flagged',
      body: 'Enrichment looks wrong to the quality grader (path mismatch, year drift, title rewritten). Worth a second look.',
      cta: 'Inspect',
      href: '/inbox',
      tone: 'ai'
    }
  ]);
</script>

<!-- Header -->
<header class="border-border flex shrink-0 items-end justify-between gap-4 border-b px-7 py-5">
  <div class="min-w-0">
    <div class="text-muted-foreground flex items-center gap-1.5 font-mono text-[10px] tracking-[0.12em] uppercase">
      <span
        class="bg-primary inline-flex size-1.5 rounded-full"
        class:mh-v2-subdot={anyRunning}
        aria-hidden="true"
      ></span>
      {anyRunning ? 'Live · pipeline running' : 'Pipeline · idle'}
    </div>
    <h1 class="mt-1 text-2xl font-semibold tracking-tight">Pipeline</h1>
    <p class="text-muted-foreground mt-1 max-w-2xl text-xs">
      Files flow left-to-right: scanned, fingerprinted, matched against providers, decided on,
      graded by AI, deduped, then landed in your library. Anything that needs a human surfaces in
      <a href="/inbox" class="text-primary hover:underline">Inbox</a>.
    </p>
  </div>
  <div class="flex shrink-0 items-center gap-2">
    <button
      type="button"
      onclick={handleRescan}
      disabled={rescanning || anyRunning}
      class="border-border bg-card hover:bg-muted text-foreground inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12.5px] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50"
    >
      {#if rescanning}
        <Loader2 class="size-3.5 animate-spin" />
      {:else}
        <RefreshCw class="size-3.5" />
      {/if}
      Rescan
    </button>
  </div>
</header>

<PipelineSubNavV2 tabs={subNavTabs} active="conveyor" running={anyRunning} />

<ScrollArea class="min-h-0 flex-1">
  <div class="flex flex-col gap-6 px-7 py-6">
    <!-- KPI row -->
    <div class="grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-6">
      <!-- Source -->
      <div class="border-border bg-card rounded-lg border p-3.5">
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">Source</div>
        {#if sourceTotal == null}
          <Skeleton class="mt-1.5 h-7 w-16" />
        {:else}
          <div class="mt-0.5 font-mono text-2xl font-semibold tabular-nums">{fmtNum(sourceTotal)}</div>
        {/if}
        <div class="text-muted-foreground mt-0.5 text-[11px]">
          files{sourceBytes != null ? ` · ${fmtBytes(sourceBytes)}` : ''}
        </div>
      </div>

      <!-- In library -->
      <div class="border-border bg-card rounded-lg border p-3.5">
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">In library</div>
        {#if inLibrary == null}
          <Skeleton class="mt-1.5 h-7 w-16" />
        {:else}
          <div class="mt-0.5 font-mono text-2xl font-semibold tabular-nums text-primary">{fmtNum(inLibrary)}</div>
        {/if}
        <div class="text-muted-foreground mt-0.5 text-[11px]">
          {enrichedPct != null ? `${enrichedPct.toFixed(1)}% enriched` : 'enriched'}
        </div>
      </div>

      <!-- In flight -->
      <div class="border-border bg-card relative overflow-hidden rounded-lg border p-3.5">
        <span class="bg-primary absolute inset-y-0 left-0 w-[3px]" aria-hidden="true"></span>
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">In flight</div>
        {#if !loaded}
          <Skeleton class="mt-1.5 h-7 w-12" />
        {:else}
          <div class="mt-0.5 font-mono text-2xl font-semibold tabular-nums text-primary">{inFlight.toLocaleString()}</div>
        {/if}
        <div class="text-muted-foreground mt-0.5 text-[11px]">
          {anyRunning ? 'mid-pipeline' : 'idle'}
        </div>
      </div>

      <!-- Awaiting you -->
      <a
        href="/inbox"
        class="relative block overflow-hidden rounded-lg border border-amber-500/40 bg-amber-500/5 p-3.5 transition-colors hover:bg-amber-500/10"
      >
        <span class="absolute inset-y-0 left-0 w-[3px] bg-amber-500" aria-hidden="true"></span>
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">Awaiting you</div>
        {#if awaitingYou == null}
          <Skeleton class="mt-1.5 h-7 w-12" />
        {:else}
          <div class="mt-0.5 font-mono text-2xl font-semibold tabular-nums text-amber-600 dark:text-amber-500">
            {fmtNum(awaitingYou)}
          </div>
        {/if}
        <div class="text-muted-foreground mt-0.5 text-[11px]">
          {fmtNum(tagReviewCount)} tags · {aiFlaggedCount != null ? `${aiFlaggedCount} AI` : '— AI'}
        </div>
        <div class="text-amber-600 dark:text-amber-500 mt-1 inline-flex items-center gap-0.5 text-[11px] font-medium">
          Open Inbox <ChevronRight class="size-3" />
        </div>
      </a>

      <!-- Avg quality -->
      <div class="border-border bg-card rounded-lg border p-3.5">
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">Avg quality</div>
        {#if avgQuality == null}
          {#if loaded}
            <div class="mt-0.5 font-mono text-2xl font-semibold tabular-nums text-muted-foreground">—</div>
          {:else}
            <Skeleton class="mt-1.5 h-7 w-14" />
          {/if}
        {:else}
          <div class="mt-0.5 font-mono text-2xl font-semibold tabular-nums">{avgQuality.toFixed(1)}</div>
        {/if}
        <div class="text-muted-foreground mt-0.5 text-[11px]">
          {qualityGraded != null && qualityGraded > 0 ? `AI score · ${qualityGraded.toLocaleString()} graded` : 'AI score'}
        </div>
      </div>

      <!-- Errors -->
      <div class="border-border bg-card rounded-lg border p-3.5">
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">Errors</div>
        {#if errorCount == null}
          <Skeleton class="mt-1.5 h-7 w-10" />
        {:else}
          <div class={cn('mt-0.5 font-mono text-2xl font-semibold tabular-nums', errorCount > 0 ? 'text-red-500' : '')}>
            {fmtNum(errorCount)}
          </div>
        {/if}
        <div class="text-muted-foreground mt-0.5 text-[11px]">failed enrichment</div>
      </div>
    </div>

    <!-- Conveyor -->
    <section class="border-border bg-card rounded-lg border">
      <div class="border-border flex items-baseline gap-2 border-b px-4 py-3">
        <span class="text-[13px] font-semibold">Conveyor</span>
        <span class="text-muted-foreground text-[11.5px]">Click a stage to see what's flowing through it.</span>
      </div>

      <div class="p-3">
        <div class="grid grid-cols-2 gap-2 sm:grid-cols-4 lg:grid-cols-7">
          {#each stages as st, i (st.id)}
            {@const isActive = activeStage === st.id}
            {@const Icon = st.icon}
            <button
              type="button"
              onclick={() => (activeStage = st.id)}
              data-active={isActive || undefined}
              class={cn(
                'group relative flex flex-col gap-1.5 rounded-md border p-2.5 text-left transition-colors',
                isActive ? 'border-primary/40 bg-primary/5' : 'border-border bg-background hover:bg-muted/60',
                !st.real && 'border-dashed'
              )}
            >
              <div class="flex items-center justify-between">
                <span
                  class={cn(
                    'grid size-6 place-items-center rounded-md',
                    st.live ? 'bg-primary/15 text-primary' : 'bg-muted text-muted-foreground'
                  )}
                >
                  <Icon class="size-3.5" />
                </span>
                <span class="text-muted-foreground/60 font-mono text-[10px]">{String(i + 1).padStart(2, '0')}</span>
              </div>
              <div class="flex items-center gap-1 text-[12.5px] font-medium">
                {st.label}
                {#if st.live}
                  <span class="bg-primary mh-v2-subdot size-1.5 rounded-full"></span>
                {/if}
              </div>
              <div class="text-muted-foreground flex items-center gap-1 text-[10.5px]">
                <span class="truncate">{st.sub}</span>
              </div>
              <div class="mt-0.5 font-mono text-base font-semibold tabular-nums">
                {#if st.real && st.count != null}
                  {st.count.toLocaleString()}
                  <span class="text-muted-foreground text-[10px] font-normal">{st.id === 'library' ? 'tracks' : 'done'}</span>
                {:else}
                  <span class="text-muted-foreground text-sm">—</span>
                {/if}
              </div>
              <div class="bg-border h-[3px] overflow-hidden rounded-full">
                <div
                  class={cn('h-full transition-[width] duration-500', st.warn ? 'bg-amber-500' : 'bg-primary')}
                  style="width: {barWidth(st)}%;"
                ></div>
              </div>
            </button>
          {/each}
        </div>

        <!-- Active-stage detail -->
        <div class="border-border bg-muted/30 mt-3 rounded-md border p-3">
          <div class="text-muted-foreground mb-2 flex items-center gap-1.5 text-[11px] font-semibold tracking-wide uppercase">
            {#if activeStageDef.warn}
              <AlertTriangle class="size-3.5 text-amber-500" />
            {:else}
              <CheckCheck class="size-3.5" />
            {/if}
            {activeStageDef.label}
          </div>
          {#if activeStageDetail.lines.length === 0}
            <p class="text-muted-foreground text-[12px]">Nothing in this stage right now.</p>
          {:else}
            <div class="flex flex-wrap gap-2">
              {#each activeStageDetail.lines as line (line.label)}
                <span
                  class="border-border bg-card inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11.5px]"
                >
                  <span class="text-muted-foreground">{line.label}</span>
                  <span class="font-mono font-medium tabular-nums">{line.value}</span>
                </span>
              {/each}
            </div>
          {/if}
        </div>
      </div>
    </section>

    <!-- Needs you -->
    <section>
      <div class="mb-2.5 flex items-baseline gap-2">
        <span class="text-[13px] font-semibold">Needs you</span>
        <span class="text-muted-foreground text-[11.5px]">When the pipeline can't decide, items pile up here.</span>
        <a
          href="/inbox"
          class="text-primary ml-auto inline-flex items-center gap-0.5 text-[12px] font-medium hover:underline"
        >
          Open Inbox <ChevronRight class="size-3" />
        </a>
      </div>
      <div class="grid grid-cols-1 gap-3 md:grid-cols-3">
        {#each needCards as card (card.id)}
          {@const Icon = card.icon}
          <a
            href={card.href}
            class="border-border bg-card hover:border-primary/40 group flex flex-col rounded-lg border p-4 transition-colors"
          >
            <div class="flex items-center gap-2.5">
              <span
                class={cn(
                  'grid size-8 place-items-center rounded-md',
                  card.tone === 'review' && 'bg-amber-500/15 text-amber-600 dark:text-amber-500',
                  card.tone === 'dupes' && 'bg-sky-500/15 text-sky-600 dark:text-sky-400',
                  card.tone === 'ai' && 'bg-primary/15 text-primary'
                )}
              >
                <Icon class="size-4" />
              </span>
              <div>
                {#if card.count == null}
                  <Skeleton class="h-5 w-8" />
                {:else}
                  <div class="font-mono text-lg font-semibold leading-none tabular-nums">{card.count.toLocaleString()}</div>
                {/if}
                <div class="text-muted-foreground mt-0.5 text-[11.5px]">{card.label}</div>
              </div>
            </div>
            <p class="text-muted-foreground mt-2.5 flex-1 text-[12px] leading-relaxed">{card.body}</p>
            <span class="text-primary mt-2.5 inline-flex items-center gap-0.5 text-[12px] font-medium">
              {card.cta} <ChevronRight class="size-3" />
            </span>
          </a>
        {/each}
      </div>
    </section>

    <!-- Live activity + Just landed -->
    <div class="grid grid-cols-1 gap-3 lg:grid-cols-2">
      <!-- Live activity -->
      <section class="border-border bg-card flex min-h-0 flex-col rounded-lg border">
        <div class="border-border flex items-center gap-2 border-b px-4 py-2.5">
          <span class="text-[12.5px] font-semibold">Live activity</span>
          <span class="text-muted-foreground text-[11px]">recent events</span>
          <span class="text-primary ml-auto font-mono text-[10.5px]">tail -f</span>
        </div>
        <div class="max-h-72 min-h-0 overflow-y-auto px-3 py-2">
          {#if recent.length > 0}
            <div class="space-y-0.5">
              {#each recent as a (a.id)}
                <div class="grid grid-cols-[auto_64px_1fr] items-center gap-2 py-[3px] font-mono text-[11px]">
                  <span class="text-muted-foreground/70 tabular-nums">{a.time}</span>
                  <span class={cn('truncate font-semibold tracking-wide uppercase text-[9.5px]', activityTone(a.type))}>{a.type}</span>
                  <span class="text-muted-foreground truncate">
                    {a.track}{a.artist ? ` — ${a.artist}` : ''}
                  </span>
                </div>
              {/each}
            </div>
          {:else}
            <p class="text-muted-foreground/70 px-1 py-8 text-center font-mono text-[11px]">
              No recent activity yet.
            </p>
          {/if}
        </div>
      </section>

      <!-- Just landed -->
      <section class="border-border bg-card flex min-h-0 flex-col rounded-lg border">
        <div class="border-border flex items-center gap-2 border-b px-4 py-2.5">
          <span class="text-[12.5px] font-semibold">Just landed</span>
          <span class="text-muted-foreground text-[11px]">in library</span>
          <a href="/library" class="text-primary ml-auto inline-flex items-center gap-0.5 text-[11.5px] font-medium hover:underline">
            All <ChevronRight class="size-3" />
          </a>
        </div>
        <div class="max-h-72 min-h-0 overflow-y-auto p-2.5">
          {#if !loaded}
            <div class="space-y-1.5">
              {#each Array(4) as _, i (i)}
                <Skeleton class="h-12 w-full" />
              {/each}
            </div>
          {:else if justLanded.length > 0}
            <div class="space-y-1">
              {#each justLanded as album (album.key)}
                {@const firstSong = album.songs[0]}
                <div class="hover:bg-muted/60 group/row flex w-full items-center gap-2.5 rounded-md p-1.5 transition-colors">
                  <button
                    type="button"
                    onclick={() => goto(`/library?album=${encodeURIComponent(album.key)}`)}
                    class="flex min-w-0 flex-1 items-center gap-2.5 text-left"
                    title="Open album"
                  >
                    {#if album.coverUrl}
                      <img src={album.coverUrl} alt="" class="size-9 shrink-0 rounded object-cover" />
                    {:else}
                      <span class="bg-gradient-to-br from-cyan-700/80 to-cyan-300/80 grid size-9 shrink-0 place-items-center rounded text-[10px] font-semibold text-white">
                        {albumInitials(album.title)}
                      </span>
                    {/if}
                    <div class="min-w-0 flex-1">
                      <div class="truncate text-[12.5px] font-medium">{album.title}</div>
                      <div class="text-muted-foreground truncate text-[11px]">
                        {album.artist}{album.year ? ` · ${album.year}` : ''}
                      </div>
                    </div>
                  </button>
                  {#if firstSong}
                    <a
                      href={`/track/${firstSong.id}`}
                      title="View enrichment timeline"
                      class="text-muted-foreground/70 hover:text-primary hover:border-primary/40 border-border inline-flex shrink-0 items-center gap-1 rounded-md border px-1.5 py-1 text-[10.5px] font-medium opacity-0 transition-colors group-hover/row:opacity-100 focus-visible:opacity-100"
                    >
                      <History class="size-3" /> Timeline
                    </a>
                  {/if}
                  <Clock class="text-muted-foreground/60 size-3.5 shrink-0" />
                  <ChevronRight class="text-muted-foreground/60 size-3.5 shrink-0" />
                </div>
              {/each}
            </div>
          {:else}
            <p class="text-muted-foreground/70 px-1 py-8 text-center text-[12px]">
              Nothing in the library yet.
            </p>
          {/if}
        </div>
      </section>
    </div>
  </div>
</ScrollArea>

<style>
  :global(.mh-v2-subdot) {
    box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    animation: mh-v2-subdot 2s infinite;
  }
  @keyframes mh-v2-subdot {
    0% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    }
    70% {
      box-shadow: 0 0 0 5px oklch(0.5 0.17 145 / 0);
    }
    100% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0);
    }
  }
</style>

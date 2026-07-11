<script lang="ts">
  import { RefreshCw, ChevronRight, Sparkles, Tag, Copy, Loader2, History } from '@lucide/svelte';
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

  // The demo account is read-only — hide the mutating Rescan control (the backend rejects it
  // regardless, this just avoids a dead button). Defaults false so non-demo callers are unaffected.
  let { isDemo = false }: { isDemo?: boolean } = $props();

  // ── data layer (reuses the existing api-client + album-sections) ───────────
  let stats = $state<ApiStats | null>(null);
  let songs = $state<ApiSong[]>([]);
  let quality = $state<QualityOverview | null>(null);
  let qualityProgress = $state<QualityProgress | null>(null);
  let duplicates = $state<DuplicatesResponse | null>(null);
  let loaded = $state(false);
  let loadError = $state(false);
  let rescanning = $state(false);

  // Guard against overlapping polls: during an active scan the API is busy and a
  // full refresh (which includes the entire song list) can take longer than the
  // poll interval. Without this, ticks stack up faster than they drain, saturating
  // the same-origin proxy — whose 10s header-timeout then aborts the in-flight
  // requests (surfacing as the misleading "access control checks" console error)
  // and bogs down the page. Skipping a tick while one is in flight makes the
  // cadence adaptively back off under load.
  let loadInFlight = false;
  async function loadAll() {
    if (loadInFlight) return;
    loadInFlight = true;
    try {
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
      // Quality failures stay silent (may be legitimately unconfigured); the core
      // fetches failing means the KPIs below are stale/missing, so say so.
      loadError =
        stRes.status === 'rejected' ||
        songsRes.status === 'rejected' ||
        dupRes.status === 'rejected';
      loaded = true;
    } finally {
      loadInFlight = false;
    }
  }

  $effect(() => {
    void loadAll();
    // Re-poll on the same cadence as the rest of the app so counts stay fresh
    // while a job runs. The SSE-backed conveyor updates independently, and the
    // in-flight guard above keeps a slow refresh from stacking up.
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
  // otherwise from the DB-backed overview (tracksEnriched = Matched || NeedsReview).
  const decidedCount = $derived.by(() => {
    if (anyRunning && snap) return (snap.enriched ?? 0) + (snap.needsReview ?? 0);
    return overview?.job.tracksEnriched ?? null;
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
    /** Cumulative count processed by this stage. */
    count: number | null;
    /** Stage is actively processing right now. */
    live: boolean;
    /** Live throughput while running (files/s), null when idle. */
    rate: number | null;
  };

  let activeStage = $state<StageId>('match');

  /** A conveyor stage's count: the live per-run figure while the stage is actively
   *  running (so the count climbs in real time), otherwise the stable library-wide total
   *  from the DB-backed overview poll — which survives restarts and stays coherent. The
   *  live snapshot value is 0 (not null) for stages that didn't run this session, so a
   *  plain `?? total` fallback would never fire; this picks deliberately. */
  function stageCount(
    live: number | null | undefined,
    total: number | null | undefined,
    running: boolean
  ): number | null {
    if (running && live != null) return live;
    return total ?? live ?? null;
  }

  const stages = $derived.by<Stage[]>(() => {
    const s = snap;
    const running = anyRunning;
    const job = overview?.job;
    return [
      {
        id: 'scan',
        label: 'Scan',
        count: stageCount(s?.scanned, job?.tracksProcessed, running && s?.scan?.status === 'Running'),
        live: running && s?.scan?.status === 'Running',
        rate: running && rates.scan > 0 ? rates.scan : null
      },
      {
        id: 'fingerprint',
        label: 'Fingerprint',
        count: stageCount(
          s?.fingerprinted,
          job?.tracksFingerprinted,
          running && s?.fingerprint?.status === 'Running'
        ),
        live: running && s?.fingerprint?.status === 'Running',
        rate: running && rates.fingerprint > 0 ? rates.fingerprint : null
      },
      {
        id: 'match',
        label: 'Match',
        count: stageCount(s?.enriched, job?.tracksBuildEligible, running && s?.enrich?.status === 'Running'),
        live: running && s?.enrich?.status === 'Running',
        rate: running && rates.enrich > 0 ? rates.enrich : null
      },
      {
        id: 'decide',
        label: 'Decide',
        count: decidedCount,
        live: running && s?.enrich?.status === 'Running',
        rate: null
      },
      {
        id: 'grade',
        label: 'AI grade',
        count: qualityGraded,
        live: qualityProgress?.active === true,
        rate: null
      },
      {
        id: 'dedupe',
        label: 'Dedupe',
        count: duplicateCount,
        live: running && s?.fingerprint?.status === 'Running',
        rate: null
      },
      {
        id: 'library',
        label: 'Library',
        count: stageCount(s?.built, job?.tracksCopied ?? inLibrary, running && s?.build?.status === 'Running'),
        live: running && s?.build?.status === 'Running',
        rate: running && rates.build > 0 ? rates.build : null
      }
    ];
  });

  const activeStageDef = $derived(stages.find((st) => st.id === activeStage) ?? stages[0]);

  // Detail figures for the selected stage. The SSE stream is count-only, so the
  // detail reflects cumulative counts honestly rather than inventing file names.
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

  // ── recent activity (real RecentActivity from the overview poll) ────────────
  const recent = $derived<ApiOverviewActivity[]>(overview?.recentActivity ?? []);

  // Sentence-case verbs + one small dot per event type. Green for progress,
  // amber only for the actionable review state, red only for failures.
  const ACTIVITY_VERB: Record<ApiOverviewActivity['type'], string> = {
    discovered: 'Discovered',
    copied: 'Added to library',
    enriched: 'Matched',
    review: 'Needs review',
    failed: 'Failed'
  };

  function activityDot(type: ApiOverviewActivity['type']): string {
    switch (type) {
      case 'failed':
        return 'bg-destructive';
      case 'review':
        return 'bg-amber-500';
      case 'enriched':
      case 'copied':
        return 'bg-primary';
      default:
        return 'bg-muted-foreground/40';
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

  type NeedRow = {
    id: 'review' | 'dupes' | 'ai';
    icon: Component;
    count: number | null;
    label: string;
    body: string;
    href: string;
  };

  const needRows = $derived<NeedRow[]>([
    {
      id: 'review',
      icon: Tag,
      count: tagReviewCount,
      label: 'Tag reviews',
      body: "Providers couldn't agree — pick the right candidate or fix the fields yourself.",
      href: '/inbox'
    },
    {
      id: 'dupes',
      icon: Copy,
      count: duplicateCount,
      label: 'Ambiguous duplicates',
      body: "Same fingerprint, can't auto-pick which copy to keep.",
      href: '/inbox'
    },
    {
      id: 'ai',
      icon: Sparkles,
      count: aiFlaggedCount,
      label: 'AI flagged',
      body: 'The quality grader thinks these matches look wrong — worth a second look.',
      href: '/inbox'
    }
  ]);
</script>

<!-- Header -->
<header
  class="border-border flex shrink-0 flex-col gap-3 border-b px-4 py-4 sm:flex-row sm:items-end sm:justify-between sm:gap-4 sm:px-7 sm:py-5"
>
  <div class="min-w-0">
    <h1 class="flex items-center gap-2.5 text-2xl font-semibold tracking-tight sm:text-[28px]">
      Pipeline
      {#if anyRunning}
        <span class="bg-primary mh-v2-pulse mt-0.5 size-2 rounded-full" aria-hidden="true"></span>
        <span class="sr-only">running</span>
      {/if}
    </h1>
    <p class="text-muted-foreground mt-1 max-w-xl text-[13px]">
      {#if anyRunning}
        Running — files are flowing through scan, match, grade, and build.
      {:else}
        Watches your source folder, matches every file, and builds a clean library. Anything that
        needs a human lands in <a href="/inbox" class="text-primary hover:underline">Inbox</a>.
      {/if}
    </p>
  </div>
  <div class="flex w-full shrink-0 items-center justify-end gap-2 sm:w-auto">
    {#if !isDemo}
      <button
        type="button"
        onclick={handleRescan}
        disabled={rescanning || anyRunning}
        class="border-border bg-card hover:bg-muted text-foreground inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12.5px] font-medium transition-[background-color,transform] duration-150 ease-out active:scale-[0.97] disabled:cursor-not-allowed disabled:opacity-50"
      >
        {#if rescanning}
          <Loader2 class="size-3.5 animate-spin" />
        {:else}
          <RefreshCw class="size-3.5" />
        {/if}
        Rescan
      </button>
    {/if}
  </div>
</header>

<ScrollArea class="min-h-0 flex-1">
  <div class="flex flex-col gap-10 px-4 py-7 sm:px-7">
    {#if loadError}
      <div class="flex items-center gap-2 text-[12.5px]">
        <span class="bg-destructive size-1.5 shrink-0 rounded-full" aria-hidden="true"></span>
        <span class="text-muted-foreground flex-1">
          Some pipeline data failed to load — figures below may be missing or stale.
        </span>
        <button
          type="button"
          onclick={() => void loadAll()}
          class="text-foreground shrink-0 font-medium hover:underline"
        >
          Retry
        </button>
      </div>
    {/if}

    <!-- Stat strip — typographic, unboxed. Desktop: hairline-divided row. -->
    <section aria-label="Pipeline stats">
      <div class="divide-border hidden items-stretch divide-x sm:flex">
        <div class="min-w-0 flex-1 pr-6">
          {#if sourceTotal == null && !loaded}
            <Skeleton class="h-9 w-16" />
          {:else}
            <div class={cn('text-[28px] leading-9 font-semibold tracking-tight tabular-nums', sourceTotal == null && 'text-muted-foreground')}>
              {fmtNum(sourceTotal)}
            </div>
          {/if}
          <div class="text-muted-foreground mt-1 text-[12.5px]">
            Source files{sourceBytes != null ? ` · ${fmtBytes(sourceBytes)}` : ''}
          </div>
        </div>

        <div class="min-w-0 flex-1 px-6">
          {#if inLibrary == null}
            <Skeleton class="h-9 w-16" />
          {:else}
            <div class="text-[28px] leading-9 font-semibold tracking-tight tabular-nums">{fmtNum(inLibrary)}</div>
          {/if}
          <div class="text-muted-foreground mt-1 truncate text-[12.5px]">
            In library{enrichedPct != null ? ` · ${enrichedPct.toFixed(0)}% enriched` : ''}
          </div>
        </div>

        <div class="min-w-0 flex-1 px-6">
          {#if !loaded}
            <Skeleton class="h-9 w-12" />
          {:else}
            <div class="text-[28px] leading-9 font-semibold tracking-tight tabular-nums">{inFlight.toLocaleString()}</div>
          {/if}
          <div class="text-muted-foreground mt-1 text-[12.5px]">In flight</div>
        </div>

        <!-- The only interactive stat: explicit chevron affordance, links to Inbox. -->
        <a
          href="/inbox"
          class="group focus-visible:ring-ring min-w-0 flex-1 rounded-sm px-6 focus-visible:ring-2 focus-visible:outline-none"
        >
          {#if awaitingYou == null}
            <Skeleton class="h-9 w-12" />
          {:else}
            <div
              class={cn(
                'text-[28px] leading-9 font-semibold tracking-tight tabular-nums',
                awaitingYou > 0 && 'text-amber-600 dark:text-amber-500'
              )}
            >
              {fmtNum(awaitingYou)}
            </div>
          {/if}
          <div class="text-muted-foreground group-hover:text-foreground mt-1 flex items-center gap-0.5 text-[12.5px] transition-colors">
            Awaiting you <ChevronRight class="size-3" />
          </div>
        </a>

        <div class="min-w-0 flex-1 px-6">
          {#if avgQuality == null && !loaded}
            <Skeleton class="h-9 w-14" />
          {:else}
            <div class={cn('text-[28px] leading-9 font-semibold tracking-tight tabular-nums', avgQuality == null && 'text-muted-foreground')}>
              {avgQuality == null ? '—' : avgQuality.toFixed(1)}
            </div>
          {/if}
          <div class="text-muted-foreground mt-1 text-[12.5px]">
            Avg quality{qualityGraded != null && qualityGraded > 0 ? ` · ${qualityGraded.toLocaleString()} graded` : ''}
          </div>
        </div>

        <div class="min-w-0 flex-1 pl-6">
          {#if errorCount == null}
            <Skeleton class="h-9 w-10" />
          {:else}
            <div class={cn('text-[28px] leading-9 font-semibold tracking-tight tabular-nums', errorCount > 0 && 'text-destructive')}>
              {fmtNum(errorCount)}
            </div>
          {/if}
          <div class="text-muted-foreground mt-1 text-[12.5px]">Errors</div>
        </div>
      </div>

      <!-- Mobile: compact two-line summary instead of stacked boxes. -->
      <div class="text-muted-foreground space-y-1 text-[13px] leading-6 sm:hidden">
        {#if !loaded}
          <Skeleton class="h-5 w-56" />
          <Skeleton class="h-5 w-40" />
        {:else}
          <p>
            <span class="text-foreground font-semibold tabular-nums">{fmtNum(sourceTotal)}</span> files ·
            <span class="text-foreground font-semibold tabular-nums">{fmtNum(inLibrary)}</span> in library{enrichedPct != null
              ? ` · ${enrichedPct.toFixed(0)}% enriched`
              : ''}
          </p>
          <p class="flex items-center gap-1">
            <a href="/inbox" class="inline-flex items-center gap-0.5 font-medium">
              <span
                class={cn(
                  'font-semibold tabular-nums',
                  (awaitingYou ?? 0) > 0 ? 'text-amber-600 dark:text-amber-500' : 'text-foreground'
                )}>{fmtNum(awaitingYou)}</span
              >
              <span class="text-muted-foreground font-normal">awaiting you</span>
              <ChevronRight class="size-3" />
            </a>
            <span>·</span>
            <span
              ><span class={cn('text-foreground font-semibold tabular-nums', (errorCount ?? 0) > 0 && 'text-destructive')}
                >{fmtNum(errorCount)}</span
              > errors</span
            >
          </p>
        {/if}
      </div>
    </section>

    <!-- Conveyor — one connected flow on the page background. -->
    <section aria-label="Conveyor">
      <div class="mb-5 flex items-baseline gap-2">
        <h2 class="text-[13px] font-semibold">Conveyor</h2>
        <span class="text-muted-foreground text-[12px]">Select a stage for detail.</span>
      </div>

      <!-- Desktop: horizontal flow, nodes joined by a continuous line. -->
      <div class="relative hidden sm:block">
        <div
          class="bg-border absolute top-[5px] h-px"
          style="left: calc(100% / 14); right: calc(100% / 14);"
          aria-hidden="true"
        ></div>
        <div class="grid grid-cols-7">
          {#each stages as st (st.id)}
            {@const isActive = activeStage === st.id}
            <button
              type="button"
              onclick={() => (activeStage = st.id)}
              aria-pressed={isActive}
              class="group focus-visible:ring-ring flex flex-col items-center gap-2.5 rounded-md pb-1 text-center transition-transform duration-100 ease-out focus-visible:ring-2 focus-visible:outline-none active:scale-[0.97]"
            >
              <span
                class={cn(
                  'relative z-10 size-[11px] rounded-full transition-colors',
                  st.count != null && st.count > 0 ? 'bg-primary' : 'bg-muted-foreground/30',
                  st.live && 'ring-primary/20 ring-4'
                )}
                aria-hidden="true"
              ></span>
              <span
                class={cn(
                  'max-w-full truncate px-1 text-[13px] leading-tight transition-colors',
                  isActive ? 'text-foreground font-semibold' : 'text-muted-foreground group-hover:text-foreground font-medium'
                )}
              >
                {st.label}
                <span class="text-muted-foreground font-normal tabular-nums">
                  {st.count == null ? '—' : st.count.toLocaleString()}
                </span>
              </span>
              {#if st.live && st.rate != null}
                <span class="text-muted-foreground -mt-1.5 text-[11px] tabular-nums">{st.rate.toFixed(0)}/s</span>
              {/if}
            </button>
          {/each}
        </div>
      </div>

      <!-- Mobile: same flow, vertical timeline. -->
      <div class="relative sm:hidden">
        <div class="bg-border absolute top-3 bottom-3 left-[5px] w-px" aria-hidden="true"></div>
        <div class="flex flex-col">
          {#each stages as st (st.id)}
            {@const isActive = activeStage === st.id}
            <button
              type="button"
              onclick={() => (activeStage = st.id)}
              aria-pressed={isActive}
              class="group focus-visible:ring-ring flex items-center gap-3 rounded-md py-2 text-left focus-visible:ring-2 focus-visible:outline-none"
            >
              <span
                class={cn(
                  'relative z-10 size-[11px] shrink-0 rounded-full transition-colors',
                  st.count != null && st.count > 0 ? 'bg-primary' : 'bg-muted-foreground/30',
                  st.live && 'ring-primary/20 ring-4'
                )}
                aria-hidden="true"
              ></span>
              <span
                class={cn(
                  'flex-1 truncate text-[13px]',
                  isActive ? 'text-foreground font-semibold' : 'text-muted-foreground font-medium'
                )}
              >
                {st.label}
              </span>
              {#if st.live && st.rate != null}
                <span class="text-muted-foreground text-[11px] tabular-nums">{st.rate.toFixed(0)}/s</span>
              {/if}
              <span class="text-muted-foreground text-[13px] tabular-nums">
                {st.count == null ? '—' : st.count.toLocaleString()}
              </span>
            </button>
          {/each}
        </div>
      </div>

      <!-- Selected-stage detail — plain inline figures, no box. -->
      <div class="border-border mt-5 border-t pt-3">
        {#if activeStageDetail.lines.length === 0}
          <p class="text-muted-foreground text-[12.5px]">
            <span class="text-foreground font-medium">{activeStageDef.label}</span>
            — nothing in this stage right now.
          </p>
        {:else}
          <p class="text-muted-foreground text-[12.5px]">
            <span class="text-foreground font-medium">{activeStageDef.label}</span>
            {#each activeStageDetail.lines as line, i (line.label)}
              <span class="text-muted-foreground/60" aria-hidden="true">{i === 0 ? ' — ' : ' · '}</span
              >{line.label}
              <span
                class={cn(
                  'font-medium tabular-nums',
                  line.label === 'To review' && line.value !== '0'
                    ? 'text-amber-600 dark:text-amber-500'
                    : 'text-foreground'
                )}>{line.value}</span
              >
            {/each}
          </p>
        {/if}
      </div>
    </section>

    <!-- Needs you — a divided list, not widget boxes. -->
    <section aria-label="Needs you">
      <div class="mb-1.5 flex flex-wrap items-baseline gap-2">
        <h2 class="text-[13px] font-semibold">Needs you</h2>
        <span class="text-muted-foreground text-[12px]">When the pipeline can't decide, items pile up here.</span>
        <a
          href="/inbox"
          class="text-primary ml-auto inline-flex items-center gap-0.5 text-[12px] font-medium hover:underline"
        >
          Open Inbox <ChevronRight class="size-3" />
        </a>
      </div>
      <div class="divide-border divide-y">
        {#each needRows as row (row.id)}
          {@const Icon = row.icon}
          <a
            href={row.href}
            class="group hover:bg-muted/50 focus-visible:ring-ring -mx-3 flex items-center gap-3.5 rounded-lg px-3 py-3 transition-colors focus-visible:ring-2 focus-visible:outline-none"
          >
            <Icon class="text-muted-foreground size-4 shrink-0" />
            <div class="min-w-0 flex-1">
              <div class="text-[13px] font-medium">{row.label}</div>
              <div class="text-muted-foreground truncate text-[12px]">{row.body}</div>
            </div>
            {#if row.count == null}
              {#if loaded}
                <span class="text-muted-foreground text-sm tabular-nums">—</span>
              {:else}
                <Skeleton class="h-4 w-6" />
              {/if}
            {:else}
              <span class={cn('text-sm font-semibold tabular-nums', row.count === 0 && 'text-muted-foreground font-normal')}>
                {row.count.toLocaleString()}
              </span>
            {/if}
            <ChevronRight class="text-muted-foreground/50 group-hover:text-muted-foreground size-3.5 shrink-0 transition-colors" />
          </a>
        {/each}
      </div>
    </section>

    <!-- Recent activity + Just landed -->
    <div class="grid grid-cols-1 gap-10 lg:grid-cols-2 lg:gap-12">
      <!-- Recent activity — plain sentence-case rows, one dot per event type. -->
      <section aria-label="Recent activity" class="min-w-0">
        <div class="mb-1.5 flex items-baseline gap-2">
          <h2 class="text-[13px] font-semibold">Recent activity</h2>
        </div>
        <div class="max-h-72 min-h-0 overflow-y-auto">
          {#if recent.length > 0}
            <div class="divide-border/60 divide-y">
              {#each recent as a (a.id)}
                <div class="flex items-center gap-2.5 py-[7px] text-[12.5px]">
                  <span class={cn('size-1.5 shrink-0 rounded-full', activityDot(a.type))} aria-hidden="true"></span>
                  <span class="min-w-0 flex-1 truncate">
                    <span class="font-medium">{ACTIVITY_VERB[a.type] ?? a.type}</span>
                    <span class="text-muted-foreground"> — {a.track}{a.artist ? ` · ${a.artist}` : ''}</span>
                  </span>
                  <span class="text-muted-foreground/70 shrink-0 text-[11.5px]">{a.time}</span>
                </div>
              {/each}
            </div>
          {:else}
            <p class="text-muted-foreground/70 py-8 text-center text-[12.5px]">No recent activity yet.</p>
          {/if}
        </div>
      </section>

      <!-- Just landed -->
      <section aria-label="Just landed" class="min-w-0">
        <div class="mb-1.5 flex items-baseline gap-2">
          <h2 class="text-[13px] font-semibold">Just landed</h2>
          <a href="/library" class="text-primary ml-auto inline-flex items-center gap-0.5 text-[12px] font-medium hover:underline">
            All <ChevronRight class="size-3" />
          </a>
        </div>
        <div class="max-h-72 min-h-0 overflow-y-auto">
          {#if !loaded}
            <div class="space-y-1.5">
              {#each Array(4) as _, i (i)}
                <Skeleton class="h-12 w-full" />
              {/each}
            </div>
          {:else if justLanded.length > 0}
            <div class="divide-border/60 divide-y">
              {#each justLanded as album (album.key)}
                {@const firstSong = album.songs[0]}
                <div class="hover:bg-muted/50 group/row -mx-2 flex items-center gap-2.5 rounded-md px-2 py-2 transition-colors">
                  <button
                    type="button"
                    onclick={() => goto(`/library?album=${encodeURIComponent(album.key)}`)}
                    class="focus-visible:ring-ring flex min-w-0 flex-1 items-center gap-2.5 rounded-sm text-left focus-visible:ring-2 focus-visible:outline-none"
                    title="Open album"
                  >
                    {#if album.coverUrl}
                      <img src={album.coverUrl} alt="" class="size-9 shrink-0 rounded object-cover" />
                    {:else}
                      <span class="bg-muted text-muted-foreground grid size-9 shrink-0 place-items-center rounded text-[10px] font-semibold">
                        {albumInitials(album.title)}
                      </span>
                    {/if}
                    <div class="min-w-0 flex-1">
                      <div class="truncate text-[12.5px] font-medium">{album.title}</div>
                      <div class="text-muted-foreground truncate text-[11.5px]">
                        {album.artist}{album.year ? ` · ${album.year}` : ''}
                      </div>
                    </div>
                  </button>
                  {#if firstSong}
                    <a
                      href={`/track/${firstSong.id}`}
                      title="View enrichment timeline"
                      class="text-muted-foreground/60 hover:text-foreground inline-flex shrink-0 items-center gap-1 rounded-md px-1.5 py-1 text-[11.5px] font-medium opacity-100 transition-colors group-hover/row:opacity-100 focus-visible:opacity-100 sm:opacity-0"
                    >
                      <History class="size-3" /> Timeline
                    </a>
                  {/if}
                  <ChevronRight class="text-muted-foreground/50 hidden size-3.5 shrink-0 sm:inline-flex" />
                </div>
              {/each}
            </div>
          {:else}
            <p class="text-muted-foreground/70 py-8 text-center text-[12.5px]">Nothing in the library yet.</p>
          {/if}
        </div>
      </section>
    </div>
  </div>
</ScrollArea>

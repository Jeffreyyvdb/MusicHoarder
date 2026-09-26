<script lang="ts">
  import {
    ChevronRight,
    Sparkles,
    Tag,
    Copy,
    Loader2,
    History,
    Pause,
    Play,
    ScanLine,
    AudioLines,
    PackageCheck,
    TriangleAlert
  } from '@lucide/svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import type { Component } from 'svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import { Button } from '$lib/components/ui/button';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import {
    fetchAlbums,
    hydrateAlbums,
    fetchDuplicates,
    fetchQualityOverview,
    fetchQualityProgress,
    fetchSongs,
    fetchStats,
    mapEnrichmentStatus,
    sortAlbumsByRecency,
    triggerEnrichmentScan,
    type AlbumSummary,
    type AlbumSummaryDto,
    type ApiOverviewActivity,
    type ApiSong,
    type ApiStats,
    type DuplicatesResponse,
    type QualityOverview,
    type QualityProgress
  } from '$lib/api-client';
  import { isBuiltSong } from '$lib/album-sections';
  import { activityDot } from '$lib/activity-tone';
  import {
    pipelineOverlay,
    JOB_CONFIRM_COPY,
    type JobConfirm,
    type StageKey
  } from '$lib/stores/pipeline-overlay.svelte';
  import { cn } from '$lib/utils';
  import { formatBytesShort } from '$lib/formatters';
  import { inboxCounts, publishInboxQueueCount } from '$lib/stores/nav-badges.svelte';

  // The pipeline's home: what is running and how to hold it, what the library looks like, where
  // each stage stands, what is waiting for a decision, and what just happened. On a phone it is
  // one inset-grouped scroll (Settings-style sections, no scroller inside a scroller — Recent
  // activity shows a handful of rows and expands in place, Just landed has a "See all"). From lg
  // the same sections carry the six-figure strip and the horizontal conveyor; below lg (a phone,
  // or an iPad in portrait beside the sidebar, where seven nodes and six figures truncate) they
  // stay value rows and a vertical timeline.
  //
  // The demo account is read-only — hide the mutating controls (Rescan, Pause/Resume, Stop; the
  // backend rejects them regardless, this just avoids dead buttons). Defaults false so non-demo
  // callers are unaffected.
  let { isDemo = false }: { isDemo?: boolean } = $props();

  // ── data layer (reuses the existing api-client + album-sections) ───────────
  let stats = $state<ApiStats | null>(null);
  let songs = $state<ApiSong[]>([]);
  let albumDtos = $state<AlbumSummaryDto[]>([]);
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
      const [stRes, songsRes, albumsRes, qRes, qpRes, dupRes] = await Promise.allSettled([
        fetchStats(),
        fetchSongs(),
        fetchAlbums(),
        fetchQualityOverview(),
        fetchQualityProgress(),
        fetchDuplicates()
      ]);
      if (stRes.status === 'fulfilled') stats = stRes.value;
      if (songsRes.status === 'fulfilled') songs = songsRes.value;
      if (albumsRes.status === 'fulfilled') albumDtos = albumsRes.value;
      // Quality grading may be unconfigured — failure just leaves the KPI as "—".
      if (qRes.status === 'fulfilled') quality = qRes.value;
      if (qpRes.status === 'fulfilled') qualityProgress = qpRes.value;
      if (dupRes.status === 'fulfilled') duplicates = dupRes.value;
      // This page already holds the two responses the Inbox badge counts from, and polls them
      // more often than the badge refreshes; handing them over keeps the badge, the Inbox hub and
      // this page's "Awaiting you" on the same, freshest figures.
      if (dupRes.status === 'fulfilled')
        publishInboxQueueCount('dupes', (dupRes.value.duplicateGroups ?? []).length);
      if (qRes.status === 'fulfilled') publishInboxQueueCount('ai', qRes.value.aiFlaggedCount);
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

  // Awaiting-you and its breakdown are the Inbox's own counts (nav-badges.svelte.ts), the ones
  // the tab badge and the Inbox hub show, so the three can never disagree: Tag review is
  // needs-review only, Duplicate tracks counts groups, AI flagged is Wrong + Questionable — each
  // exactly what its queue lists — and "Awaiting you" is their sum, the badge. Failed matches have
  // no queue; they are the Errors figure.
  const inbox = $derived(inboxCounts());
  const tagReviewCount = $derived(inbox.review);
  const aiFlaggedCount = $derived(inbox.ai);
  const awaitingYou = $derived(inbox.total);

  const avgQuality = $derived(quality?.library?.averageScore ?? null);
  const qualityGraded = $derived(quality?.library?.graded ?? null);

  // Duplicate tracks flagged by fingerprint dedupe (DB-backed; read-only count). The Dedupe stage's
  // figure — tracks, not the groups the Inbox queue decides on.
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
    /** Stage is actively processing right now (not paused). */
    live: boolean;
    /** The JobManager step behind this stage is paused (automatic runs skip it). */
    paused: boolean;
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

  /** Running and not paused. A paused Enrich step keeps reporting Running (its workers hold the
   *  queue), and a paused build reports Running until its job winds down; neither is flowing. */
  function flowing(key: StageKey): boolean {
    return pipelineOverlay.isStageRunning(key) && !pipelineOverlay.isStagePaused(key);
  }

  const stages = $derived.by<Stage[]>(() => {
    const s = snap;
    const running = anyRunning;
    const job = overview?.job;
    const paused = (key: StageKey) => pipelineOverlay.isStagePaused(key);
    return [
      {
        id: 'scan',
        label: 'Scan',
        count: stageCount(s?.scanned, job?.tracksProcessed, running && s?.scan?.status === 'Running'),
        live: running && flowing('scan'),
        paused: paused('scan'),
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
        live: running && flowing('fingerprint'),
        paused: paused('fingerprint'),
        rate: running && rates.fingerprint > 0 ? rates.fingerprint : null
      },
      {
        id: 'match',
        label: 'Match',
        count: stageCount(s?.enriched, job?.tracksBuildEligible, running && s?.enrich?.status === 'Running'),
        live: running && flowing('enrich'),
        paused: paused('enrich'),
        rate: running && rates.enrich > 0 ? rates.enrich : null
      },
      {
        id: 'decide',
        label: 'Decide',
        count: decidedCount,
        live: running && flowing('enrich'),
        paused: false,
        rate: null
      },
      {
        id: 'grade',
        label: 'AI grade',
        count: qualityGraded,
        live: qualityProgress?.active === true,
        paused: false,
        rate: null
      },
      {
        id: 'dedupe',
        label: 'Dedupe',
        count: duplicateCount,
        live: running && flowing('fingerprint'),
        paused: false,
        rate: null
      },
      {
        id: 'library',
        label: 'Library',
        count: stageCount(s?.built, job?.tracksCopied ?? inLibrary, running && s?.build?.status === 'Running'),
        live: running && flowing('build'),
        paused: paused('build'),
        rate: running && rates.build > 0 ? rates.build : null
      }
    ];
  });

  const activeStageDef = $derived(stages.find((st) => st.id === activeStage) ?? stages[0]);

  // Detail figures for the selected stage. The SSE stream is count-only, so the
  // detail reflects cumulative counts honestly rather than inventing file names.
  type DetailLine = { label: string; value: string; attention?: boolean };

  const activeStageDetail = $derived.by<DetailLine[]>(() => {
    const st = activeStageDef;
    const lines: DetailLine[] = [];

    if (st.id === 'decide') {
      if (decidedCount != null) lines.push({ label: 'Decided', value: decidedCount.toLocaleString() });
      if (matchedCount != null) lines.push({ label: 'Matched', value: matchedCount.toLocaleString() });
      if (tagReviewCount != null)
        lines.push({
          label: 'To review',
          value: tagReviewCount.toLocaleString(),
          attention: tagReviewCount > 0
        });
      return lines;
    }
    if (st.id === 'grade') {
      if (qualityGraded != null) lines.push({ label: 'Graded', value: qualityGraded.toLocaleString() });
      if (avgQuality != null) lines.push({ label: 'Avg score', value: avgQuality.toFixed(0) });
      if (aiFlaggedCount != null) lines.push({ label: 'Flagged', value: aiFlaggedCount.toLocaleString() });
      return lines;
    }
    if (st.id === 'dedupe') {
      if (duplicateCount != null) lines.push({ label: 'Duplicates', value: duplicateCount.toLocaleString() });
      if (duplicates?.groups != null) lines.push({ label: 'Clusters', value: duplicates.groups.toLocaleString() });
      return lines;
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
    return lines;
  });

  // ── recent activity (real RecentActivity from the overview poll) ────────────
  // A handful of rows that expand in place, rather than a scroller inside the page's scroller.
  // The per-file list lives nowhere else a phone can reach (History is an aggregated feed and the
  // drawer's live log is desktop-only), so every row stays one tap away.
  const RECENT_ROWS = 6;
  let showAllActivity = $state(false);
  const allRecent = $derived<ApiOverviewActivity[]>(overview?.recentActivity ?? []);
  const recent = $derived(showAllActivity ? allRecent : allRecent.slice(0, RECENT_ROWS));

  // Sentence-case verbs + one small dot per event type (activityDot: the tint for progress, the
  // warning tone only for the actionable review state, red only for failures).
  const ACTIVITY_VERB: Record<ApiOverviewActivity['type'], string> = {
    discovered: 'Discovered',
    copied: 'Added to library',
    enriched: 'Matched',
    review: 'Needs review',
    failed: 'Failed'
  };

  // ── just landed (the newest albums the server grouped) ──────────────────────
  const justLanded = $derived.by<AlbumSummary[]>(() => {
    if (!loaded || albumDtos.length === 0) return [];
    const byId = new Map(songs.map((song) => [song.id, song]));
    return sortAlbumsByRecency(hydrateAlbums(albumDtos, byId)).slice(0, 6);
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

  // ── job control (pause / resume / stop) ─────────────────────────────────────
  // One row per JobManager step that is running or paused, so the phone — which never gets the
  // desktop import drawer — can see how long is left and hold or stop it. Feedback is the row
  // itself changing; the store toasts only on failure.
  const JOB_STEPS: { key: StageKey; label: string; icon: Component }[] = [
    { key: 'scan', label: 'Scan', icon: ScanLine },
    { key: 'fingerprint', label: 'Fingerprint', icon: AudioLines },
    { key: 'enrich', label: 'Match', icon: Sparkles },
    { key: 'build', label: 'Library build', icon: PackageCheck }
  ];

  type JobRow = {
    key: StageKey;
    label: string;
    icon: Component;
    running: boolean;
    paused: boolean;
    busy: boolean;
    rate: number;
    /** Share of this step's work done, 0–1 — null when there is nothing to measure against. */
    progress: number | null;
  };

  // Each step's own done/target, the same figures the desktop drawer's stage cards draw, so a
  // phone gets a determinate bar per step instead of a rate alone.
  function stepProgress(key: StageKey): number | null {
    const s = snap;
    if (!s) return null;
    const discovered = s.discovered ?? 0;
    const [done, target] =
      key === 'scan'
        ? [s.scanned, discovered]
        : key === 'fingerprint'
          ? [s.fingerprinted, discovered]
          : key === 'enrich'
            ? [s.enriched, discovered]
            : [s.built, overview?.job?.tracksBuildEligible || s.enriched || discovered];
    if (!target || target <= 0) return null;
    return Math.max(0, Math.min(1, (done ?? 0) / target));
  }

  const jobRows = $derived<JobRow[]>(
    JOB_STEPS.map((j) => ({
      ...j,
      running: pipelineOverlay.isStageRunning(j.key),
      paused: pipelineOverlay.isStagePaused(j.key),
      busy: pipelineOverlay.isStageBusy(j.key),
      rate: rates[j.key],
      progress: stepProgress(j.key)
    })).filter((j) => j.running || j.paused)
  );
  const anyFlowing = $derived(jobRows.some((j) => j.running && !j.paused));
  const anyPaused = $derived(jobRows.some((j) => j.paused));
  const etaSeconds = $derived(pipelineOverlay.etaSeconds);

  function etaPhrase(sec: number): string {
    if (sec < 60) return 'under a minute left';
    const min = Math.round(sec / 60);
    if (min < 60) return `about ${min} min left`;
    const h = Math.floor(min / 60);
    const m = min % 60;
    return m === 0 ? `about ${h} h left` : `about ${h} h ${m} min left`;
  }

  function jobStatus(j: JobRow): string {
    if (j.paused) {
      // Paused enrichment workers park on their queue rather than dropping it.
      if (j.running && j.key === 'enrich') return 'Paused, queue kept';
      if (j.running) return 'Pausing…';
      return 'Paused';
    }
    return j.rate > 0 ? `${j.rate >= 10 ? Math.round(j.rate) : j.rate.toFixed(1)} files/s` : 'Running';
  }

  // The status line is the page's subtitle — on a phone it is the first thing under the title,
  // so it stays a few words; the sections below carry the detail.
  const statusMeta = $derived(
    anyFlowing
      ? `Running${etaSeconds != null ? ` · ${etaPhrase(etaSeconds)}` : ''}`
      : anyPaused
        ? 'Paused · automatic runs skip a paused step'
        : 'Idle · watching your source folder'
  );

  let confirmOpen = $state(false);
  let confirmKind = $state<JobConfirm>('stop');
  const confirmCopy = $derived(JOB_CONFIRM_COPY[confirmKind]);

  function ask(kind: JobConfirm) {
    confirmKind = kind;
    confirmOpen = true;
  }

  function requestStop() {
    if (pipelineOverlay.needsConfirm('stop')) ask('stop');
    else void pipelineOverlay.cancelRunning();
  }

  function togglePause(j: JobRow) {
    if (!j.paused && j.key === 'build' && pipelineOverlay.needsConfirm('pause-build')) ask('pause-build');
    else void pipelineOverlay.setStagePaused(j.key, !j.paused);
  }

  function confirmAction() {
    // Closed before the request starts rather than after the Action's own close.
    confirmOpen = false;
    if (confirmKind === 'stop') void pipelineOverlay.cancelRunning();
    else void pipelineOverlay.setStagePaused('build', true);
  }

  type NeedRow = {
    id: 'review' | 'dupes' | 'ai';
    icon: Component;
    count: number | null;
    label: string;
    body: string;
    href: string;
  };

  // Each row opens its own queue (?tab=): the bare /inbox is the phone's Inbox hub, so a row that
  // linked there would land one level short of what it promised.
  const needRows = $derived<NeedRow[]>([
    {
      id: 'review',
      icon: Tag,
      count: tagReviewCount,
      label: 'Tag reviews',
      body: "Providers couldn't agree — pick the right candidate or fix the fields yourself.",
      href: '/inbox?tab=review'
    },
    {
      id: 'dupes',
      icon: Copy,
      count: inbox.dupes,
      label: 'Ambiguous duplicates',
      body: "Same fingerprint, can't auto-pick which copy to keep.",
      href: '/inbox?tab=dupes'
    },
    {
      id: 'ai',
      icon: Sparkles,
      count: aiFlaggedCount,
      label: 'AI flagged',
      body: 'The quality grader thinks these matches look wrong or questionable — worth a second look.',
      href: '/inbox?tab=ai'
    }
  ]);

  // A trailing text link in a section header ("See all", "Open Inbox"): tint text at the header's
  // size, with a 44pt hit area on touch grown by a pseudo-element rather than the visual. It grows
  // mostly upward, into the gap between sections: the card starts 6px below the line (the
  // header's padding), and an even 13px down let the card's first row take 7px of the target.
  // 20 up + the 18px line + 6 down = 44.
  const HEADER_LINK =
    'text-primary relative inline-flex items-center gap-0.5 font-medium outline-none hover:underline focus-visible:underline pointer-coarse:after:absolute pointer-coarse:after:-inset-x-2 pointer-coarse:after:-top-5 pointer-coarse:after:-bottom-1.5';
</script>

{#snippet sectionHeader(title: string, link?: { href: string; label: string })}
  <!-- A span, not a div: it sits inside the section's <h2>. -->
  <span class="flex items-baseline justify-between gap-3">
    <span>{title}</span>
    {#if link}
      <a href={link.href} class={HEADER_LINK}>{link.label}</a>
    {/if}
  </span>
{/snippet}

<!-- A status dot beside a figure that is waiting on you: the warning colour marks it without
     turning the number itself red (red is for Errors alone). Decorative — the label already says
     "Awaiting you". -->
{#snippet attentionDot()}
  <span class="bg-warning size-2 shrink-0 rounded-full" aria-hidden="true"></span>
{/snippet}

<!-- One figure of the lg+ strip. The label and its detail take a line each, wrapping rather than
     truncating: a sixth of the column is too narrow for "In library · 78% matched" on one line. -->
{#snippet kpi(label: string, value: string, sub: string | null, opts?: { tone?: string; loading?: boolean })}
  <div class="min-w-0 flex-1 px-4 py-4 xl:px-5">
    {#if opts?.loading}
      <Skeleton class="h-7 w-14" />
    {:else}
      <div class={cn('text-xl leading-tight font-semibold tabular-nums', opts?.tone)}>{value}</div>
    {/if}
    <div class="text-muted-foreground mt-1 text-[12.5px] leading-snug">{label}</div>
    {#if sub}
      <div class="text-muted-foreground text-[12.5px] leading-snug tabular-nums">{sub}</div>
    {/if}
  </div>
{/snippet}

{#snippet valueRow(
  label: string,
  value: string,
  opts?: { tone?: string; loading?: boolean; href?: string; attention?: boolean }
)}
  <GroupedList.Row {label} href={opts?.href} chevron={!!opts?.href}>
    {#snippet trailing()}
      {#if opts?.loading}
        <Skeleton class="h-4 w-12" />
      {:else}
        <span
          class={cn(
            'text-body inline-flex items-center gap-2 tabular-nums md:text-sm',
            opts?.tone ?? 'text-muted-foreground'
          )}
        >
          {#if opts?.attention}{@render attentionDot()}{/if}
          {value}
        </span>
      {/if}
    {/snippet}
  </GroupedList.Row>
{/snippet}

{#snippet stageNode(st: Stage)}
  <span
    class={cn(
      'relative z-10 block size-[11px] shrink-0 rounded-full transition-colors',
      st.count != null && st.count > 0 ? 'bg-primary' : 'bg-muted-foreground-dim',
      st.live && 'ring-primary/25 ring-4'
    )}
    aria-hidden="true"
  ></span>
{/snippet}

{#snippet stageState(st: Stage)}
  {#if st.paused}
    <span class="text-warning-text inline-flex items-center gap-1 font-medium">
      <Pause class="size-3" aria-hidden="true" /> Paused
    </span>
  {:else if st.live && st.rate != null}
    <span class="text-muted-foreground tabular-nums">{st.rate.toFixed(0)}/s</span>
  {/if}
{/snippet}

{#snippet stageDetailLine()}
  {#if activeStageDetail.length === 0}
    <span>Nothing in this stage right now.</span>
  {:else}
    {#each activeStageDetail as line, i (line.label)}
      <span aria-hidden="true">{i === 0 ? '' : ' · '}</span>{line.label}
      <span
        class={cn(
          'font-medium tabular-nums',
          line.attention ? 'text-warning-text' : 'text-foreground'
        )}>{line.value}</span
      >
    {/each}
  {/if}
{/snippet}

<!-- One scroller for the whole page; the nav bar is its first child so the large title scrolls
     away under the sticky bar. The grouped background is what the inset sections sit on. -->
<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1">
    <PageToolbarV2 title="Pipeline" meta={statusMeta} grouped>
      {#snippet actions()}
        {#if anyFlowing}
          <!-- The live pulse beside the desktop title; on a phone the subtitle already says
               "Running" and the Running now rows pulse below it. -->
          <span class="hidden items-center md:inline-flex">
            <span class="bg-primary mh-v2-pulse size-2 shrink-0 rounded-full" aria-hidden="true"></span>
            <span class="sr-only">running</span>
          </span>
        {/if}
        {#if !isDemo}
          <Button
            variant="gray"
            class="rounded-full"
            onclick={handleRescan}
            disabled={rescanning || anyRunning}
          >
            <!-- The Scan stage's own glyph: Rescan runs that step. A circular arrow would read as
                 "refresh this page", which is what it means everywhere else. -->
            {#if rescanning}
              <Loader2 class="animate-spin" aria-hidden="true" />
            {:else}
              <ScanLine aria-hidden="true" />
            {/if}
            <!-- The phone bar shows the glyph alone; the word stays its accessible name. -->
            <span class="max-md:sr-only">Rescan</span>
          </Button>
        {/if}
      {/snippet}
    </PageToolbarV2>

    <div class="mx-auto flex w-full max-w-6xl flex-col gap-7 pt-2 pb-8 md:gap-6 md:px-7 md:pt-6">
      {#if loadError}
        <GroupedList.Section>
          <GroupedList.Row
            icon={TriangleAlert}
            iconClass="bg-destructive/12 text-destructive-text"
            label="Some figures failed to load"
            sublabel="What's below may be missing or out of date."
          >
            {#snippet trailing()}
              <Button variant="ghost" class="text-primary hover:text-primary h-11 md:h-8" onclick={() => void loadAll()}>
                Retry
              </Button>
            {/snippet}
          </GroupedList.Row>
        </GroupedList.Section>
      {/if}

      <!-- Running now — what is moving, how long is left, and the controls to hold or stop it.
           On a phone these rows are the ONLY job controls (the drawer is desktop-only). -->
      {#if jobRows.length > 0}
        <GroupedList.Section headingLevel={2}
          header={anyFlowing ? 'Running now' : 'Paused'}
          footer={anyFlowing && etaSeconds != null
            ? `${etaPhrase(etaSeconds).replace(/^./, (c) => c.toUpperCase())} for everything in flight.`
            : anyPaused
              ? 'Automatic runs skip a paused step until you resume it.'
              : undefined}
        >
          {#each jobRows as j (j.key)}
            <GroupedList.Row
              icon={j.icon}
              iconClass={j.paused
                ? 'bg-warning/15 text-warning-text'
                : 'bg-primary text-primary-foreground'}
              label={j.label}
              sublabel={jobStatus(j)}
            >
              {#if j.progress != null}
                <span
                  role="progressbar"
                  aria-label="{j.label} progress"
                  aria-valuemin={0}
                  aria-valuemax={100}
                  aria-valuenow={Math.round(j.progress * 100)}
                  class="bg-muted mt-1.5 mb-0.5 block h-1 w-full overflow-hidden rounded-full"
                >
                  <span
                    class={cn(
                      'block h-full w-full origin-left transition-transform duration-300 ease-out',
                      j.paused ? 'bg-warning' : 'bg-primary'
                    )}
                    style="transform: scaleX({j.progress})"
                  ></span>
                </span>
              {/if}
              {#snippet trailing()}
                {#if !isDemo}
                  <Button
                    variant="gray"
                    size="sm"
                    class="relative h-8 rounded-full px-3 text-[13px] pointer-coarse:after:absolute pointer-coarse:after:-inset-1.5"
                    onclick={() => togglePause(j)}
                    disabled={j.busy}
                    aria-label={j.paused ? `Resume ${j.label.toLowerCase()}` : `Pause ${j.label.toLowerCase()}`}
                  >
                    {#if j.busy}
                      <Loader2 class="animate-spin" aria-hidden="true" />
                    {:else if j.paused}
                      <Play aria-hidden="true" />
                    {:else}
                      <Pause aria-hidden="true" />
                    {/if}
                    {j.paused ? 'Resume' : 'Pause'}
                  </Button>
                {/if}
              {/snippet}
            </GroupedList.Row>
          {/each}
          {#if !isDemo && pipelineOverlay.canStop}
            <!-- Destructive, last in its group; confirms while the build runs (needsConfirm). -->
            <GroupedList.Row
              onclick={requestStop}
              disabled={pipelineOverlay.cancelling}
              destructive
              label={pipelineOverlay.cancelling ? 'Stopping…' : 'Stop all'}
            />
          {/if}
        </GroupedList.Section>
      {/if}

      <!-- Summary. Below lg it reads as a list of values; from lg it is the six-figure strip. -->
      <GroupedList.Section headingLevel={2} header="Summary">
        <!-- lg+: the strip -->
        <div class="divide-separator hidden divide-x lg:flex">
          {@render kpi(
            'Source files',
            fmtNum(sourceTotal),
            sourceBytes != null ? formatBytesShort(sourceBytes) : null,
            { loading: sourceTotal == null && !loaded, tone: sourceTotal == null ? 'text-muted-foreground' : undefined }
          )}
          {@render kpi(
            'In library',
            fmtNum(inLibrary),
            enrichedPct != null ? `${enrichedPct.toFixed(0)}% matched` : null,
            { loading: inLibrary == null }
          )}
          {@render kpi('In flight', inFlight.toLocaleString(), null, { loading: !loaded })}
          <!-- The only interactive figure: it opens the Inbox. -->
          <a
            href="/inbox"
            class="group hover:bg-accent focus-visible:ring-ring min-w-0 flex-1 px-4 py-4 outline-none focus-visible:ring-2 focus-visible:ring-inset xl:px-5"
          >
            {#if awaitingYou == null && !loaded}
              <Skeleton class="h-7 w-12" />
            {:else if awaitingYou == null}
              <div class="text-muted-foreground text-xl leading-tight font-semibold">—</div>
            {:else}
              <!-- The figure stays foreground with a warning dot: in light mode the warning and
                   destructive text tones are near twins, and waiting is not an error. -->
              <div class="flex items-center gap-2 text-xl leading-tight font-semibold tabular-nums">
                {#if awaitingYou > 0}{@render attentionDot()}{/if}
                {fmtNum(awaitingYou)}
              </div>
            {/if}
            <div class="text-muted-foreground group-hover:text-foreground mt-1 flex items-center gap-0.5 text-[12.5px]">
              Awaiting you <ChevronRight class="size-3" aria-hidden="true" />
            </div>
          </a>
          {@render kpi(
            'Avg quality',
            avgQuality == null ? '—' : avgQuality.toFixed(1),
            qualityGraded != null && qualityGraded > 0 ? `${qualityGraded.toLocaleString()} graded` : null,
            { loading: avgQuality == null && !loaded, tone: avgQuality == null ? 'text-muted-foreground' : undefined }
          )}
          {@render kpi('Errors', fmtNum(errorCount), null, {
            loading: errorCount == null,
            tone: (errorCount ?? 0) > 0 ? 'text-destructive-text' : undefined
          })}
        </div>

        <!-- Below lg: value rows. -->
        <div class="lg:hidden">
          {@render valueRow(
            'Source files',
            `${fmtNum(sourceTotal)}${sourceBytes != null ? ` · ${formatBytesShort(sourceBytes)}` : ''}`,
            { loading: sourceTotal == null && !loaded }
          )}
          {@render valueRow(
            'In library',
            `${fmtNum(inLibrary)}${enrichedPct != null ? ` · ${enrichedPct.toFixed(0)}% matched` : ''}`,
            { loading: inLibrary == null }
          )}
          {@render valueRow('In flight', inFlight.toLocaleString(), { loading: !loaded })}
          {@render valueRow('Awaiting you', fmtNum(awaitingYou), {
            loading: awaitingYou == null && !loaded,
            href: '/inbox',
            attention: (awaitingYou ?? 0) > 0,
            tone: (awaitingYou ?? 0) > 0 ? 'text-foreground font-medium' : undefined
          })}
          {@render valueRow(
            'Avg quality',
            `${avgQuality == null ? '—' : avgQuality.toFixed(1)}${qualityGraded ? ` · ${qualityGraded.toLocaleString()} graded` : ''}`,
            { loading: avgQuality == null && !loaded }
          )}
          {@render valueRow('Errors', fmtNum(errorCount), {
            loading: errorCount == null,
            tone: (errorCount ?? 0) > 0 ? 'text-destructive-text font-medium' : undefined
          })}
        </div>
      </GroupedList.Section>

      <!-- Stages: the seven-step conveyor. Selecting a stage shows its figures — inline under the
           row below lg, under the flow from lg. -->
      <GroupedList.Section headingLevel={2}
        header="Stages"
        footer="Select a stage to see its figures."
      >
        <!-- lg+: horizontal flow, nodes joined by a continuous line. -->
        <div class="hidden px-2 pt-5 pb-4 lg:block">
          <div class="relative">
            <div
              class="bg-separator absolute top-[5px] h-(--hairline)"
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
                  class="group focus-visible:ring-ring flex flex-col items-center gap-2.5 rounded-md pb-1 text-center transition-transform duration-100 ease-out outline-none focus-visible:ring-2 active:scale-[0.97]"
                >
                  {@render stageNode(st)}
                  <!-- Label and count on their own lines: seven nodes share the column, and "Fingerprint
                       75" on one line truncated before 1280px. -->
                  <span class="flex max-w-full flex-col items-center px-1 text-[13px] leading-tight">
                    <span
                      class={cn(
                        'max-w-full truncate transition-colors',
                        isActive
                          ? 'text-foreground font-semibold'
                          : 'text-muted-foreground group-hover:text-foreground font-medium'
                      )}
                    >
                      {st.label}
                    </span>
                    <span class="text-muted-foreground mt-0.5 tabular-nums">
                      {st.count == null ? '—' : st.count.toLocaleString()}
                    </span>
                  </span>
                  <span class="-mt-1.5 min-h-4 text-[11px]">{@render stageState(st)}</span>
                </button>
              {/each}
            </div>
          </div>
          <p class="border-separator text-muted-foreground mx-3 mt-4 border-t pt-3 text-[12.5px]">
            <span class="text-foreground font-medium">{activeStageDef.label}</span>
            <span aria-hidden="true" class="mx-1">—</span>{@render stageDetailLine()}
          </p>
        </div>

        <!-- Below lg: the same flow as a vertical timeline of rows. The line runs through the
             leading column; the selected row opens its figures under its label. The rows are a
             selection (one is always chosen, like the flow's nodes), so they announce pressed
             rather than a disclosure that a second tap would not close. -->
        <div class="lg:hidden">
          {#each stages as st, i (st.id)}
            {@const isActive = activeStage === st.id}
            <GroupedList.Row
              onclick={() => (activeStage = st.id)}
              aria-pressed={isActive}
            >
              {#snippet leading()}
                <!-- The connecting line is positioned against the row itself (the nearest
                     positioned box), so it spans the whole row however tall the open one grows:
                     16px row inset + half the 29px column. -->
                <span class="flex w-[29px] justify-center">
                  <span
                    aria-hidden="true"
                    class={cn(
                      'bg-separator absolute left-[calc(1rem+14px)] w-(--hairline)',
                      i === 0 ? 'top-1/2' : 'top-0',
                      i === stages.length - 1 ? 'bottom-1/2' : 'bottom-0'
                    )}
                  ></span>
                  <span class="relative flex items-center">{@render stageNode(st)}</span>
                </span>
              {/snippet}
              <span class={cn('text-body md:text-sm', isActive && 'font-semibold')}>{st.label}</span>
              {#if isActive}
                <span class="text-subheadline text-muted-foreground mt-0.5 md:text-xs">{@render stageDetailLine()}</span>
              {/if}
              {#snippet trailing()}
                <span class="text-subheadline flex items-center gap-3 md:text-xs">
                  {@render stageState(st)}
                  <span class="text-body text-muted-foreground tabular-nums md:text-sm">
                    {st.count == null ? '—' : st.count.toLocaleString()}
                  </span>
                </span>
              {/snippet}
            </GroupedList.Row>
          {/each}
        </div>
      </GroupedList.Section>

      <!-- Needs you — each row opens its queue. -->
      <GroupedList.Section headingLevel={2} footer="When the pipeline can't decide, items pile up here.">
        {#snippet header()}
          {@render sectionHeader('Needs you', { href: '/inbox', label: 'Open Inbox' })}
        {/snippet}
        {#each needRows as row (row.id)}
          <GroupedList.Row href={row.href} icon={row.icon} label={row.label} sublabel={row.body} chevron>
            {#snippet trailing()}
              {#if row.count == null}
                {#if loaded}
                  <span class="text-body text-muted-foreground tabular-nums md:text-sm">—</span>
                {:else}
                  <Skeleton class="h-4 w-6" />
                {/if}
              {:else}
                <span
                  class={cn(
                    'text-body tabular-nums md:text-sm',
                    row.count === 0 ? 'text-muted-foreground' : 'text-foreground font-semibold'
                  )}
                >
                  {row.count.toLocaleString()}
                </span>
              {/if}
            {/snippet}
          </GroupedList.Row>
        {/each}
      </GroupedList.Section>

      <!-- Recent activity + Just landed: side by side from lg. -->
      <div class="grid grid-cols-1 items-start gap-7 lg:grid-cols-2 lg:gap-6">
        <GroupedList.Section headingLevel={2} class="min-w-0">
          {#snippet header()}
            {@render sectionHeader('Recent activity', { href: '/history', label: 'Open History' })}
          {/snippet}
          {#if recent.length > 0}
            {#each recent as a (a.id)}
              <GroupedList.Row
                label={ACTIVITY_VERB[a.type] ?? a.type}
                sublabel={`${a.track}${a.artist ? ` · ${a.artist}` : ''}`}
                value={a.time}
              >
                {#snippet leading()}
                  <span class="flex w-3 justify-center" aria-hidden="true">
                    <span class={cn('size-2 rounded-full', activityDot(a.type))}></span>
                  </span>
                {/snippet}
              </GroupedList.Row>
            {/each}
            {#if allRecent.length > RECENT_ROWS}
              <!-- Expands in place, in the page's own scroller. -->
              <GroupedList.Row
                onclick={() => (showAllActivity = !showAllActivity)}
                aria-expanded={showAllActivity}
              >
                <span class="text-body text-primary md:text-sm">
                  {showAllActivity ? 'Show fewer' : `Show all ${allRecent.length.toLocaleString()}`}
                </span>
              </GroupedList.Row>
            {/if}
          {:else}
            <p class="text-body text-muted-foreground px-4 py-6 text-center md:text-sm">
              No recent activity yet.
            </p>
          {/if}
        </GroupedList.Section>

        <GroupedList.Section headingLevel={2} class="min-w-0">
          {#snippet header()}
            {@render sectionHeader('Just landed', { href: '/library', label: 'See all' })}
          {/snippet}
          {#if !loaded}
            {#each Array(4) as _, i (i)}
              <div class="flex items-center gap-3 px-4 py-2.5">
                <Skeleton class="size-10 shrink-0 rounded-sm" />
                <div class="min-w-0 flex-1 space-y-1.5">
                  <Skeleton class="h-4 w-2/3" />
                  <Skeleton class="h-3 w-1/3" />
                </div>
              </div>
            {/each}
          {:else if justLanded.length > 0}
            {#each justLanded as album (album.key)}
              {@const firstSong = album.songs[0]}
              <!-- The whole row opens the album (a stretched link), and the Timeline button sits
                   above it — two targets without nesting one link inside another. -->
              <GroupedList.Row chevron class="group/row hover:bg-accent active:bg-accent">
                {#snippet leading()}
                  {#if album.coverUrl}
                    <img
                      src={album.coverUrl}
                      alt=""
                      draggable="false"
                      class="size-10 shrink-0 rounded-sm object-cover"
                    />
                  {:else}
                    <span
                      class="bg-muted text-muted-foreground grid size-10 shrink-0 place-items-center rounded-sm text-[11px] font-semibold"
                      aria-hidden="true"
                    >
                      {albumInitials(album.title)}
                    </span>
                  {/if}
                {/snippet}
                <a
                  href={`/library?album=${encodeURIComponent(album.key)}`}
                  class="text-body md:text-sm truncate outline-none after:absolute after:inset-0 focus-visible:after:ring-2 focus-visible:after:ring-ring focus-visible:after:ring-inset"
                >
                  {album.title}
                </a>
                <span class="text-subheadline text-muted-foreground truncate md:text-xs">
                  {album.artist}{album.year ? ` · ${album.year}` : ''}
                </span>
                {#snippet trailing()}
                  {#if firstSong}
                    <Button
                      variant="ghost"
                      size="icon"
                      href={`/track/${firstSong.id}`}
                      aria-label="Enrichment timeline for {album.title}"
                      title="View enrichment timeline"
                      class="text-muted-foreground hover:text-foreground relative z-10 -my-1 rounded-full pointer-coarse:size-11 pointer-fine:opacity-0 pointer-fine:group-hover/row:opacity-100 pointer-fine:focus-visible:opacity-100"
                    >
                      <History aria-hidden="true" />
                    </Button>
                  {/if}
                {/snippet}
              </GroupedList.Row>
            {/each}
          {:else}
            <p class="text-body text-muted-foreground px-4 py-6 text-center md:text-sm">
              Nothing in the library yet.
            </p>
          {/if}
        </GroupedList.Section>
      </div>
    </div>
  </ScrollArea>
</div>

<AlertDialog.Root bind:open={confirmOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>{confirmCopy.title}</AlertDialog.Title>
      <AlertDialog.Description>{confirmCopy.description}</AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action variant="destructive" onclick={confirmAction}>{confirmCopy.action}</AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

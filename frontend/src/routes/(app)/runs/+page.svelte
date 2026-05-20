<script lang="ts">
  import { Card, CardContent, CardHeader, CardTitle } from '$lib/components/ui/card';
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import {
    Music,
    FolderInput,
    FolderOutput,
    Clock,
    XCircle,
    Disc3,
    Sparkles,
    Copy,
    FileWarning,
    ArrowRight,
    PackageCheck,
    Search,
    RefreshCw
  } from '@lucide/svelte';
  import StatusBadge from '$lib/components/overview/StatusBadge.svelte';
  import PipelineStage from '$lib/components/overview/PipelineStage.svelte';
  import StatCard from '$lib/components/overview/StatCard.svelte';
  import ActivityItem from '$lib/components/overview/ActivityItem.svelte';
  import {
    fetchOverview,
    triggerEnrichmentScan,
    triggerFingerprint,
    triggerEnrich,
    triggerBuild,
    pauseStep,
    resumeStep,
    openProgressStream,
    type ApiOverview,
    type ProgressSnapshot
  } from '$lib/api-client';
  import { isDemoMode } from '$lib/app-mode';
  import type { StepAction } from '$lib/components/overview/StepControl.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import MobileRuns from '$lib/components/mobile/MobileRuns.svelte';

  const isMobile = new IsMobile();

  type StatusBannerType = 'success' | 'error' | 'info';

  let overview = $state<ApiOverview | null>(null);
  let liveProgress = $state<ProgressSnapshot | null>(null);
  let banner = $state<{ type: StatusBannerType; text: string } | null>(null);
  let triggering = $state<string | null>(null);
  let sseCleanup: (() => void) | null = null;
  let bannerTimeout: ReturnType<typeof setTimeout> | null = null;
  let elapsedMin = $state<number | null>(null);

  async function loadOverview() {
    try {
      overview = await fetchOverview();
    } catch {
      // silently ignore
    }
  }

  function connectSse() {
    if (isDemoMode) return;
    sseCleanup?.();
    sseCleanup = openProgressStream(
      (snapshot) => {
        liveProgress = snapshot;
      },
      () => {
        sseCleanup = null;
        void loadOverview();
        setTimeout(connectSse, 2000);
      }
    );
  }

  $effect(() => {
    void loadOverview();
    connectSse();
    const interval = setInterval(loadOverview, 15_000);
    return () => {
      clearInterval(interval);
      sseCleanup?.();
      if (bannerTimeout) clearTimeout(bannerTimeout);
    };
  });

  // Elapsed time clock — re-runs whenever the job's startedAt changes.
  $effect(() => {
    const startedAt = overview?.job?.startedAt;
    if (!startedAt) {
      elapsedMin = null;
      return;
    }
    const update = () => {
      elapsedMin = Math.floor((Date.now() - new Date(startedAt).getTime()) / 60_000);
    };
    update();
    const t = setInterval(update, 60_000);
    return () => clearInterval(t);
  });

  function showBanner(type: StatusBannerType, text: string) {
    banner = { type, text };
    if (bannerTimeout) clearTimeout(bannerTimeout);
    bannerTimeout = setTimeout(() => {
      banner = null;
    }, 6_000);
  }

  async function handleStepAction(step: string, action: StepAction) {
    triggering = step;
    try {
      if (action === 'start') {
        const fn =
          step === 'scan'
            ? triggerEnrichmentScan
            : step === 'fingerprint'
              ? triggerFingerprint
              : step === 'enrich'
                ? triggerEnrich
                : triggerBuild;
        const result = await fn();
        if (!result.ok) showBanner('error', result.message);
        else showBanner('success', `${step} started`);
      } else if (action === 'pause') {
        await pauseStep(step);
        showBanner('info', `${step} paused`);
      } else {
        await resumeStep(step);
        showBanner('info', `${step} resumed`);
      }
    } catch {
      showBanner('error', `Failed to ${action} ${step}. API may be unavailable.`);
    } finally {
      triggering = null;
    }
  }

  const job = $derived(
    overview?.job ?? {
      status: 'completed' as const,
      startedAt: new Date().toISOString(),
      tracksDiscovered: 0,
      tracksProcessed: 0,
      tracksFingerprinted: 0,
      tracksEnriched: 0,
      tracksBuildEligible: 0,
      tracksCopied: 0,
      tracksReview: 0,
      tracksFailed: 0
    }
  );

  const scanSnap = $derived(liveProgress?.scan);
  const fpSnap = $derived(liveProgress?.fingerprint);
  const enrichSnap = $derived(liveProgress?.enrich);
  const buildSnap = $derived(liveProgress?.build);

  const scanRunning = $derived(scanSnap?.status === 'Running');
  const fpRunning = $derived(fpSnap?.status === 'Running');
  const enrichRunning = $derived(enrichSnap?.status === 'Running');
  const buildRunning = $derived(buildSnap?.status === 'Running');

  const discovered = $derived(Math.max(liveProgress?.discovered ?? 0, job.tracksDiscovered));
  const scanned = $derived(
    scanRunning ? Math.max(liveProgress?.scanned ?? 0, job.tracksProcessed) : job.tracksProcessed
  );
  const fingerprinted = $derived(
    fpRunning
      ? Math.max(liveProgress?.fingerprinted ?? 0, job.tracksFingerprinted ?? 0)
      : (job.tracksFingerprinted ?? 0)
  );
  const enriched = $derived(
    enrichRunning
      ? Math.max(liveProgress?.enriched ?? 0, job.tracksEnriched ?? 0)
      : (job.tracksEnriched ?? 0)
  );
  const buildEligible = $derived(job.tracksBuildEligible ?? 0);
  const built = $derived(
    buildRunning ? Math.max(liveProgress?.built ?? 0, job.tracksCopied) : job.tracksCopied
  );

  const scanPct = $derived(discovered > 0 ? Math.min(100, (scanned / discovered) * 100) : 0);
  const fpPct = $derived(discovered > 0 ? Math.min(100, (fingerprinted / discovered) * 100) : 0);
  const enrichPct = $derived(discovered > 0 ? Math.min(100, (enriched / discovered) * 100) : 0);
  const buildPct = $derived(buildEligible > 0 ? Math.min(100, (built / buildEligible) * 100) : 0);

  const overallStatus = $derived.by(() => {
    const labels: string[] = [];
    if (scanRunning) labels.push('Scanning');
    if (fpRunning) labels.push('Fingerprinting');
    if (enrichRunning) labels.push('Enriching');
    if (buildRunning) labels.push('Building');
    return labels.length > 0 ? labels.join(', ') : 'Idle';
  });

  const enrichPaused = $derived(enrichSnap?.isPaused ?? false);
</script>

{#if isMobile.current}
  <MobileRuns />
{:else}
<main class="flex-1 p-4 md:p-6 lg:p-8">
  <div class="mx-auto max-w-7xl space-y-6">
    <div class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <h1 class="text-2xl font-bold md:text-3xl">Runs · history</h1>
        <p class="text-muted-foreground">
          Live pipeline status and recent ingest activity. Runs start automatically on launch.
        </p>
      </div>
    </div>

    {#if banner}
      <div
        class="rounded-md border px-3 py-2 text-sm {banner.type === 'error'
          ? 'border-red-500/30 bg-red-500/10 text-red-400'
          : banner.type === 'success'
            ? 'border-green-500/30 bg-green-500/10 text-green-400'
            : 'border-border bg-card text-muted-foreground'}"
      >
        <p>{banner.text}</p>
      </div>
    {/if}

    <Card>
      <CardContent class="p-4 md:p-6">
        <div class="flex flex-col gap-4 md:flex-row md:items-center md:gap-6">
          <div class="flex min-w-0 flex-1 items-center gap-3">
            <div class="bg-secondary flex size-10 shrink-0 items-center justify-center rounded-lg">
              <FolderInput class="text-muted-foreground size-5" />
            </div>
            <div class="min-w-0">
              <p class="text-muted-foreground text-xs">Source</p>
              <p class="truncate font-medium">{overview?.sourcePath ?? '—'}</p>
            </div>
          </div>
          <ArrowRight class="text-muted-foreground hidden size-5 md:block" />
          <div class="flex min-w-0 flex-1 items-center gap-3">
            <div class="bg-primary/10 flex size-10 shrink-0 items-center justify-center rounded-lg">
              <FolderOutput class="text-primary size-5" />
            </div>
            <div class="min-w-0">
              <p class="text-muted-foreground text-xs">Destination</p>
              <p class="truncate font-medium">{overview?.destinationPath ?? '—'}</p>
            </div>
          </div>
        </div>
      </CardContent>
    </Card>

    <Card>
      <CardHeader class="pb-3">
        <div class="flex flex-wrap items-center gap-x-3 gap-y-2">
          <CardTitle class="mr-auto text-lg">Pipeline</CardTitle>
          <div class="flex shrink-0 items-center gap-2">
            <div class="text-muted-foreground flex items-center gap-1.5 text-sm">
              <Clock class="size-4 shrink-0" />
              <span class="whitespace-nowrap">
                {elapsedMin !== null ? `${elapsedMin} min` : '—'}
              </span>
            </div>
            <StatusBadge status={overallStatus} />
          </div>
        </div>
      </CardHeader>
      <CardContent class="space-y-5">
        <PipelineStage
          icon={Search}
          label="Scan"
          count={scanned}
          total={discovered}
          unit="files"
          progress={scanPct}
          step={scanSnap}
          {triggering}
          stepKey="scan"
          onAction={handleStepAction}
          mode="trigger"
        />
        <PipelineStage
          icon={Disc3}
          label="Fingerprint"
          count={fingerprinted}
          total={discovered}
          unit="tracks"
          progress={fpPct}
          step={fpSnap}
          {triggering}
          stepKey="fingerprint"
          onAction={handleStepAction}
          mode="auto"
          subtitle="Runs automatically after scan"
        />
        <PipelineStage
          icon={Sparkles}
          label="Enrich"
          count={enriched}
          total={discovered}
          unit="tracks"
          progress={enrichPct}
          step={enrichSnap}
          {triggering}
          stepKey="enrich"
          onAction={handleStepAction}
          mode="continuous"
          subtitle={enrichPaused
            ? 'Paused — tracks will queue until resumed'
            : 'Processes tracks as they arrive from fingerprinting'}
        />
        <PipelineStage
          icon={PackageCheck}
          label="Build Library"
          count={built}
          total={buildEligible}
          unit="copied"
          progress={buildPct}
          step={buildSnap}
          {triggering}
          stepKey="build"
          onAction={handleStepAction}
          mode="trigger"
          subtitle={buildEligible < discovered && buildEligible > 0
            ? `${(discovered - buildEligible).toLocaleString()} tracks need review first`
            : undefined}
        />
      </CardContent>
    </Card>

    <div class="grid gap-4 sm:grid-cols-2 lg:grid-cols-5">
      <StatCard
        icon={Disc3}
        label="Discovered"
        value={discovered}
        color="text-foreground"
        bgColor="bg-secondary"
      />
      <StatCard
        icon={Sparkles}
        label="Enriched"
        value={job.tracksEnriched ?? 0}
        color="text-blue-400"
        bgColor="bg-blue-400/10"
      />
      <StatCard
        icon={Copy}
        label="Copied"
        value={job.tracksCopied}
        color="text-primary"
        bgColor="bg-primary/10"
      />
      <StatCard
        icon={FileWarning}
        label="Need Review"
        value={job.tracksReview}
        color="text-amber-400"
        bgColor="bg-amber-400/10"
        href="/review"
      />
      <StatCard
        icon={XCircle}
        label="Failed"
        value={job.tracksFailed}
        color="text-red-400"
        bgColor="bg-red-400/10"
      />
    </div>

    <div class="grid grid-cols-1 gap-6 lg:grid-cols-3">
      <Card class="lg:col-span-2">
        <CardHeader class="pb-2">
          <CardTitle class="text-lg">Recent Activity</CardTitle>
        </CardHeader>
        <CardContent class="p-0">
          <ScrollArea class="h-[320px]">
            <div class="space-y-1 p-4 pt-0">
              {#if (overview?.recentActivity ?? []).length > 0}
                {#each overview?.recentActivity ?? [] as activity (activity.id)}
                  <ActivityItem {activity} />
                {/each}
              {:else}
                <p class="text-muted-foreground py-4 text-center text-sm">
                  No recent activity yet
                </p>
              {/if}
            </div>
          </ScrollArea>
        </CardContent>
      </Card>

      <Card>
        <CardHeader class="pb-2">
          <CardTitle class="text-lg">Quick Actions</CardTitle>
        </CardHeader>
        <CardContent class="space-y-3">
          <Button
            variant="outline"
            class="hover:bg-muted hover:text-foreground dark:hover:bg-muted/50 h-auto w-full justify-start gap-3 py-3"
            disabled={triggering === 'scan' || scanRunning}
            onclick={() => handleStepAction('scan', 'start')}
          >
            <div class="bg-secondary flex size-8 shrink-0 items-center justify-center rounded-lg">
              <RefreshCw
                class="text-muted-foreground size-4 {scanRunning ? 'animate-spin' : ''}"
              />
            </div>
            <div class="min-w-0 text-left whitespace-normal">
              <p class="font-medium">Re-scan Source</p>
              <p class="text-muted-foreground text-xs">
                {scanRunning ? 'Scanning...' : 'Check for new or changed files'}
              </p>
            </div>
          </Button>
          <Button
            variant="outline"
            class="hover:bg-muted hover:text-foreground dark:hover:bg-muted/50 h-auto w-full justify-start gap-3 py-3"
            href="/review"
          >
            <div
              class="flex size-8 shrink-0 items-center justify-center rounded-lg bg-amber-400/10"
            >
              <FileWarning class="size-4 text-amber-400" />
            </div>
            <div class="min-w-0 text-left whitespace-normal">
              <p class="font-medium">Review Tracks</p>
              <p class="text-muted-foreground text-xs">
                {job.tracksReview} tracks need attention
              </p>
            </div>
          </Button>
          <Button
            variant="outline"
            class="hover:bg-muted hover:text-foreground dark:hover:bg-muted/50 h-auto w-full justify-start gap-3 py-3"
            href="/app"
          >
            <div class="bg-primary/10 flex size-8 shrink-0 items-center justify-center rounded-lg">
              <Music class="text-primary size-4" />
            </div>
            <div class="min-w-0 text-left whitespace-normal">
              <p class="font-medium">Browse Library</p>
              <p class="text-muted-foreground text-xs">View imported tracks</p>
            </div>
          </Button>
        </CardContent>
      </Card>
    </div>
  </div>
</main>
{/if}

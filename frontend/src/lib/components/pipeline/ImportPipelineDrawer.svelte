<script lang="ts">
  import { fly } from 'svelte/transition';
  import { cubicOut } from 'svelte/easing';
  import {
    ScanLine,
    Disc3,
    Sparkles,
    PackageCheck,
    ArrowRight,
    Loader2,
    Square,
    X
  } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import {
    pipelineOverlay,
    JOB_CONFIRM_COPY,
    type JobConfirm,
    type StageKey
  } from '$lib/stores/pipeline-overlay.svelte';
  import PipelineStageCard from './PipelineStageCard.svelte';
  import PipelineLogRow from './PipelineLogRow.svelte';

  const snap = $derived(pipelineOverlay.snapshot);
  const overview = $derived(pipelineOverlay.overview);
  const rates = $derived(pipelineOverlay.rates);

  const sourcePath = $derived(overview?.sourcePath ?? '—');
  const destinationPath = $derived(overview?.destinationPath ?? '—');

  const processed = $derived(pipelineOverlay.processed);
  const remaining = $derived(pipelineOverlay.remaining);
  const etaSeconds = $derived(pipelineOverlay.etaSeconds);
  const anyRunning = $derived(pipelineOverlay.isAnyRunning);

  const discovered = $derived(snap?.discovered ?? 0);
  const buildTarget = $derived(overview?.job?.tracksBuildEligible || snap?.enriched || discovered);

  function formatEta(seconds: number | null): string {
    if (seconds == null) return '—';
    const s = Math.max(0, seconds);
    const h = Math.floor(s / 3600);
    const m = Math.floor((s % 3600) / 60);
    const sec = s % 60;
    return [h, m, sec].map((n) => n.toString().padStart(2, '0')).join(':');
  }

  const recent = $derived(overview?.recentActivity ?? []);

  // Job control. Feedback is the stage itself changing (Paused badge, pulse stopping); the store
  // toasts only on failure. Confirm state is local so the conveyor page, when it is open under
  // this drawer, never pops a second dialog.
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

  function togglePause(key: StageKey) {
    const paused = pipelineOverlay.isStagePaused(key);
    if (!paused && key === 'build' && pipelineOverlay.needsConfirm('pause-build')) ask('pause-build');
    else void pipelineOverlay.setStagePaused(key, !paused);
  }

  function confirmAction() {
    if (confirmKind === 'stop') void pipelineOverlay.cancelRunning();
    else void pipelineOverlay.setStagePaused('build', true);
  }
</script>

<aside
  class="bg-card border-border shadow-pipeline fixed right-0 bottom-0 left-0 z-40 hidden h-[340px] flex-col border-t md:flex"
  transition:fly={{ y: 40, duration: 250, easing: cubicOut, opacity: 0 }}
>
  <header class="border-border flex items-center justify-between gap-4 border-b px-5 py-3">
    <div class="flex min-w-0 items-center gap-3">
      <span
        class="bg-primary relative inline-flex size-2.5 shrink-0 rounded-full"
        class:animate-pulse={anyRunning}
        aria-hidden="true"
      ></span>
      <div class="min-w-0">
        <div class="text-sm font-semibold">Import pipeline</div>
        <div class="text-muted-foreground flex min-w-0 items-center gap-1.5 font-mono text-[11px]">
          <span class="truncate">{sourcePath}</span>
          <ArrowRight class="size-3 shrink-0" />
          <span class="truncate">{destinationPath}</span>
        </div>
      </div>
    </div>

    <div class="flex shrink-0 items-center gap-5">
      <div class="hidden text-right md:block">
        <div class="text-muted-foreground text-[11px] font-semibold tracking-wider uppercase">
          Processed
        </div>
        <div class="font-mono text-base font-semibold tabular-nums">
          {processed.toLocaleString()}
        </div>
      </div>
      <div class="hidden text-right md:block">
        <div class="text-muted-foreground text-[11px] font-semibold tracking-wider uppercase">
          Remaining
        </div>
        <div class="font-mono text-base font-semibold tabular-nums">
          {remaining.toLocaleString()}
        </div>
      </div>
      <div class="text-right">
        <div class="text-muted-foreground text-[11px] font-semibold tracking-wider uppercase">
          ETA
        </div>
        <div class="font-mono text-base font-semibold tabular-nums">
          {formatEta(etaSeconds)}
        </div>
      </div>
      {#if pipelineOverlay.canStop}
        <button
          type="button"
          onclick={requestStop}
          disabled={pipelineOverlay.cancelling}
          class="border-border bg-background hover:bg-muted text-foreground inline-flex h-8 items-center gap-1.5 rounded-md border px-2.5 text-[12px] font-medium transition-colors disabled:opacity-60"
        >
          {#if pipelineOverlay.cancelling}
            <Loader2 class="size-3.5 animate-spin" />
            Stopping…
          {:else}
            <Square class="size-3 fill-current" />
            Stop all
          {/if}
        </button>
      {/if}
      <button
        type="button"
        class="text-muted-foreground hover:bg-muted hover:text-foreground grid size-8 place-items-center rounded"
        aria-label="Close pipeline overlay"
        onclick={() => pipelineOverlay.setOpen(false)}
      >
        <X class="size-4" />
      </button>
    </div>
  </header>

  <div class="grid min-h-0 flex-1 grid-cols-1 md:grid-cols-[1fr_420px]">
    <div class="overflow-y-auto p-4">
      <div class="grid grid-cols-1 gap-2.5 sm:grid-cols-2 lg:grid-cols-4">
        <PipelineStageCard
          icon={ScanLine}
          label="Scanning"
          status={snap?.scan?.status}
          isPaused={pipelineOverlay.isStagePaused('scan')}
          onTogglePause={() => togglePause('scan')}
          pauseBusy={pipelineOverlay.isStageBusy('scan')}
          count={snap?.scanned ?? 0}
          total={discovered}
          perSec={rates.scan}
        />
        <PipelineStageCard
          icon={Disc3}
          label="Fingerprinting"
          status={snap?.fingerprint?.status}
          isPaused={pipelineOverlay.isStagePaused('fingerprint')}
          onTogglePause={() => togglePause('fingerprint')}
          pauseBusy={pipelineOverlay.isStageBusy('fingerprint')}
          count={snap?.fingerprinted ?? 0}
          total={discovered}
          perSec={rates.fingerprint}
        />
        <PipelineStageCard
          icon={Sparkles}
          label="Enrichment"
          status={snap?.enrich?.status}
          isPaused={pipelineOverlay.isStagePaused('enrich')}
          onTogglePause={() => togglePause('enrich')}
          pauseBusy={pipelineOverlay.isStageBusy('enrich')}
          count={snap?.enriched ?? 0}
          total={discovered}
          perSec={rates.enrich}
        />
        <PipelineStageCard
          icon={PackageCheck}
          label="Writing"
          status={snap?.build?.status}
          isPaused={pipelineOverlay.isStagePaused('build')}
          onTogglePause={() => togglePause('build')}
          pauseBusy={pipelineOverlay.isStageBusy('build')}
          count={snap?.built ?? 0}
          total={buildTarget}
          perSec={rates.build}
        />
      </div>
    </div>

    <div class="bg-muted/30 border-border flex min-h-0 flex-col border-t md:border-t-0 md:border-l">
      <div
        class="text-muted-foreground border-border flex items-center justify-between border-b px-4 py-2.5 text-[11px] font-semibold tracking-wide uppercase"
      >
        <span>Live log</span>
        <span class="text-primary font-mono normal-case tracking-normal">tail -f</span>
      </div>
      <ScrollArea class="min-h-0 flex-1">
        <div class="space-y-0.5 px-3 py-2">
          {#if recent.length > 0}
            {#each recent as activity, i (activity.id)}
              <PipelineLogRow {activity} faded={i} />
            {/each}
          {:else}
            <p class="text-muted-foreground-dim px-1 py-2 text-center font-mono text-[11px]">
              No recent activity yet
            </p>
          {/if}
        </div>
      </ScrollArea>
    </div>
  </div>
</aside>

<AlertDialog.Root bind:open={confirmOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>{confirmCopy.title}</AlertDialog.Title>
      <AlertDialog.Description>{confirmCopy.description}</AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Keep running</AlertDialog.Cancel>
      <AlertDialog.Action variant="destructive" onclick={confirmAction}>{confirmCopy.action}</AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<style>
  /* Slightly softer top shadow than the default Tailwind shadow-lg —
     mirrors `box-shadow: 0 -12px 40px rgba(0,0,0,0.08)` from the prototype. */
  .shadow-pipeline {
    box-shadow: 0 -12px 40px rgba(0, 0, 0, 0.08);
  }
</style>

<script lang="ts">
  import { Loader2, OctagonX, Pause, Play } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { Button } from '$lib/components/ui/button';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import {
    cancelJob,
    fetchJobStatus,
    pauseStep,
    resumeStep,
    type ProgressSnapshot,
    type StepSnapshot
  } from '$lib/api-client';
  import { cn } from '$lib/utils';

  // The demo account is read-only — the backend rejects these regardless, so hide them rather
  // than leaving dead buttons.
  let { isDemo = false }: { isDemo?: boolean } = $props();

  // ── what the backend actually offers ─────────────────────────────────────────
  // Naming here is deliberate. `POST /api/enrichment/pause` does NOT suspend a step: it sets a
  // flag and, if the step is running, cancels it outright. There is no resume-from-position —
  // `resume` only clears the flag, and the step restarts from the beginning next time something
  // triggers it. So the control is an auto-run gate, and calling it "Hold" is the honest name.
  //
  // Cancel is global. `POST /api/enrichment/cancel` takes no arguments and stops every running
  // step, so it is one button here rather than a per-row action pretending otherwise.
  type StepId = 'scan' | 'fingerprint' | 'enrich' | 'build';

  // `purge` is deliberately unparseable at the endpoint, and `download` is driven by the
  // wishlist rather than being a stage you would hold, so neither is exposed.
  const STEPS: { id: StepId; label: string; body: string }[] = [
    { id: 'scan', label: 'Scan', body: 'Indexes new files from the source share.' },
    {
      id: 'fingerprint',
      label: 'Fingerprint',
      body: 'Computes the acoustic fingerprint each provider matches against.'
    },
    { id: 'enrich', label: 'Match', body: 'Asks every enabled provider to identify each track.' },
    {
      id: 'build',
      label: 'Build',
      body: 'Copies matched tracks to the destination library and writes their tags.'
    }
  ];

  // Read from the status endpoint rather than the SSE snapshot: the stream fires on *progress*,
  // so an idle step's hold flag would not repaint until something else happened. Polling here
  // keeps the pills honest, and a mutation refreshes immediately.
  let status = $state<ProgressSnapshot | null>(null);
  let pending = $state<StepId | 'all' | null>(null);
  /** The step a hold has been requested for while it is running — drives the confirm dialog. */
  let confirming = $state<StepId | null>(null);
  let confirmStopAll = $state(false);

  async function refresh(): Promise<void> {
    try {
      status = (await fetchJobStatus()).progress;
    } catch {
      // Keep the last good snapshot; a blip should not blank the controls.
    }
  }

  $effect(() => {
    void refresh();
    const timer = setInterval(() => void refresh(), 5_000);
    return () => clearInterval(timer);
  });

  function snapFor(id: StepId): StepSnapshot | undefined {
    return status?.[id];
  }

  const anythingRunning = $derived(
    status != null &&
      (STEPS.some((s) => status?.[s.id]?.status === 'Running') ||
        status?.download?.status === 'Running')
  );

  type Tone = 'run' | 'held' | 'fail' | 'idle';

  function stepState(s: StepSnapshot | undefined): { label: string; tone: Tone } {
    if (!s) return { label: '—', tone: 'idle' };
    // A held step that is still winding down reports Running with the flag already set.
    if (s.status === 'Running') {
      return s.isPaused ? { label: 'Stopping', tone: 'held' } : { label: 'Running', tone: 'run' };
    }
    if (s.isPaused) return { label: 'Held', tone: 'held' };
    if (s.status === 'Failed') return { label: 'Failed', tone: 'fail' };
    return { label: 'Idle', tone: 'idle' };
  }

  const TONE_CLASS: Record<Tone, string> = {
    run: 'text-primary',
    held: 'text-muted-foreground',
    fail: 'text-destructive',
    idle: 'text-muted-foreground'
  };

  function labelFor(id: StepId): string {
    return STEPS.find((s) => s.id === id)?.label ?? id;
  }

  async function hold(id: StepId): Promise<void> {
    pending = id;
    try {
      await pauseStep(id);
      toast.success(`${labelFor(id)} held — it won't start on its own.`);
      await refresh();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : `Could not hold ${labelFor(id)}.`);
    } finally {
      pending = null;
    }
  }

  async function release(id: StepId): Promise<void> {
    pending = id;
    try {
      await resumeStep(id);
      // Deliberately not "resumed": nothing restarts here. The step becomes eligible again and
      // waits for the next automatic sweep or a manual run.
      toast.success(`${labelFor(id)} released — it can run again.`);
      await refresh();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : `Could not release ${labelFor(id)}.`);
    } finally {
      pending = null;
    }
  }

  /** Holding an idle step is harmless; holding a running one throws away the pass, so confirm. */
  function requestHold(id: StepId): void {
    if (snapFor(id)?.status === 'Running') confirming = id;
    else void hold(id);
  }

  async function stopAll(): Promise<void> {
    pending = 'all';
    try {
      const res = await cancelJob();
      toast.success(res.message ?? 'Cancellation requested.');
      await refresh();
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not stop the running steps.');
    } finally {
      pending = null;
    }
  }
</script>

<section aria-label="Step control">
  <div class="mb-5 flex flex-wrap items-baseline gap-x-2 gap-y-1">
    <h2 class="text-[13px] font-semibold">Step control</h2>
    <span class="text-muted-foreground text-[12px]">
      Holding a step stops it running automatically.
    </span>
  </div>

  <div class="border-border divide-border divide-y rounded-lg border">
    {#each STEPS as step (step.id)}
      {@const snap = snapFor(step.id)}
      {@const state = stepState(snap)}
      {@const held = snap?.isPaused === true}
      <div class="flex flex-col gap-3 p-4 sm:flex-row sm:items-center sm:justify-between">
        <div class="min-w-0 flex-1 pr-4">
          <div class="flex items-center gap-2">
            <h3 class="text-[13px] font-semibold">{step.label}</h3>
            <span class={cn('text-[11px] font-medium', TONE_CLASS[state.tone])}>
              {state.label}
            </span>
          </div>
          <p class="text-muted-foreground mt-1 text-[12px]">{step.body}</p>
        </div>
        {#if !isDemo}
          <Button
            variant="outline"
            size="sm"
            class="shrink-0 gap-1.5"
            disabled={pending !== null}
            onclick={() => (held ? release(step.id) : requestHold(step.id))}
          >
            {#if pending === step.id}
              <Loader2 class="size-3.5 animate-spin" />
            {:else if held}
              <Play class="size-3.5" />
            {:else}
              <Pause class="size-3.5" />
            {/if}
            {held ? 'Release' : 'Hold'}
          </Button>
        {/if}
      </div>
    {/each}
  </div>

  {#if !isDemo}
    <div class="mt-3 flex flex-wrap items-center justify-between gap-3">
      <p class="text-muted-foreground text-[12px]">
        Stopping is all-or-nothing — the pipeline has no per-step cancel.
      </p>
      <Button
        variant="outline"
        size="sm"
        class="text-destructive hover:text-destructive shrink-0 gap-1.5"
        disabled={!anythingRunning || pending !== null}
        onclick={() => (confirmStopAll = true)}
      >
        {#if pending === 'all'}
          <Loader2 class="size-3.5 animate-spin" />
        {:else}
          <OctagonX class="size-3.5" />
        {/if}
        Stop all
      </Button>
    </div>
  {/if}
</section>

<!-- Holding a *running* step discards the current pass. Say so before doing it. -->
<AlertDialog.Root
  open={confirming !== null}
  onOpenChange={(v) => {
    if (!v) confirming = null;
  }}
>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>
        Hold {confirming ? labelFor(confirming) : ''} while it's running?
      </AlertDialog.Title>
      <AlertDialog.Description>
        This stops the current pass immediately. There is no resume point — when you release it,
        {confirming ? labelFor(confirming) : 'the step'} starts again from the beginning. Anything already
        written to the library is kept.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action
        onclick={() => {
          const id = confirming;
          confirming = null;
          if (id) void hold(id);
        }}
      >
        Stop and hold
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<AlertDialog.Root bind:open={confirmStopAll}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Stop every running step?</AlertDialog.Title>
      <AlertDialog.Description>
        Cancelling is all-or-nothing — every running step stops, not just one. Steps are not held,
        so anything eligible will start again on the next automatic sweep. Hold a step first if you
        want it to stay stopped.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action onclick={() => void stopAll()}>Stop all</AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

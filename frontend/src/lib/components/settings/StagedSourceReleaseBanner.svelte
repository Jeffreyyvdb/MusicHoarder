<script module lang="ts">
  import type { StagedSourceReleaseSnapshot } from '$lib/api-client';

  /**
   * What a screen reader hears when a release starts and when it ends — spoken by Settings'
   * always-mounted live region (see purgeAnnouncement for why not from here).
   */
  export function releaseAnnouncement(snapshot: StagedSourceReleaseSnapshot): string {
    if (snapshot.status === 'running') return 'Releasing staged downloads';
    if (snapshot.status === 'completed') return 'Staged downloads released';
    if (snapshot.status === 'cancelled') return 'Release stopped';
    if (snapshot.status === 'failed') return 'Release failed';
    return '';
  }
</script>

<script lang="ts">
  import { CircleAlert, CircleCheck, CircleStop, Loader2 } from '@lucide/svelte';
  import { formatFileSize } from '$lib/formatters';

  // A staged-source release's progress as the last cell of the Download staging section — same
  // shape as PurgeStatusBanner: spinner and bar while running, then a glyph and the outcome.
  const formatBytes = (bytes: number) => (bytes > 0 ? formatFileSize(bytes) : '0 B');

  type Props = { snapshot: StagedSourceReleaseSnapshot };
  const { snapshot }: Props = $props();

  const handled = $derived(
    snapshot.released +
      snapshot.alreadyMissing +
      snapshot.skippedVerification +
      snapshot.raced +
      snapshot.failed
  );
  const pct = $derived(
    Math.min(100, snapshot.candidates > 0 ? (handled / snapshot.candidates) * 100 : 0)
  );
  const skipped = $derived(snapshot.skippedVerification + snapshot.raced + snapshot.failed);
</script>

<!-- Not a live region: the counts change on every 1.5s poll, and VoiceOver would read each new
     sentence for the length of the run. Settings announces the start and the end once
     (releaseAnnouncement); the progressbar carries the value for anyone who asks. -->
<div data-slot="grouped-list-row" class="relative flex items-start gap-3 px-4 py-3">
  {#if snapshot.status === 'running'}
    <Loader2 class="text-muted-foreground mt-0.5 size-5 shrink-0 animate-spin" aria-hidden="true" />
    <div class="min-w-0 flex-1">
      <p class="text-body md:text-sm">Releasing staged downloads…</p>
      <p class="text-subheadline text-muted-foreground md:text-xs">
        <span class="tabular-nums">{handled.toLocaleString()}</span> of
        <span class="tabular-nums">{snapshot.candidates.toLocaleString()}</span> checked,
        <span class="tabular-nums">{snapshot.released.toLocaleString()}</span> released,
        {formatBytes(snapshot.bytesReclaimed)} reclaimed.
      </p>
      <span
        role="progressbar"
        aria-label="Release progress"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(pct)}
        class="bg-muted mt-2 block h-1 w-full overflow-hidden rounded-full"
      >
        <span
          class="bg-primary block h-full w-full origin-left transition-transform duration-300"
          style="transform: scaleX({pct / 100})"
        ></span>
      </span>
    </div>
  {:else if snapshot.status === 'completed'}
    <CircleCheck class="text-primary mt-0.5 size-5 shrink-0" aria-hidden="true" />
    <div class="min-w-0 flex-1">
      <p class="text-body md:text-sm">Staged downloads released</p>
      <p class="text-subheadline text-muted-foreground md:text-xs">
        Released {snapshot.released.toLocaleString()} staged
        {snapshot.released === 1 ? 'copy' : 'copies'} and reclaimed {formatBytes(
          snapshot.bytesReclaimed
        )}.
        {#if snapshot.alreadyMissing > 0}
          {snapshot.alreadyMissing.toLocaleString()} were already gone.
        {/if}
        {#if skipped > 0}
          <span class="mt-1 block">
            {skipped.toLocaleString()} left in place — {snapshot.skippedVerification.toLocaleString()}
            failed verification, {snapshot.raced.toLocaleString()} changed mid-run, {snapshot.failed.toLocaleString()}
            could not be deleted (see server logs).
          </span>
        {/if}
      </p>
    </div>
  {:else if snapshot.status === 'cancelled'}
    <CircleStop class="text-muted-foreground mt-0.5 size-5 shrink-0" aria-hidden="true" />
    <div class="min-w-0 flex-1">
      <p class="text-body md:text-sm">Release stopped</p>
      <p class="text-subheadline text-muted-foreground md:text-xs">
        Stopped after releasing {snapshot.released.toLocaleString()}; the rest are picked up by the
        next sweep.
      </p>
    </div>
  {:else}
    <CircleAlert class="text-destructive-text mt-0.5 size-5 shrink-0" aria-hidden="true" />
    <div class="min-w-0 flex-1">
      <p class="text-body text-destructive-text md:text-sm">Release failed</p>
      <p class="text-subheadline text-muted-foreground md:text-xs">
        {snapshot.error ?? 'Unknown error — check server logs.'}
      </p>
    </div>
  {/if}
</div>

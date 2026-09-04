<script lang="ts">
  import type { StagedSourceReleaseSnapshot } from '$lib/api-client';
  import { CheckCircle2, AlertCircle } from '@lucide/svelte';
  import * as Alert from '$lib/components/ui/alert/index.js';
  import { Progress } from '$lib/components/ui/progress/index.js';
  import { Spinner } from '$lib/components/ui/spinner/index.js';
  import { formatFileSize } from '$lib/formatters';

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
  const pct = $derived(snapshot.candidates > 0 ? (handled / snapshot.candidates) * 100 : 0);
  const skipped = $derived(snapshot.skippedVerification + snapshot.raced + snapshot.failed);
</script>

{#if snapshot.status === 'running'}
  <Alert.Root class="bg-secondary/30">
    <Spinner class="text-muted-foreground size-4" />
    <Alert.Title>Releasing staged downloads…</Alert.Title>
    <Alert.Description class="text-muted-foreground">
      <span class="tabular-nums">{handled.toLocaleString()}</span> /
      <span class="tabular-nums">{snapshot.candidates.toLocaleString()}</span> checked,
      <span class="tabular-nums">{snapshot.released.toLocaleString()}</span> released,
      {formatBytes(snapshot.bytesReclaimed)} reclaimed.
    </Alert.Description>
    <Progress value={Math.min(100, pct)} class="mt-2 h-1.5" />
  </Alert.Root>
{:else if snapshot.status === 'completed'}
  <Alert.Root class="border-[#1DB954]/50 bg-[#1DB954]/10 text-[#1DB954]">
    <CheckCircle2 class="size-4" />
    <Alert.Title>Staged downloads released</Alert.Title>
    <Alert.Description class="text-[#1DB954] opacity-90">
      Released {snapshot.released.toLocaleString()} staged
      {snapshot.released === 1 ? 'copy' : 'copies'} and reclaimed {formatBytes(
        snapshot.bytesReclaimed
      )}.
      {#if snapshot.alreadyMissing > 0}
        {snapshot.alreadyMissing.toLocaleString()} were already gone.
      {/if}
      {#if skipped > 0}
        <span class="mt-1 block">
          {skipped.toLocaleString()} left in place — {snapshot.skippedVerification.toLocaleString()} failed
          verification, {snapshot.raced.toLocaleString()} changed mid-run, {snapshot.failed.toLocaleString()}
          could not be deleted (see server logs).
        </span>
      {/if}
    </Alert.Description>
  </Alert.Root>
{:else if snapshot.status === 'cancelled'}
  <Alert.Root class="bg-secondary/30">
    <AlertCircle class="text-muted-foreground size-4" />
    <Alert.Title>Release stopped</Alert.Title>
    <Alert.Description class="text-muted-foreground">
      Stopped after releasing {snapshot.released.toLocaleString()}; the rest are picked up by the
      next sweep.
    </Alert.Description>
  </Alert.Root>
{:else}
  <Alert.Root variant="destructive">
    <AlertCircle class="size-4" />
    <Alert.Title>Release failed</Alert.Title>
    <Alert.Description class="opacity-90">
      {snapshot.error ?? 'Unknown error — check server logs.'}
    </Alert.Description>
  </Alert.Root>
{/if}

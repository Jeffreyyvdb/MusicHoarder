<script lang="ts">
  import type { PurgeSnapshot } from '$lib/api-client';
  import { CheckCircle2, AlertCircle } from '@lucide/svelte';
  import * as Alert from '$lib/components/ui/alert/index.js';
  import { Progress } from '$lib/components/ui/progress/index.js';
  import { Spinner } from '$lib/components/ui/spinner/index.js';

  type Props = { snapshot: PurgeSnapshot };
  const { snapshot }: Props = $props();

  const modeLabel = $derived(
    snapshot.mode === 'post-fingerprint' ? 'Reset enrichment data' : 'Purge all data'
  );

  const songsPct = $derived(
    snapshot.songsTotal > 0 ? (snapshot.songsProcessed / snapshot.songsTotal) * 100 : 0
  );
  const filesPct = $derived(
    snapshot.filesTotal > 0
      ? ((snapshot.filesDeleted + snapshot.filesFailed) / snapshot.filesTotal) * 100
      : 0
  );
  const overallPct = $derived(snapshot.filesTotal > 0 ? filesPct : songsPct);
</script>

{#if snapshot.status === 'running'}
  <Alert.Root class="bg-secondary/30">
    <Spinner class="text-muted-foreground size-4" />
    <Alert.Title>{modeLabel} running…</Alert.Title>
    <Alert.Description class="text-muted-foreground">
      {#if snapshot.filesTotal > 0}
        {snapshot.filesDeleted.toLocaleString()} / {snapshot.filesTotal.toLocaleString()} destination
        files deleted{#if snapshot.filesFailed > 0}
          &nbsp;({snapshot.filesFailed.toLocaleString()} failed){/if}.
      {:else}
        Preparing {snapshot.songsTotal.toLocaleString()} songs…
      {/if}
    </Alert.Description>
    <Progress value={Math.min(100, overallPct)} class="mt-2 h-1.5" />
  </Alert.Root>
{:else if snapshot.status === 'completed'}
  {@const prefix = snapshot.mode === 'post-fingerprint' ? 'Reset' : 'Deleted'}
  <Alert.Root class="border-[#1DB954]/50 bg-[#1DB954]/10 text-[#1DB954]">
    <CheckCircle2 class="size-4" />
    <Alert.Title>{modeLabel} complete</Alert.Title>
    <Alert.Description class="text-[#1DB954] opacity-90">
      {prefix}
      {snapshot.songsProcessed.toLocaleString()} songs, removed {snapshot.filesDeleted.toLocaleString()}
      destination files, cleared {snapshot.spotifyMatchesCleared.toLocaleString()} Spotify matches.
      {#if snapshot.filesFailed > 0}
        <span class="mt-1 block">
          {snapshot.filesFailed.toLocaleString()} file{snapshot.filesFailed === 1 ? '' : 's'} could not
          be deleted (see server logs).
        </span>
      {/if}
    </Alert.Description>
  </Alert.Root>
{:else}
  <Alert.Root variant="destructive">
    <AlertCircle class="size-4" />
    <Alert.Title>{modeLabel} failed</Alert.Title>
    <Alert.Description class="opacity-90">
      {snapshot.error ?? 'Unknown error — check server logs.'}
    </Alert.Description>
  </Alert.Root>
{/if}

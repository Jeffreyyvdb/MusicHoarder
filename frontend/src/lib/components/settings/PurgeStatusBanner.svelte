<script lang="ts">
  import type { PurgeSnapshot } from '$lib/api-client';
  import { Loader2, CheckCircle2, AlertCircle } from '@lucide/svelte';

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
  <div
    class="border-border bg-secondary/30 flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
  >
    <Loader2 class="text-muted-foreground mt-0.5 size-4 shrink-0 animate-spin" />
    <div class="min-w-0 flex-1">
      <p class="font-medium">{modeLabel} running…</p>
      <p class="text-muted-foreground text-xs">
        {#if snapshot.filesTotal > 0}
          {snapshot.filesDeleted.toLocaleString()} / {snapshot.filesTotal.toLocaleString()} destination
          files deleted{#if snapshot.filesFailed > 0}
            &nbsp;({snapshot.filesFailed.toLocaleString()} failed){/if}.
        {:else}
          Preparing {snapshot.songsTotal.toLocaleString()} songs…
        {/if}
      </p>
      <div class="bg-secondary mt-2 h-1.5 w-full overflow-hidden rounded-full">
        <div
          class="bg-primary h-full rounded-full transition-[width] duration-300"
          style="width: {Math.min(100, overallPct).toFixed(1)}%"
        ></div>
      </div>
    </div>
  </div>
{:else if snapshot.status === 'completed'}
  {@const prefix = snapshot.mode === 'post-fingerprint' ? 'Reset' : 'Deleted'}
  <div
    class="flex items-start gap-2 rounded-lg border border-[#1DB954]/50 bg-[#1DB954]/10 px-4 py-3 text-sm text-[#1DB954]"
  >
    <CheckCircle2 class="mt-0.5 size-4 shrink-0" />
    <div>
      <p class="font-medium">{modeLabel} complete</p>
      <p class="text-xs opacity-90">
        {prefix}
        {snapshot.songsProcessed.toLocaleString()} songs, removed {snapshot.filesDeleted.toLocaleString()}
        destination files, cleared {snapshot.spotifyMatchesCleared.toLocaleString()} Spotify matches.
      </p>
      {#if snapshot.filesFailed > 0}
        <p class="mt-1 text-xs opacity-90">
          {snapshot.filesFailed.toLocaleString()} file{snapshot.filesFailed === 1 ? '' : 's'} could not
          be deleted (see server logs).
        </p>
      {/if}
    </div>
  </div>
{:else}
  <div
    class="border-destructive/50 bg-destructive/10 text-destructive flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
  >
    <AlertCircle class="mt-0.5 size-4 shrink-0" />
    <div>
      <p class="font-medium">{modeLabel} failed</p>
      <p class="text-xs opacity-90">{snapshot.error ?? 'Unknown error — check server logs.'}</p>
    </div>
  </div>
{/if}

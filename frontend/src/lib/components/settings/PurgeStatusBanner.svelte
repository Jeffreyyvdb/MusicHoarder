<script module lang="ts">
  import type { PurgeSnapshot } from '$lib/api-client';

  export function purgeModeLabel(snapshot: PurgeSnapshot): string {
    return snapshot.mode === 'post-fingerprint' ? 'Reset enrichment data' : 'Purge all data';
  }

  /**
   * What a screen reader hears when a purge starts and when it ends. Settings speaks it through
   * one live region that is always mounted: a region inserted already holding its text (as this
   * banner is, the moment a purge starts) is not announced, so only the end was ever heard.
   */
  export function purgeAnnouncement(snapshot: PurgeSnapshot): string {
    const label = purgeModeLabel(snapshot);
    if (snapshot.status === 'running') return `${label} started`;
    if (snapshot.status === 'completed') return `${label} complete`;
    if (snapshot.status === 'failed') return `${label} failed`;
    return '';
  }
</script>

<script lang="ts">
  import { CircleAlert, CircleCheck, Loader2 } from '@lucide/svelte';

  // The purge's progress as the last cell of the Danger zone section: a spinner and a thin
  // determinate bar while it runs, then its outcome. Success is a check glyph plus words, never
  // green text; a failure speaks in the destructive text token.
  type Props = { snapshot: PurgeSnapshot };
  const { snapshot }: Props = $props();

  const modeLabel = $derived(purgeModeLabel(snapshot));

  const songsPct = $derived(
    snapshot.songsTotal > 0 ? (snapshot.songsProcessed / snapshot.songsTotal) * 100 : 0
  );
  const filesPct = $derived(
    snapshot.filesTotal > 0
      ? ((snapshot.filesDeleted + snapshot.filesFailed) / snapshot.filesTotal) * 100
      : 0
  );
  const overallPct = $derived(Math.min(100, snapshot.filesTotal > 0 ? filesPct : songsPct));
</script>

<!-- Not a live region: the counts change on every 1.5s poll, and VoiceOver would read each new
     sentence for the length of the run. Settings announces the start and the end once
     (purgeAnnouncement); the progressbar carries the value for anyone who asks. -->
<div data-slot="grouped-list-row" class="relative flex items-start gap-3 px-4 py-3">
  {#if snapshot.status === 'running'}
    <Loader2 class="text-muted-foreground mt-0.5 size-5 shrink-0 animate-spin" aria-hidden="true" />
    <div class="min-w-0 flex-1">
      <p class="text-body md:text-sm">{modeLabel} running…</p>
      <p class="text-subheadline text-muted-foreground md:text-xs">
        {#if snapshot.filesTotal > 0}
          <span class="tabular-nums">{snapshot.filesDeleted.toLocaleString()}</span> of
          <span class="tabular-nums">{snapshot.filesTotal.toLocaleString()}</span> destination files
          deleted{#if snapshot.filesFailed > 0}
            &nbsp;(<span class="tabular-nums">{snapshot.filesFailed.toLocaleString()}</span> failed){/if}.
        {:else}
          Preparing <span class="tabular-nums">{snapshot.songsTotal.toLocaleString()}</span> songs…
        {/if}
      </p>
      <span
        role="progressbar"
        aria-label="{modeLabel} progress"
        aria-valuemin={0}
        aria-valuemax={100}
        aria-valuenow={Math.round(overallPct)}
        class="bg-muted mt-2 block h-1 w-full overflow-hidden rounded-full"
      >
        <span
          class="bg-primary block h-full w-full origin-left transition-transform duration-300"
          style="transform: scaleX({overallPct / 100})"
        ></span>
      </span>
    </div>
  {:else if snapshot.status === 'completed'}
    {@const prefix = snapshot.mode === 'post-fingerprint' ? 'Reset' : 'Deleted'}
    <CircleCheck class="text-primary mt-0.5 size-5 shrink-0" aria-hidden="true" />
    <div class="min-w-0 flex-1">
      <p class="text-body md:text-sm">{modeLabel} complete</p>
      <p class="text-subheadline text-muted-foreground md:text-xs">
        {prefix}
        {snapshot.songsProcessed.toLocaleString()} songs, removed {snapshot.filesDeleted.toLocaleString()}
        destination files, cleared {snapshot.spotifyMatchesCleared.toLocaleString()} Spotify matches.
        {#if snapshot.filesFailed > 0}
          <span class="text-warning-text mt-1 block">
            {snapshot.filesFailed.toLocaleString()} file{snapshot.filesFailed === 1 ? '' : 's'} could
            not be deleted (see server logs).
          </span>
        {/if}
      </p>
    </div>
  {:else}
    <CircleAlert class="text-destructive-text mt-0.5 size-5 shrink-0" aria-hidden="true" />
    <div class="min-w-0 flex-1">
      <p class="text-body text-destructive-text md:text-sm">{modeLabel} failed</p>
      <p class="text-subheadline text-muted-foreground md:text-xs">
        {snapshot.error ?? 'Unknown error — check server logs.'}
      </p>
    </div>
  {/if}
</div>

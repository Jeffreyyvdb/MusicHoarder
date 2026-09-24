<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import StatusLine from '$lib/components/settings/StatusLine.svelte';
  import {
    auditStoredVideos,
    deleteSongVideo,
    type StoredVideoAudit,
    type StoredVideoAuditRow
  } from '$lib/api-client';
  import { Film, Loader2, Trash2 } from '@lucide/svelte';

  /**
   * Finds music videos that are a single still image — an album cover held for the whole song —
   * among the clips already downloaded. Videos fetched before the pre-download check existed were
   * never measured, so this is how that backlog gets cleaned up.
   *
   * Measuring decodes each file's keyframes with ffmpeg (about a tenth of a second per video), so
   * the scan is bounded by a limit rather than run on the whole library at once.
   *
   * The audit and the removal are admin endpoints. The demo sees the section (it shows what the
   * product does) with the scan held back, rather than a live row that can only fail.
   */
  type Props = { admin: boolean };
  const { admin }: Props = $props();

  let audit = $state<StoredVideoAudit | null>(null);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let removing = $state<number | null>(null);
  let removed = $state<Set<number>>(new Set());
  // Deleting a downloaded clip cannot be undone, so each Remove confirms.
  let confirming = $state<StoredVideoAuditRow | null>(null);

  async function run() {
    loading = true;
    error = null;
    try {
      audit = await auditStoredVideos(50);
      removed = new Set();
    } catch {
      error = 'Could not scan the stored videos.';
    } finally {
      loading = false;
    }
  }

  async function remove(row: StoredVideoAuditRow) {
    removing = row.songId;
    try {
      await deleteSongVideo(row.songId);
      // Mark rather than splice: the row stays visible as struck-through confirmation, and the
      // reclaimed total below stays honest without a re-scan.
      removed = new Set([...removed, row.songId]);
    } catch {
      error = 'Could not remove that video.';
    } finally {
      removing = null;
    }
  }

  function mb(bytes: number): string {
    return `${(bytes / 1024 / 1024).toFixed(0)} MB`;
  }

  function titleOf(row: StoredVideoAuditRow): string {
    return `${row.artist ?? 'Unknown artist'} — ${row.title ?? 'Unknown title'}`;
  }

  const staticRows = $derived(audit?.rows.filter((r) => r.motion === 'Static') ?? []);
  const reclaimed = $derived(
    staticRows.filter((r) => removed.has(r.songId)).reduce((sum, r) => sum + r.fileBytes, 0)
  );
</script>

{#snippet footer()}
  <div class="flex flex-col gap-2">
    {#if error}
      <StatusLine tone="error">{error}</StatusLine>
    {/if}
    {#if audit}
      <p class="text-foreground tabular-nums">
        {audit.measured} checked · {audit.staticCount}
        {audit.staticCount === 1 ? 'still image' : 'still images'} · {mb(audit.staticBytes)} of {mb(
          audit.totalBytes
        )}{#if reclaimed > 0}&nbsp;· {mb(reclaimed)} reclaimed{/if}
      </p>
    {/if}
    {#if audit?.more}
      <p>Showing the first 50 videos — scan again after clearing these to check the rest.</p>
    {/if}
    <p>
      Finds clips that are really just an album cover held for the whole song, and shows the disk
      they take. New downloads are checked before they are fetched; this covers the ones that were
      not.
    </p>
  </div>
{/snippet}

<GroupedList.Section headingLevel={2} header="Stored music videos" {footer}>
  {#if !admin}
    <GroupedList.Row icon={Film} label="Scan stored videos" value="Hidden in the demo" disabled />
  {:else}
    <GroupedList.Row icon={Film} onclick={run} disabled={loading}>
      <span class="text-body text-primary md:text-sm">
        {loading ? 'Checking…' : audit ? 'Scan again' : 'Scan stored videos'}
      </span>
      {#snippet trailing()}
        {#if loading}
          <Loader2 class="text-muted-foreground size-4 animate-spin" aria-hidden="true" />
        {/if}
      {/snippet}
    </GroupedList.Row>
  {/if}

  {#if audit && staticRows.length === 0}
    <GroupedList.Row
      label="No still-image videos found{audit.measured === 0
        ? ' — nothing could be measured'
        : ''}"
      disabled
    />
  {:else}
    {#each staticRows as row (row.songId)}
      {@const gone = removed.has(row.songId)}
      <GroupedList.Row sublabel={gone ? `${mb(row.fileBytes)} · removed` : mb(row.fileBytes)}>
        <span
          class="text-body md:text-sm {gone
            ? 'text-muted-foreground line-through'
            : 'text-foreground'}">{titleOf(row)}</span
        >
        {#snippet trailing()}
          {#if !gone}
            <Button
              variant="ghost"
              size="icon"
              class="text-destructive-text hover:text-destructive-text -my-2 -mr-2 pointer-coarse:size-11"
              disabled={removing === row.songId}
              aria-label="Remove the video for {titleOf(row)}"
              onclick={() => (confirming = row)}
            >
              {#if removing === row.songId}
                <Loader2 class="size-4 animate-spin" />
              {:else}
                <Trash2 class="size-[18px]" />
              {/if}
            </Button>
          {/if}
        {/snippet}
      </GroupedList.Row>
    {/each}
  {/if}
</GroupedList.Section>

<AlertDialog.Root
  open={confirming !== null}
  onOpenChange={(open) => {
    if (!open) confirming = null;
  }}
>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Remove this video?</AlertDialog.Title>
      <AlertDialog.Description>
        The clip for “{confirming ? titleOf(confirming) : ''}” is deleted from disk. The song stays.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action
        variant="destructive"
        onclick={() => {
          const row = confirming;
          confirming = null;
          if (row) void remove(row);
        }}
      >
        Remove video
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import {
    auditStoredVideos,
    deleteSongVideo,
    type StoredVideoAudit,
    type StoredVideoAuditRow
  } from '$lib/api-client';
  import { AlertCircle, Film, Loader2, Trash2 } from '@lucide/svelte';

  /**
   * Finds music videos that are a single still image — an album cover held for the whole song —
   * among the clips already downloaded. Videos fetched before the pre-download check existed were
   * never measured, so this is how that backlog gets cleaned up.
   *
   * Measuring decodes each file's keyframes with ffmpeg (about a tenth of a second per video), so
   * the scan is bounded by a limit rather than run on the whole library at once.
   */
  let audit = $state<StoredVideoAudit | null>(null);
  let loading = $state(false);
  let error = $state<string | null>(null);
  let removing = $state<number | null>(null);
  let removed = $state<Set<number>>(new Set());

  async function run() {
    loading = true;
    error = null;
    try {
      audit = await auditStoredVideos(100);
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

  const staticRows = $derived(audit?.rows.filter((r) => r.motion === 'Static') ?? []);
  const reclaimed = $derived(
    staticRows.filter((r) => removed.has(r.songId)).reduce((sum, r) => sum + r.fileBytes, 0)
  );
</script>

<section class="border-border bg-card rounded-lg border">
  <header class="border-border border-b px-5 py-3.5">
    <h2 class="flex items-center gap-2 text-sm font-semibold">
      <Film class="size-4" /> Stored music videos
    </h2>
    <p class="text-muted-foreground text-xs">
      Finds clips that are really just an album cover held for the whole song, and shows the disk
      they take. New downloads are checked before they are fetched; this covers the ones that were
      not.
    </p>
  </header>

  <div class="flex flex-col gap-3 px-5 py-4">
    <div class="flex items-center gap-2">
      <Button size="sm" variant="outline" disabled={loading} onclick={run}>
        {#if loading}
          <Loader2 class="mr-1.5 size-3.5 animate-spin" /> Checking…
        {:else}
          Scan stored videos
        {/if}
      </Button>
      {#if audit}
        <span class="text-muted-foreground text-xs">
          {audit.measured} checked · {audit.staticCount}
          {audit.staticCount === 1 ? 'still image' : 'still images'} · {mb(audit.staticBytes)} of {mb(
            audit.totalBytes
          )}
        </span>
      {/if}
    </div>

    {#if error}
      <p class="text-destructive flex items-center gap-1.5 text-xs">
        <AlertCircle class="size-3.5" />
        {error}
      </p>
    {/if}

    {#if audit?.more}
      <p class="text-muted-foreground text-xs">
        Showing the first 100 videos — scan again after clearing these to check the rest.
      </p>
    {/if}

    {#if audit && staticRows.length === 0}
      <p class="text-muted-foreground text-xs">
        No still-image videos found{audit.measured === 0 ? ' — nothing could be measured' : ''}.
      </p>
    {:else if staticRows.length > 0}
      <ul class="divide-border border-border divide-y rounded-md border">
        {#each staticRows as row (row.songId)}
          <li class="flex items-center gap-3 px-3 py-2">
            <span class="min-w-0 flex-1">
              <span
                class="block truncate text-xs font-medium"
                class:line-through={removed.has(row.songId)}
                class:text-muted-foreground={removed.has(row.songId)}
              >
                {row.artist ?? 'Unknown artist'} — {row.title ?? 'Unknown title'}
              </span>
              <span class="text-muted-foreground text-[11px]">{mb(row.fileBytes)}</span>
            </span>
            {#if removed.has(row.songId)}
              <span class="text-muted-foreground text-[11px]">removed</span>
            {:else}
              <Button
                size="sm"
                variant="ghost"
                class="text-destructive hover:text-destructive h-7 px-2 text-xs"
                disabled={removing === row.songId}
                onclick={() => remove(row)}
              >
                {#if removing === row.songId}
                  <Loader2 class="size-3.5 animate-spin" />
                {:else}
                  <Trash2 class="mr-1 size-3.5" /> Remove
                {/if}
              </Button>
            {/if}
          </li>
        {/each}
      </ul>
      {#if reclaimed > 0}
        <p class="text-muted-foreground text-xs">{mb(reclaimed)} reclaimed.</p>
      {/if}
    {/if}
  </div>
</section>

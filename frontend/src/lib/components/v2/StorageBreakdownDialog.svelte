<script lang="ts">
  import { untrack } from 'svelte';
  import { AlertTriangle, Loader2 } from '@lucide/svelte';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Button } from '$lib/components/ui/button';
  import type { StorageRoot } from '$lib/api-client';
  import { storageUsage } from '$lib/stores/storage-usage.svelte';
  import { categoryMeta, originMeta, rootLabel } from '$lib/storage-usage-meta';
  import { formatBytesShort, formatFileSize, formatRelativeTime } from '$lib/formatters';

  // A bottom sheet on a phone (grabber, drag to dismiss) and a centred dialog on a desktop — the
  // BottomSheet primitive switches at md. One scroller: the sheet body, never a second one
  // nested inside it. Opened from the sidebar footer, the Manage hub and the account panel; the
  // shell mounts it, so it outlives whichever of those opened it.

  // Re-read on every open, so the sheet never shows a figure older than the sidebar's.
  $effect(() => {
    if (storageUsage.dialogOpen) untrack(() => void storageUsage.reload());
  });

  const snap = $derived(storageUsage.snapshot);
  const computing = $derived(storageUsage.computing);
  const error = $derived(storageUsage.error);

  // The bar is drawn against the volume when one could be probed, so the unfilled remainder reads as
  // "the rest of the disk"; without a volume it is drawn against what we manage.
  const barTotal = $derived(
    snap ? (snap.capacityBytes > 0 ? snap.capacityBytes : snap.managedBytes) : 0
  );
  const categories = $derived((snap?.categories ?? []).filter((c) => c.bytes > 0));
  const libraryBytes = $derived(snap?.categories.find((c) => c.key === 'library')?.bytes ?? 0);
  const origins = $derived(
    (snap?.origins ?? []).filter((o) => o.bytes > 0).sort((a, b) => b.bytes - a.bytes)
  );
  const maxOriginBytes = $derived(Math.max(1, ...origins.map((o) => o.bytes)));
  const untrackedRoots = $derived((snap?.roots ?? []).filter((r) => r.untrackedAudioBytes > 0));
  const unmeasuredRoots = $derived((snap?.roots ?? []).filter((r) => !r.walked));

  function pct(bytes: number, total: number): number {
    return total > 0 ? (bytes / total) * 100 : 0;
  }
  function pctLabel(bytes: number, total: number): string {
    const p = pct(bytes, total);
    if (p >= 10) return `${p.toFixed(0)}%`;
    if (p >= 1) return `${p.toFixed(1)}%`;
    return '<1%';
  }
  function plural(n: number, one: string, many: string): string {
    return `${n.toLocaleString()} ${n === 1 ? one : many}`;
  }
  function fmtDuration(ms: number): string {
    if (ms < 1000) return `${ms} ms`;
    const s = ms / 1000;
    return s < 60 ? `${s.toFixed(s < 10 ? 1 : 0)} s` : `${Math.round(s / 60)} min`;
  }
  function sentence(text: string): string {
    return text.charAt(0).toUpperCase() + text.slice(1);
  }
  function whyNotMeasured(root: StorageRoot): string {
    if (root.skipped === 'offline') return 'the folder is offline';
    if (root.skipped === 'error') return 'the walk failed part-way';
    if (root.skipped?.startsWith('same as')) return `it is ${root.skipped}`;
    if (!root.exists) return 'the folder was not found';
    return root.skipped ?? 'it was skipped';
  }

  function close() {
    storageUsage.dialogOpen = false;
  }
</script>

{#snippet swatch(color: string)}
  <span class="size-2.5 shrink-0 rounded-full {color}" aria-hidden="true"></span>
{/snippet}

<BottomSheet.Root
  open={storageUsage.dialogOpen}
  onOpenChange={(value) => (storageUsage.dialogOpen = value)}
  title="Storage"
  description="What MusicHoarder's files take up on disk, measured folder by folder."
>
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={close}>Done</BottomSheet.Action>
  {/snippet}

  {#if !snap && computing}
    <div
      class="text-muted-foreground flex flex-col items-center justify-center gap-2 px-8 py-12 text-center"
    >
      <Loader2 class="size-5 animate-spin" />
      <span class="text-callout md:text-sm">Measuring your folders…</span>
      <span class="text-footnote max-w-xs md:text-xs">
        The first measurement walks every managed folder. A large library takes a few minutes.
      </span>
    </div>
  {:else if !snap}
    <div class="text-muted-foreground flex flex-col items-center gap-4 px-8 py-12 text-center">
      <span class="text-callout flex items-center gap-2 md:text-sm">
        <AlertTriangle class="text-warning-text size-4 shrink-0" />
        {error ?? 'No measurement yet.'}
      </span>
      <Button
        variant="gray"
        size="pill"
        class="text-primary"
        onclick={() => storageUsage.refresh()}
      >
        Measure now
      </Button>
    </div>
  {:else}
    <div class="flex flex-col gap-7 pt-2">
      <!-- Headline: the managed total against the volume, and the bar the rows below explain. -->
      <GroupedList.Section contentClass="px-4 py-3.5">
        <div class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
          <div class="flex items-baseline gap-1.5">
            <span class="text-title-1 tabular-nums">{formatBytesShort(snap.managedBytes)}</span>
            {#if snap.capacityBytes > 0}
              <span class="text-subheadline text-muted-foreground md:text-sm">
                of {formatBytesShort(snap.capacityBytes)}
              </span>
            {/if}
          </div>
          {#if snap.capacityBytes > 0}
            <span class="text-subheadline text-muted-foreground tabular-nums md:text-sm">
              {formatBytesShort(snap.freeBytes)} free
            </span>
          {/if}
        </div>
        <div
          class="bg-muted mt-3 flex h-3 overflow-hidden rounded-full"
          role="img"
          aria-label="Storage by type"
        >
          {#each categories as c (c.key)}
            {@const meta = categoryMeta(c.key)}
            <div
              class={meta.color}
              style="width: {pct(c.bytes, barTotal)}%"
              title="{meta.label}: {formatFileSize(c.bytes)}"
            ></div>
          {/each}
        </div>
        {#if snap.capacityBytes > 0}
          <p class="text-footnote text-muted-foreground mt-2 md:text-xs">
            The unfilled part is the rest of the volume: free space and files MusicHoarder does not
            manage.
          </p>
        {/if}
      </GroupedList.Section>

      <GroupedList.Section header="By type">
        {#each categories as c (c.key)}
          {@const meta = categoryMeta(c.key)}
          <GroupedList.Row label={meta.label} sublabel={meta.description}>
            {#snippet leading()}{@render swatch(meta.color)}{/snippet}
            {#snippet trailing()}
              <span class="flex flex-col items-end">
                <span class="text-subheadline tabular-nums md:text-sm"
                  >{formatFileSize(c.bytes)}</span
                >
                <span class="text-footnote text-muted-foreground tabular-nums md:text-xs">
                  {plural(c.files, 'file', 'files')} · {pctLabel(c.bytes, snap.managedBytes)}
                </span>
              </span>
            {/snippet}
          </GroupedList.Row>
        {/each}
      </GroupedList.Section>

      <GroupedList.Section
        header="Where the library came from"
        footer={origins.length > 0
          ? `Of the ${formatFileSize(libraryBytes)} in your built library.`
          : undefined}
      >
        {#if origins.length === 0}
          <GroupedList.Row>
            <span class="text-subheadline text-muted-foreground md:text-sm"
              >No built tracks measured yet.</span
            >
          </GroupedList.Row>
        {:else}
          {#each origins as o (o.key)}
            {@const meta = originMeta(o.key)}
            <GroupedList.Row label={meta.label}>
              <span
                class="bg-muted mt-1.5 mb-0.5 block h-1.5 w-full overflow-hidden rounded-full"
                aria-hidden="true"
              >
                <span
                  class="block h-full rounded-full {meta.color}"
                  style="width: {pct(o.bytes, maxOriginBytes)}%"
                ></span>
              </span>
              {#snippet trailing()}
                <span class="flex flex-col items-end">
                  <span class="text-subheadline tabular-nums md:text-sm"
                    >{formatFileSize(o.bytes)}</span
                  >
                  <span class="text-footnote text-muted-foreground tabular-nums md:text-xs">
                    {plural(o.files, 'track', 'tracks')}
                  </span>
                </span>
              {/snippet}
            </GroupedList.Row>
          {/each}
        {/if}
      </GroupedList.Section>

      {#if snap.volumes.length > 0}
        <GroupedList.Section header="Volumes">
          {#each snap.volumes as v (v.samplePath)}
            {@const used = v.totalBytes - v.freeBytes}
            <GroupedList.Row>
              <span class="text-subheadline truncate font-mono md:text-xs" title={v.samplePath}
                >{v.samplePath}</span
              >
              <span
                class="bg-muted mt-1.5 block h-1.5 w-full overflow-hidden rounded-full"
                aria-hidden="true"
              >
                <span
                  class="bg-muted-foreground block h-full rounded-full"
                  style="width: {pct(used, v.totalBytes)}%"
                ></span>
              </span>
              <span class="text-footnote text-muted-foreground mt-1 tabular-nums md:text-xs">
                {formatBytesShort(used)} of {formatBytesShort(v.totalBytes)} · {formatBytesShort(
                  v.freeBytes
                )} free
              </span>
            </GroupedList.Row>
          {/each}
        </GroupedList.Section>
      {/if}

      <!-- Prose, one note per cell. -->
      <GroupedList.Section
        header="Notes"
        contentClass="text-subheadline text-muted-foreground md:text-sm"
      >
        {#if snap.duplicates.tracks > 0}
          <GroupedList.Row>
            <p>
              <span class="text-foreground tabular-nums"
                >{formatFileSize(snap.duplicates.bytes)}</span
              >
              across {plural(snap.duplicates.tracks, 'track', 'tracks')} flagged as duplicates, counting
              every copy on disk.
            </p>
          </GroupedList.Row>
        {/if}
        {#if snap.reclaimable.stagedSourceBytes > 0}
          <GroupedList.Row>
            <p>
              <span class="text-foreground tabular-nums"
                >{formatFileSize(snap.reclaimable.stagedSourceBytes)}</span
              >
              of staged downloads can be released now that their library copies are verified —
              <a
                href="/settings?tab=sources"
                onclick={close}
                class="text-primary underline-offset-2 hover:underline">release them in Settings</a
              >.
            </p>
          </GroupedList.Row>
        {/if}
        {#each untrackedRoots as r (r.key)}
          <GroupedList.Row>
            <p>
              <span class="text-foreground tabular-nums"
                >{formatFileSize(r.untrackedAudioBytes)}</span
              >
              of audio in {rootLabel(r.key)} ({plural(r.untrackedAudioFiles, 'file', 'files')}) is
              not tracked by any library track.
            </p>
          </GroupedList.Row>
        {/each}
        {#each unmeasuredRoots as r (r.key)}
          <GroupedList.Row>
            <p>
              {sentence(rootLabel(r.key))}
              <span class="font-mono text-[0.9em] break-all">{r.path}</span> was not measured: {whyNotMeasured(
                r
              )}.
            </p>
          </GroupedList.Row>
        {/each}
        <GroupedList.Row>
          <p>
            Lyrics live in the database and are embedded in the audio tags — there are no lyric
            files{#if snap.lyrics.tracksWithLyrics > 0}
              (about {formatFileSize(snap.lyrics.approxTextBytes)} of text over {plural(
                snap.lyrics.tracksWithLyrics,
                'track',
                'tracks'
              )}){/if}.
          </p>
        </GroupedList.Row>
        <GroupedList.Row>
          <p>The database itself is not included.</p>
        </GroupedList.Row>
      </GroupedList.Section>

      <GroupedList.Section>
        {#snippet footer()}
          Measured {formatRelativeTime(snap.computedAtUtc)} · took {fmtDuration(
            snap.durationMs
          )}{#if error}
            · <span class="text-warning-text">last refresh failed</span>{/if}
        {/snippet}
        <GroupedList.Row onclick={() => storageUsage.refresh()} disabled={computing}>
          <span class="text-body text-primary flex items-center gap-2 md:text-sm">
            {#if computing}
              <Loader2 class="size-4 animate-spin" /> Measuring…
            {:else}
              Measure again
            {/if}
          </span>
        </GroupedList.Row>
      </GroupedList.Section>
    </div>
  {/if}
</BottomSheet.Root>

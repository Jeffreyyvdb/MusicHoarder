<script lang="ts">
  import { untrack } from 'svelte';
  import { AlertTriangle, Loader2, RefreshCw } from '@lucide/svelte';
  import * as Dialog from '$lib/components/ui/dialog';
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import type { StorageRoot } from '$lib/api-client';
  import { storageUsage } from '$lib/stores/storage-usage.svelte';
  import { categoryMeta, originMeta, rootLabel } from '$lib/storage-usage-meta';
  import { formatBytesShort, formatFileSize, formatRelativeTime } from '$lib/formatters';

  // Re-read on every open, so the dialog never shows a figure older than the sidebar's.
  $effect(() => {
    if (storageUsage.dialogOpen) untrack(() => void storageUsage.reload());
  });

  const snap = $derived(storageUsage.snapshot);
  const computing = $derived(storageUsage.computing);
  const error = $derived(storageUsage.error);

  // The bar is drawn against the volume when one could be probed, so the unfilled remainder reads as
  // "the rest of the disk"; without a volume it is drawn against what we manage.
  const barTotal = $derived(snap ? (snap.capacityBytes > 0 ? snap.capacityBytes : snap.managedBytes) : 0);
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
</script>

<Dialog.Root open={storageUsage.dialogOpen} onOpenChange={(value) => (storageUsage.dialogOpen = value)}>
  <Dialog.Content class="sm:max-w-lg">
    <Dialog.Header>
      <Dialog.Title>Storage</Dialog.Title>
      <Dialog.Description>
        What MusicHoarder's files take up on disk, measured folder by folder.
      </Dialog.Description>
    </Dialog.Header>

    {#if !snap && computing}
      <div class="text-muted-foreground flex flex-col items-center justify-center gap-2 py-10 text-center text-[12.5px]">
        <Loader2 class="size-4 animate-spin" />
        <span>Measuring your folders…</span>
        <span class="text-muted-foreground/70 max-w-xs text-[11px]">
          The first measurement walks every managed folder. A large library takes a few minutes.
        </span>
      </div>
    {:else if !snap}
      <div
        class="border-border bg-card text-muted-foreground flex flex-col items-center gap-3 rounded-lg border border-dashed px-3.5 py-8 text-[12.5px]"
      >
        <div class="flex items-center gap-2">
          <AlertTriangle class="size-4 text-amber-500" />
          {error ?? 'No measurement yet.'}
        </div>
        <Button size="sm" variant="outline" onclick={() => storageUsage.refresh()}>Measure now</Button>
      </div>
    {:else}
      <ScrollArea class="-mx-1 max-h-[65vh] min-h-0 min-w-0 px-1">
        <div class="flex min-w-0 flex-col gap-5 pr-2">
          <section>
            <div class="flex flex-wrap items-baseline justify-between gap-x-3 gap-y-1">
              <div class="flex items-baseline gap-1.5">
                <span class="text-[26px] leading-none font-semibold tracking-tight tabular-nums">
                  {formatBytesShort(snap.managedBytes)}
                </span>
                {#if snap.capacityBytes > 0}
                  <span class="text-muted-foreground text-[12.5px]">of {formatBytesShort(snap.capacityBytes)}</span>
                {/if}
              </div>
              {#if snap.capacityBytes > 0}
                <span class="text-muted-foreground text-[12px] tabular-nums">{formatBytesShort(snap.freeBytes)} free</span>
              {/if}
            </div>
            <div class="bg-muted mt-3 flex h-3 overflow-hidden rounded-full" role="img" aria-label="Storage by type">
              {#each categories as c (c.key)}
                {@const meta = categoryMeta(c.key)}
                <div class={meta.color} style="width: {pct(c.bytes, barTotal)}%" title="{meta.label}: {formatFileSize(c.bytes)}"></div>
              {/each}
            </div>
            {#if snap.capacityBytes > 0}
              <p class="text-muted-foreground/70 mt-1.5 text-[11px]">
                The unfilled part is the rest of the volume: free space and files MusicHoarder does not manage.
              </p>
            {/if}
          </section>

          <section class="flex flex-col gap-2">
            <h3 class="text-muted-foreground text-[11px] font-medium tracking-wide uppercase">By type</h3>
            {#each categories as c (c.key)}
              {@const meta = categoryMeta(c.key)}
              <div class="flex items-center gap-2.5 text-[12.5px]">
                <span class="size-2 shrink-0 rounded-full {meta.color}"></span>
                <div class="min-w-0 flex-1">
                  <div class="truncate">{meta.label}</div>
                  <div class="text-muted-foreground truncate text-[11px]">{meta.description}</div>
                </div>
                <div class="shrink-0 text-right">
                  <div class="tabular-nums">{formatFileSize(c.bytes)}</div>
                  <div class="text-muted-foreground text-[11px] tabular-nums">
                    {plural(c.files, 'file', 'files')} · {pctLabel(c.bytes, snap.managedBytes)}
                  </div>
                </div>
              </div>
            {/each}
          </section>

          <section class="flex flex-col gap-2">
            <h3 class="text-muted-foreground text-[11px] font-medium tracking-wide uppercase">
              Where the library came from
            </h3>
            {#if origins.length === 0}
              <p class="text-muted-foreground text-[12px]">No built tracks measured yet.</p>
            {:else}
              {#each origins as o (o.key)}
                {@const meta = originMeta(o.key)}
                <div class="flex items-center gap-2.5 text-[12.5px]">
                  <span class="min-w-0 flex-1 truncate" title={meta.label}>{meta.label}</span>
                  <div class="bg-muted hidden h-1.5 w-32 shrink-0 overflow-hidden rounded-full sm:block">
                    <div class="h-full rounded-full {meta.color}" style="width: {pct(o.bytes, maxOriginBytes)}%"></div>
                  </div>
                  <span class="w-[4.5rem] shrink-0 text-right tabular-nums">{formatFileSize(o.bytes)}</span>
                  <span class="text-muted-foreground w-14 shrink-0 text-right text-[11px] tabular-nums">
                    {plural(o.files, 'track', 'tracks')}
                  </span>
                </div>
              {/each}
              <p class="text-muted-foreground/70 text-[11px]">
                Of the {formatFileSize(libraryBytes)} in your built library.
              </p>
            {/if}
          </section>

          {#if snap.volumes.length > 0}
            <section class="flex flex-col gap-2">
              <h3 class="text-muted-foreground text-[11px] font-medium tracking-wide uppercase">Volumes</h3>
              {#each snap.volumes as v (v.samplePath)}
                {@const used = v.totalBytes - v.freeBytes}
                <div class="text-[12.5px]">
                  <div class="flex flex-col gap-0.5 sm:flex-row sm:items-center sm:justify-between sm:gap-3">
                    <span class="min-w-0 truncate font-mono text-[11.5px]" title={v.samplePath}>{v.samplePath}</span>
                    <span class="shrink-0 tabular-nums">
                      {formatBytesShort(used)}
                      <span class="text-muted-foreground">of {formatBytesShort(v.totalBytes)} · {formatBytesShort(v.freeBytes)} free</span>
                    </span>
                  </div>
                  <div class="bg-muted mt-1 h-1.5 overflow-hidden rounded-full">
                    <div class="bg-foreground/40 h-full rounded-full" style="width: {pct(used, v.totalBytes)}%"></div>
                  </div>
                </div>
              {/each}
            </section>
          {/if}

          <section class="flex flex-col gap-1.5">
            <h3 class="text-muted-foreground text-[11px] font-medium tracking-wide uppercase">Notes</h3>
            <ul class="text-muted-foreground flex list-disc flex-col gap-1.5 pl-4 text-[12px]">
              {#if snap.duplicates.tracks > 0}
                <li>
                  <span class="text-foreground tabular-nums">{formatFileSize(snap.duplicates.bytes)}</span>
                  across {plural(snap.duplicates.tracks, 'track', 'tracks')} flagged as duplicates, counting every copy on disk.
                </li>
              {/if}
              {#if snap.reclaimable.stagedSourceBytes > 0}
                <li>
                  <span class="text-foreground tabular-nums">{formatFileSize(snap.reclaimable.stagedSourceBytes)}</span>
                  of staged downloads can be released now that their library copies are verified —
                  <a href="/settings?tab=sources" class="underline underline-offset-2">release them in Settings</a>.
                </li>
              {/if}
              {#each untrackedRoots as r (r.key)}
                <li>
                  <span class="text-foreground tabular-nums">{formatFileSize(r.untrackedAudioBytes)}</span>
                  of audio in {rootLabel(r.key)} ({plural(r.untrackedAudioFiles, 'file', 'files')}) is not tracked by any
                  library track.
                </li>
              {/each}
              {#each unmeasuredRoots as r (r.key)}
                <li>
                  {sentence(rootLabel(r.key))}
                  <span class="font-mono text-[11px]">{r.path}</span> was not measured: {whyNotMeasured(r)}.
                </li>
              {/each}
              <li>
                Lyrics live in the database and are embedded in the audio tags — there are no lyric files{#if snap.lyrics.tracksWithLyrics > 0}
                  (about {formatFileSize(snap.lyrics.approxTextBytes)} of text over {plural(snap.lyrics.tracksWithLyrics, 'track', 'tracks')}){/if}.
              </li>
              <li>The database itself is not included.</li>
            </ul>
          </section>
        </div>
      </ScrollArea>

      <div class="text-muted-foreground flex flex-wrap items-center justify-between gap-3 border-t pt-3 text-[11.5px]">
        <span class="min-w-0">
          Measured {formatRelativeTime(snap.computedAtUtc)} · took {fmtDuration(snap.durationMs)}
          {#if error}
            · <span class="text-amber-500">last refresh failed</span>
          {/if}
        </span>
        <Button size="sm" variant="outline" disabled={computing} onclick={() => storageUsage.refresh()}>
          {#if computing}
            <Loader2 class="size-3.5 animate-spin" /> Measuring…
          {:else}
            <RefreshCw class="size-3.5" /> Refresh
          {/if}
        </Button>
      </div>
    {/if}
  </Dialog.Content>
</Dialog.Root>

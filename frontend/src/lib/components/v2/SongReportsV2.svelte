<script lang="ts">
  import { ExternalLink, Flag, RefreshCw, TriangleAlert } from '@lucide/svelte';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { reportReasonLabel } from '#lib/components/v2/ReportDownloadSheet.svelte';
  import * as GroupedList from '#lib/components/ui/grouped-list/index.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { EmptyState } from '#lib/components/ui/empty-state/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import { listSongReports, type SongReport } from '#lib/api-client.ts';
  import { formatRelativeTime } from '#lib/formatters.ts';
  import {
    durationMismatch,
    reportedTitle,
    reportsByProvider,
    requestedLine,
    sourceLink
  } from '#lib/song-reports.ts';
  import { cn } from '#lib/utils.ts';

  // Manage → Reported downloads: every track reported as a wrong download (from a track's ⋯ menu or
  // Now Playing's), newest first, with what was asked for, what arrived and where it came from — the
  // evidence for making downloads pick better. The per-provider tally says which source to look at.

  let rows = $state<SongReport[]>([]);
  let loading = $state(true);
  let loaded = $state(false);
  let error = $state<string | null>(null);

  async function load() {
    loading = true;
    try {
      rows = await listSongReports();
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load reports';
    } finally {
      loading = false;
      loaded = true;
    }
  }

  $effect(() => {
    void load();
  });

  const providers = $derived(reportsByProvider(rows));
  const headerMeta = $derived(
    loaded && !error ? `${rows.length} ${rows.length === 1 ? 'report' : 'reports'}` : undefined
  );

  function metaLine(r: SongReport): string {
    return [
      reportReasonLabel(r.reason),
      r.downloadProvider,
      durationMismatch(r),
      r.removedAtUtc ? null : 'kept',
      formatRelativeTime(r.createdAtUtc)
    ]
      .filter(Boolean)
      .join(' · ');
  }
</script>

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1">
    <PageToolbarV2 title="Reported downloads" meta={headerMeta} grouped>
      {#snippet actions()}
        <Button variant="gray" class="rounded-full" onclick={load} disabled={loading}>
          <RefreshCw class={cn(loading && 'animate-spin')} aria-hidden="true" />
          <span class="max-md:sr-only">Refresh</span>
        </Button>
      {/snippet}
    </PageToolbarV2>

    <div class="mx-auto flex w-full max-w-3xl flex-col gap-7 pt-2 pb-8 md:gap-6 md:px-7 md:pt-6">
      {#if error}
        <GroupedList.Section>
          <GroupedList.Row
            icon={TriangleAlert}
            iconClass="bg-destructive/12 text-destructive-text"
            label="Couldn't load reports"
            sublabel={error}
          >
            {#snippet trailing()}
              <Button
                variant="ghost"
                class="text-primary hover:text-primary h-11 md:h-8"
                onclick={load}
              >
                Retry
              </Button>
            {/snippet}
          </GroupedList.Row>
        </GroupedList.Section>
      {:else if !loaded}
        <GroupedList.Section>
          {#each Array(4) as _, i (i)}
            <div class="space-y-1.5 px-4 py-2.5">
              <Skeleton class="h-4 w-1/2" />
              <Skeleton class="h-3 w-2/3" />
            </div>
          {/each}
        </GroupedList.Section>
      {:else if rows.length === 0}
        <EmptyState
          icon={Flag}
          title="No reported downloads"
          hint="When a download is the wrong song, pick Report wrong download… in its ⋯ menu. It is removed from your library and listed here."
        />
      {:else}
        {#if providers.length > 1}
          <GroupedList.Section header="By source">
            {#each providers as p (p.provider)}
              <GroupedList.Row label={p.provider} value={p.count} />
            {/each}
          </GroupedList.Section>
        {/if}

        <GroupedList.Section
          header="Reports"
          footer="Each report keeps what was asked for, what arrived and where it came from, as they were when you reported it."
        >
          {#each rows as r (r.id)}
            {@const asked = requestedLine(r)}
            {@const link = sourceLink(r)}
            <GroupedList.Row label={reportedTitle(r)} class="py-2.5">
              {#if asked}
                <span class="text-subheadline text-muted-foreground md:text-xs">
                  Asked for {asked}
                </span>
              {/if}
              <span class="text-subheadline text-muted-foreground md:text-xs">{metaLine(r)}</span>
              {#if r.note}
                <span class="text-subheadline text-foreground mt-0.5 md:text-xs">“{r.note}”</span>
              {/if}
              {#snippet trailing()}
                {#if link}
                  <Button
                    variant="ghost"
                    size="icon"
                    href={link}
                    target="_blank"
                    rel="noopener noreferrer"
                    class="text-primary hover:text-primary size-11 rounded-full md:size-8"
                    aria-label="Open the source of {reportedTitle(r)}"
                  >
                    <ExternalLink class="size-[18px]" aria-hidden="true" />
                  </Button>
                {/if}
              {/snippet}
            </GroupedList.Row>
          {/each}
        </GroupedList.Section>
      {/if}
    </div>
  </ScrollArea>
</div>

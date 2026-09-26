<script lang="ts">
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { onMount } from 'svelte';
  import { Disc3, History, Loader2, RefreshCw, Sparkles, TriangleAlert } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Button } from '$lib/components/ui/button';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import {
    fetchAlbumQualityOverview,
    fetchAlbumQualityProgress,
    gradeAllAlbums,
    gradeOutdatedAlbums,
    type AlbumQualityOverview,
    type AlbumQualityRow
  } from '$lib/api-client';
  import { verdictBadge, scoreColor, verdictGlyph } from '$lib/quality-ui';
  import { cn } from '$lib/utils';

  // Album matches: how well each owned album matches its canonical release. The bar carries one
  // prominent action (Grade all albums, which asks first: it spends AI credits) and a More menu
  // (Re-grade outdated, Refresh); a run shows its progress in the subtitle and a determinate bar
  // under the title. The page is one scroller: the rollup, then the worst offenders as rows that
  // open the album.

  let overview = $state<AlbumQualityOverview | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let grading = $state(false);
  /** Only while a run is queuing or draining; the subtitle falls back to the rollup after. */
  let progressText = $state<string | null>(null);
  /** Processed / total of the current run, when the API reports them — drives the bar. */
  let progress = $state<{ processed: number; total: number } | null>(null);
  /** Starting a run failed (grading off or unconfigured): a notice row until the next attempt. */
  let gradeUnavailable = $state(false);
  let confirmGradeAll = $state(false);

  // The run's poll outlives the click that started it, so it belongs to the page: cleared on
  // teardown, or leaving mid-run would keep polling and reload into a destroyed component.
  let poll: ReturnType<typeof setInterval> | null = null;
  let destroyed = false;
  function stopPolling() {
    if (poll) clearInterval(poll);
    poll = null;
  }

  async function load() {
    loading = true;
    try {
      overview = await fetchAlbumQualityOverview();
      error = null;
    } catch (e) {
      overview = null;
      error = e instanceof Error ? e.message : 'Failed to load album quality overview';
    } finally {
      loading = false;
    }
  }

  onMount(() => {
    void load();
    return () => {
      destroyed = true;
      stopPolling();
    };
  });

  /** Deep-link to an album in the library (matches AlbumSummary.key). */
  function albumHref(row: AlbumQualityRow): string {
    const key = `${(row.artist ?? '').toLowerCase()}::${(row.album ?? '').toLowerCase()}`;
    return `/library?album=${encodeURIComponent(key)}`;
  }

  function endRun() {
    stopPolling();
    grading = false;
    progressText = null;
    progress = null;
  }

  async function regrade(outdatedOnly = false) {
    if (grading) return;
    grading = true;
    gradeUnavailable = false;
    progress = null;
    progressText = 'Queuing…';
    try {
      const { enqueued } = outdatedOnly ? await gradeOutdatedAlbums() : await gradeAllAlbums();
      if (destroyed) return;
      if (enqueued === 0) {
        // A passing confirmation, not a state of the page: it must not take over the subtitle.
        endRun();
        toast.success('Everything is up to date', { description: 'No album needed grading.' });
        return;
      }
      // Poll until the run drains, then reload the overview.
      stopPolling();
      poll = setInterval(async () => {
        try {
          const p = await fetchAlbumQualityProgress();
          if (destroyed) return;
          if (p.active) {
            const total = p.total ?? enqueued;
            progress = { processed: p.processed ?? 0, total };
            progressText = `Grading ${p.processed ?? 0} of ${total}…`;
          } else {
            endRun();
            await load();
          }
        } catch {
          endRun();
        }
      }, 1500);
    } catch {
      endRun();
      gradeUnavailable = true;
    }
  }

  const lib = $derived(overview?.library);

  const headerMeta = $derived(
    progressText ??
      (overview && lib
        ? `${lib.graded.toLocaleString()} graded · ${Math.round(overview.coverage * 100)}% coverage${lib.averageScore != null ? ` · avg ${lib.averageScore}` : ''}`
        : undefined)
  );

  const rollup = $derived(
    overview && lib
      ? [
          { label: 'Graded', value: lib.graded.toLocaleString() },
          { label: 'Coverage', value: `${Math.round(overview.coverage * 100)}%` },
          { label: 'Avg score', value: lib.averageScore != null ? String(lib.averageScore) : '—' },
          { label: 'Wrong', value: lib.verdicts.wrong.toLocaleString(), tone: lib.verdicts.wrong > 0 ? 'text-destructive-text' : undefined },
          { label: 'Questionable', value: lib.verdicts.questionable.toLocaleString(), tone: lib.verdicts.questionable > 0 ? 'text-warning-text' : undefined },
          { label: 'Excellent', value: lib.verdicts.excellent.toLocaleString() }
        ]
      : []
  );
</script>

<!-- One scroller; the nav bar is its first child. -->
<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1" viewportClass="overscroll-contain">
    <PageToolbarV2 title="Album matches" meta={headerMeta} grouped>
      {#snippet actions()}
        <!-- Sparkles is the grading glyph (AI quality's Re-grade uses it too); the circular arrow
             is kept for Refresh alone, in More. -->
        <Button onclick={() => (confirmGradeAll = true)} disabled={grading} class="rounded-full">
          {#if grading}
            <Loader2 class="animate-spin" aria-hidden="true" />
          {:else}
            <Sparkles aria-hidden="true" />
          {/if}
          <!-- The phone bar shows the glyph alone; the words stay its accessible name. -->
          <span class="max-md:sr-only">Grade all albums</span>
        </Button>
      {/snippet}
      {#snippet more()}
        {#if overview && overview.outdatedCount > 0}
          <DropdownMenu.Item onSelect={() => regrade(true)} disabled={grading}>
            <History />
            Re-grade {overview.outdatedCount.toLocaleString()} outdated
          </DropdownMenu.Item>
        {/if}
        <DropdownMenu.Item onSelect={load}>
          <RefreshCw />
          Refresh
        </DropdownMenu.Item>
      {/snippet}
    </PageToolbarV2>

    <div class="mx-auto flex w-full max-w-4xl flex-col gap-7 pt-2 pb-8 md:gap-6 md:px-7 md:pt-6">
      {#if progress}
        <!-- A determinate bar while a run drains (progress-indicators: prefer determinate). -->
        <div class="px-8 md:px-4">
          <span
            role="progressbar"
            aria-label="Album grading progress"
            aria-valuemin={0}
            aria-valuemax={progress.total}
            aria-valuenow={progress.processed}
            class="bg-muted block h-1 w-full overflow-hidden rounded-full"
          >
            <span
              class="bg-primary block h-full w-full origin-left transition-transform duration-300 ease-out"
              style="transform: scaleX({progress.total > 0 ? Math.min(1, progress.processed / progress.total) : 0})"
            ></span>
          </span>
        </div>
      {/if}

      {#if gradeUnavailable}
        <GroupedList.Section>
          <GroupedList.Row
            icon={TriangleAlert}
            iconClass="bg-warning/15 text-warning-text"
            label="Couldn't start grading"
            sublabel="AI grading is not configured on the server, or it is turned off in Settings."
          />
        </GroupedList.Section>
      {/if}

      {#if error}
        <GroupedList.Section>
          <GroupedList.Row
            icon={TriangleAlert}
            iconClass="bg-destructive/12 text-destructive-text"
            label="Couldn't load album grades"
            sublabel={error}
          />
        </GroupedList.Section>
      {:else if loading}
        <GroupedList.Section>
          {#each Array(5) as _, i (i)}
            <div class="flex items-center justify-between px-4 py-3">
              <Skeleton class="h-4 w-24" />
              <Skeleton class="h-4 w-10" />
            </div>
          {/each}
        </GroupedList.Section>
      {:else if !overview || lib === undefined}
        <p class="text-body text-muted-foreground px-8 py-14 text-center md:text-sm">No album grades yet.</p>
      {:else}
        <!-- Rollup: value rows on a phone, a strip of six figures on a desktop. -->
        <GroupedList.Section headingLevel={2} header="Summary">
          <div class="md:hidden">
            {#each rollup as r (r.label)}
              <GroupedList.Row label={r.label}>
                {#snippet trailing()}
                  <span class={cn('text-body tabular-nums', r.tone ?? 'text-muted-foreground')}>{r.value}</span>
                {/snippet}
              </GroupedList.Row>
            {/each}
          </div>
          <div class="divide-separator hidden grid-cols-6 divide-x md:grid">
            {#each rollup as r (r.label)}
              <div class="min-w-0 px-4 py-3.5">
                <div class={cn('text-lg font-semibold tabular-nums', r.tone)}>{r.value}</div>
                <div class="text-muted-foreground truncate text-[12px]">{r.label}</div>
              </div>
            {/each}
          </div>
        </GroupedList.Section>

        <!-- Worst offenders: each row opens the album. -->
        <GroupedList.Section headingLevel={2}
          header="Worst matches"
          footer={overview.worstOffenders.length > 0
            ? 'Owned tracks against the canonical release, worst grade first.'
            : undefined}
        >
          {#if overview.worstOffenders.length === 0}
            <div class="flex flex-col items-center gap-2 px-6 py-10 text-center">
              <Disc3 class="text-muted-foreground size-8" aria-hidden="true" />
              <p class="text-body text-muted-foreground md:text-sm">
                No graded albums yet — run “Grade all albums”.
              </p>
            </div>
          {:else}
            {#each overview.worstOffenders as row (row.canonicalAlbumId)}
              <GroupedList.Row href={albumHref(row)} chevron>
                {#snippet leading()}
                  <span
                    class={cn(
                      'text-caption-1 inline-flex h-6 w-14 shrink-0 items-center justify-center gap-1 rounded-full border font-semibold tabular-nums',
                      verdictBadge(row.verdict)
                    )}
                  >
                    <span aria-hidden="true">{verdictGlyph(row.verdict)}</span>
                    <span class={scoreColor(row.score)}>{row.score}</span>
                    <span class="sr-only">{row.verdict}</span>
                  </span>
                {/snippet}
                <span class="flex min-w-0 items-center gap-1.5">
                  <span class="text-body truncate md:text-sm">{row.album ?? '—'}</span>
                  {#if row.isOutdated}
                    <span
                      class="text-caption-1 bg-warning/6 text-warning-text dark:bg-warning/15 inline-flex shrink-0 items-center gap-1 rounded-full px-1.5 py-px"
                    >
                      <History class="size-3" aria-hidden="true" /> Outdated
                    </span>
                  {/if}
                </span>
                <span class="text-subheadline text-muted-foreground line-clamp-2 md:text-xs">
                  {row.artist ?? '—'} · {row.ownedTrackCount} of {row.canonicalTrackCount} owned
                  {#if row.summary}— {row.summary}{/if}
                </span>
              </GroupedList.Row>
            {/each}
          {/if}
        </GroupedList.Section>
      {/if}
    </div>
  </ScrollArea>
</div>

<AlertDialog.Root bind:open={confirmGradeAll}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Grade all albums?</AlertDialog.Title>
      <AlertDialog.Description>
        Queues every linked album for the AI grader, to check it against its canonical release.
        Albums unchanged since their last grade are skipped; the rest use your OpenRouter credits.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action
        onclick={() => {
          confirmGradeAll = false;
          void regrade(false);
        }}>Grade all</AlertDialog.Action
      >
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<script lang="ts">
  import { onMount } from 'svelte';
  import {
    ChevronRight,
    Loader2,
    Play,
    ScanSearch,
    Square,
    TriangleAlert,
    VolumeX
  } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { EmptyState } from '#lib/components/ui/empty-state/index.ts';
  import * as DropdownMenu from '#lib/components/ui/dropdown-menu/index.ts';
  import * as GroupedList from '#lib/components/ui/grouped-list/index.ts';
  import { LinearProgress } from '#lib/components/ui/progress/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import {
    ApiError,
    fetchCleanEditProgress,
    fetchCleanEdits,
    isPermissionError,
    startCleanEditCheck,
    stopCleanEditCheck,
    toPlayerSong,
    type CleanEditList,
    type CleanEditProgress
  } from '#lib/api-client.ts';
  import {
    evidenceLine,
    notCheckedCount,
    progressLine,
    verdictLabel,
    VERDICT_ROWS
  } from '#lib/clean-edits.ts';
  import { poll } from '#lib/poll.ts';
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { artistOf } from '#lib/track-list-view.svelte.ts';

  // Clean edits: built songs that are a label's clean edit, or have muted words, although the
  // explicit original exists. The page is one scroller. A run compares each file with the free
  // previews of both editions, about three seconds a song, and survives leaving the page: the run
  // lives on the server, so the page only follows it.

  const PAGE = 200;

  let list = $state<CleanEditList | null>(null);
  let progress = $state<CleanEditProgress | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let loadingMore = $state(false);

  let stopRunPoll: (() => void) | null = null;
  let destroyed = false;
  function stopPolling() {
    stopRunPoll?.();
    stopRunPoll = null;
  }

  const running = $derived(progress?.running ?? false);

  async function load(count = PAGE) {
    try {
      list = await fetchCleanEdits(count, 0);
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load the clean edit check';
    } finally {
      loading = false;
    }
  }

  async function showMore() {
    if (!list || loadingMore) return;
    loadingMore = true;
    try {
      const next = await fetchCleanEdits(PAGE, list.items.length);
      if (destroyed || !list) return;
      list = { ...next, items: [...list.items, ...next.items] };
    } catch (e) {
      toast.error('Could not load more', {
        description: e instanceof Error ? e.message : undefined
      });
    } finally {
      loadingMore = false;
    }
  }

  function follow() {
    if (stopRunPoll) return;
    stopRunPoll = poll(async () => {
      try {
        const latest = await fetchCleanEditProgress();
        if (destroyed) return;
        progress = latest;
        await load(Math.max(PAGE, list?.items.length ?? 0));
        if (!latest.running) stopPolling();
      } catch {
        stopPolling();
      }
    }, 3000);
  }

  onMount(() => {
    songsStore.revalidate();
    void (async () => {
      try {
        progress = await fetchCleanEditProgress();
      } catch (e) {
        error = e instanceof Error ? e.message : 'Failed to load the clean edit check';
      }
      await load();
      if (!destroyed && progress?.running) follow();
    })();
    return () => {
      destroyed = true;
      stopPolling();
    };
  });

  async function start() {
    try {
      progress = await startCleanEditCheck();
    } catch (err) {
      // Already running (another tab, or this one before a reload): follow that run.
      if (err instanceof ApiError && err.status === 409) {
        progress = await fetchCleanEditProgress();
      } else {
        if (isPermissionError(err)) toast.error(err instanceof Error ? err.message : 'Not allowed');
        else
          toast.error('Could not start the check', {
            description: err instanceof Error ? err.message : undefined
          });
        return;
      }
    }
    follow();
  }

  async function stop() {
    try {
      progress = await stopCleanEditCheck();
    } catch (err) {
      toast.error('Could not stop the check', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }

  const playable = $derived(
    (list?.items ?? []).flatMap((item) => {
      const song = songsStore.songsById.get(item.songId);
      return song ? [song] : [];
    })
  );

  function play(songId: number) {
    const index = playable.findIndex((song) => song.id === songId);
    if (index < 0) return;
    void playerStore.startQueue(
      playable.map((song) => toPlayerSong(song, artistOf(song))),
      index,
      { source: { label: 'Clean edits', href: '/clean-edits' } }
    );
  }

  const headerMeta = $derived.by(() => {
    if (progress) {
      const line = progressLine(progress);
      if (line) return line;
    }
    if (!list) return undefined;
    return `${list.foundTotal.toLocaleString()} found · ${list.eligible.toLocaleString()} built songs`;
  });

  const notChecked = $derived(list ? notCheckedCount(list.counts, list.eligible) : 0);
  const checkedAny = $derived(list ? list.eligible - notChecked > 0 : false);
</script>

<!-- One scroller; the nav bar is its first child. -->
<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1" viewportClass="overscroll-contain">
    <PageToolbarV2 title="Clean edits" meta={headerMeta} grouped>
      {#snippet actions()}
        {#if running}
          <Button variant="gray" class="rounded-full" onclick={stop} disabled={progress?.stopping}>
            <Square aria-hidden="true" />
            <span class="max-md:sr-only">Stop</span>
          </Button>
        {:else}
          <Button class="rounded-full" onclick={start}>
            <ScanSearch aria-hidden="true" />
            <span class="max-md:sr-only">Check library</span>
          </Button>
        {/if}
      {/snippet}
      {#snippet more()}
        <DropdownMenu.Item onSelect={() => load(Math.max(PAGE, list?.items.length ?? 0))}>
          Refresh
        </DropdownMenu.Item>
      {/snippet}
    </PageToolbarV2>

    <div class="mx-auto flex w-full max-w-4xl flex-col gap-7 pt-2 pb-8 md:gap-6 md:px-7 md:pt-6">
      <p class="text-subheadline text-muted-foreground px-8 md:px-4 md:text-sm">
        Finds songs that are a label’s clean edit, or have words muted, although the explicit
        original exists. Each built file is compared with the free previews of both editions from
        iTunes and Deezer, about three seconds a song. Nothing is changed or replaced.
      </p>

      {#if running && progress}
        <div class="px-8 md:px-4">
          <LinearProgress
            label="Clean edit check progress"
            value={progress.processed}
            max={Math.max(progress.total, 1)}
          />
        </div>
      {/if}

      {#if error}
        <GroupedList.Section>
          <GroupedList.Row
            icon={TriangleAlert}
            iconClass="bg-destructive/12 text-destructive-text"
            label="Couldn't load the clean edit check"
            sublabel={error}
          />
        </GroupedList.Section>
      {:else if loading}
        <GroupedList.Section>
          {#each Array(5) as _, i (i)}
            <div class="flex items-center justify-between px-4 py-3">
              <Skeleton class="h-4 w-32" />
              <Skeleton class="h-4 w-10" />
            </div>
          {/each}
        </GroupedList.Section>
      {:else if list}
        <GroupedList.Section
          headingLevel={2}
          header="Found"
          footer={list.foundTotal > 0
            ? 'Songs whose file is not the explicit original, A to Z by artist.'
            : undefined}
        >
          {#if list.items.length === 0}
            <EmptyState
              icon={VolumeX}
              title={checkedAny ? 'No clean edits found' : 'Nothing checked yet'}
              hint={checkedAny
                ? 'Every song checked so far follows its explicit original, or could not be told apart.'
                : 'Check library compares each built song with its explicit and clean previews.'}
              action={checkedAny || running
                ? undefined
                : { label: 'Check library', onclick: start }}
            />
          {:else}
            {#each list.items as item (item.songId)}
              {@const available = songsStore.songsById.has(item.songId)}
              <div
                class="after:bg-separator relative flex min-h-14 items-center py-1 pr-2 pl-1 after:absolute after:right-0 after:bottom-0 after:left-14 after:h-(--hairline) last:after:hidden"
              >
                <button
                  type="button"
                  class="text-primary hover:bg-accent focus-visible:ring-ring flex size-11 shrink-0 items-center justify-center rounded-full focus-visible:ring-2 focus-visible:outline-hidden disabled:opacity-40"
                  aria-label={`Play ${item.title ?? 'song'}`}
                  disabled={!available}
                  onclick={() => play(item.songId)}
                >
                  <Play class="size-5" aria-hidden="true" />
                </button>
                <a
                  href={`/track/${item.songId}`}
                  class="hover:bg-accent focus-visible:ring-ring flex min-w-0 flex-1 items-center gap-2 rounded-lg py-1.5 pr-1 pl-2 focus-visible:ring-2 focus-visible:outline-hidden"
                >
                  <span class="flex min-w-0 flex-1 flex-col">
                    <span class="flex min-w-0 items-center gap-1.5">
                      <span class="text-body truncate md:text-sm">{item.title ?? '—'}</span>
                      <span
                        class="text-caption-1 bg-warning/6 text-warning-text dark:bg-warning/15 shrink-0 rounded-full px-1.5 py-px"
                        >{verdictLabel(item.verdict)}</span
                      >
                    </span>
                    <span class="text-subheadline text-muted-foreground truncate md:text-xs">
                      {item.artist ?? '—'}{item.album ? ` · ${item.album}` : ''}
                    </span>
                    <span class="text-footnote text-muted-foreground line-clamp-2 md:text-xs">
                      {evidenceLine(item)}
                    </span>
                  </span>
                  <ChevronRight
                    class="text-muted-foreground-dim size-4 shrink-0"
                    aria-hidden="true"
                  />
                </a>
              </div>
            {/each}
            {#if list.items.length < list.foundTotal}
              <GroupedList.Row
                label={loadingMore
                  ? 'Loading…'
                  : `Show more (${(list.foundTotal - list.items.length).toLocaleString()} left)`}
                onclick={showMore}
                disabled={loadingMore}
              >
                {#snippet trailing()}
                  {#if loadingMore}<Loader2 class="size-4 animate-spin" aria-hidden="true" />{/if}
                {/snippet}
              </GroupedList.Row>
            {/if}
          {/if}
        </GroupedList.Section>

        <GroupedList.Section
          headingLevel={2}
          header="Every verdict"
          footer="A check that failed, for instance when a catalog did not answer, is tried again next time."
        >
          {#each VERDICT_ROWS as row (row.verdict)}
            <GroupedList.Row label={row.label} sublabel={row.meaning}>
              {#snippet trailing()}
                <span class="text-body text-muted-foreground tabular-nums"
                  >{(list?.counts[row.verdict] ?? 0).toLocaleString()}</span
                >
              {/snippet}
            </GroupedList.Row>
          {/each}
          <GroupedList.Row
            label="Not checked yet"
            sublabel="Built songs without a check for their current file."
          >
            {#snippet trailing()}
              <span class="text-body text-muted-foreground tabular-nums"
                >{notChecked.toLocaleString()}</span
              >
            {/snippet}
          </GroupedList.Row>
        </GroupedList.Section>
      {/if}
    </div>
  </ScrollArea>
</div>

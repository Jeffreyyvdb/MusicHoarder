<script lang="ts">
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import FilterChip from '$lib/components/v2/FilterChip.svelte';
  import { page } from '$app/state';
  import {
    fetchQualityOverview,
    fetchQualityProgress,
    fetchQualitySongs,
    gradeAllSongs,
    gradeOutdatedSongs,
    type QualityOverview,
    type QualityProgress,
    type QualityCategory,
    type QualitySongRow
  } from '$lib/api-client';
  import QualityStatusCards from '$lib/components/quality/QualityStatusCards.svelte';
  import QualityFixesRow from '$lib/components/quality/QualityFixesRow.svelte';
  import QualityList from '$lib/components/quality/QualityList.svelte';
  import QualityDetail from '$lib/components/quality/QualityDetail.svelte';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { replaceUrl } from '$lib/navigation/replace-url';
  import { prefersReducedMotion } from '$lib/motion';
  import { QUALITY_BUCKETS, VERDICT_TABS, issueLabel, qualityCategoryLabel } from '$lib/quality-ui';
  import { cn } from '$lib/utils';
  import { ChevronDown, History, Loader2, RefreshCw, Sparkles, TriangleAlert, X } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { tick, untrack } from 'svelte';

  // The AI-quality workbench. Two shapes, split at lg rather than the usual md, because the
  // master-detail needs the width:
  //  - lg+: the desktop split view — bucket cards, algorithm patterns, verdict chips, and the
  //    worst-first list beside the selected song's detail.
  //  - below lg: a drill-down with ONE scroller per page. The list page carries the buckets and
  //    top issues as inset lists and the songs as rows; a row pushes /quality?detail=<id>, whose
  //    nav bar goes Back to the list. (`detail`, not `song`: tab memory treats `song` as a
  //    one-shot parameter and would fold the detail into its list.)
  // The selected song lives in the URL at both widths, so a deep link opens it either way.

  let overview = $state<QualityOverview | null>(null);
  let isLoading = $state(true);
  let error = $state<string | null>(null);
  let busy = $state(false);
  let polling = $state(false);
  // Assume configured until the API tells us otherwise, so the button doesn't flash disabled.
  let gradingConfigured = $state(true);
  let gradingError = $state<QualityProgress['lastError']>(null);
  /** The running grade pass, for a determinate bar while it drains. */
  let gradeProgress = $state<{ processed: number; total: number } | null>(null);

  let category = $state<QualityCategory>('silent');
  let categoryInit = false;
  let songs = $state<QualitySongRow[]>([]);
  let songsTotal = $state(0);
  let songsLoading = $state(false);
  let songsLoaded = $state(false);
  let reqSeq = 0;
  let issueFilter = $state<string | null>(null);

  const narrowQuery = new IsMobile(1024);
  const narrow = $derived(narrowQuery.current);

  const detailParam = $derived.by(() => {
    const n = Number(page.url.searchParams.get('detail'));
    return Number.isInteger(n) && n > 0 ? n : null;
  });

  async function loadOverview() {
    try {
      const [ov, progress] = await Promise.all([fetchQualityOverview(), fetchQualityProgress()]);
      overview = ov;
      gradingConfigured = progress.aiGradingConfigured ?? true;
      gradingError = progress.lastError ?? null;
      error = null;
      if (!categoryInit) {
        categoryInit = true;
        category = ov.silentFailureCount > 0 ? 'silent' : ov.flaggedCount > 0 ? 'flagged' : 'all';
      }
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load quality overview';
    } finally {
      isLoading = false;
    }
  }

  $effect(() => {
    void loadOverview();
  });

  // Reload the list whenever the category changes (or the overview reloads after a re-grade).
  $effect(() => {
    if (!overview) return;
    const cat = category;
    void loadSongs(cat);
  });

  async function loadSongs(cat: QualityCategory) {
    const seq = ++reqSeq;
    songsLoading = true;
    try {
      const result = await fetchQualitySongs(cat, 0, 300);
      if (seq !== reqSeq) return; // a newer request superseded this one
      songs = result.items;
      songsTotal = result.total;
    } catch {
      if (seq === reqSeq) songs = [];
    } finally {
      if (seq === reqSeq) {
        songsLoading = false;
        songsLoaded = true;
      }
    }
  }

  function withDetail(id: number | null): string {
    const url = new URL(page.url);
    if (id == null) url.searchParams.delete('detail');
    else url.searchParams.set('detail', String(id));
    return `${url.pathname}${url.search}`;
  }

  function selectCategory(c: QualityCategory) {
    category = c;
    issueFilter = null;
    void revealList();
  }

  // On the drill-down a bucket or issue row changes the song list further down the page. When
  // that list's header is below the fold the tap would seem to do nothing, so bring it into view
  // (HIG scroll views: scroll automatically when relevant content is no longer in view). The
  // anchor's scroll-margin keeps it clear of the sticky nav bar.
  let listAnchor = $state<HTMLElement | null>(null);
  async function revealList() {
    if (!narrow) return;
    await tick();
    const el = listAnchor;
    const root = listScroller;
    if (!el || !root) return;
    const top = el.getBoundingClientRect().top;
    const view = root.getBoundingClientRect();
    // The scroller runs under the floating tab bar and mini player; its bottom padding is their
    // height (--mh-content-pad), so what is really visible ends that much higher.
    const visibleBottom = view.bottom - (parseFloat(getComputedStyle(root).paddingBottom) || 0);
    // Already showing: the header sits in view with room for a few rows under it.
    if (top >= view.top && top <= visibleBottom - 200) return;
    el.scrollIntoView({ block: 'start', behavior: prefersReducedMotion() ? 'auto' : 'smooth' });
  }

  // The split view swaps the detail in place (a replace, so Back leaves the page rather than
  // walking every row you looked at); the drill-down's rows are links that push.
  function selectSong(id: number) {
    void replaceUrl(withDetail(id));
  }

  function handleRegraded() {
    // Re-grade may move the song between buckets — refresh counts + the list.
    void loadOverview();
  }

  const displayedSongs = $derived(
    issueFilter ? songs.filter((s) => s.issues.some((i) => i.code === issueFilter)) : songs
  );

  // The URL's song while it is in the list; otherwise the split view shows the first row and the
  // drill-down shows the list.
  const selectedId = $derived.by(() => {
    if (detailParam != null && displayedSongs.some((s) => s.songId === detailParam)) return detailParam;
    return narrow ? null : (displayedSongs[0]?.songId ?? null);
  });
  const selectedRow = $derived(displayedSongs.find((s) => s.songId === selectedId) ?? null);
  const showDetailPage = $derived(narrow && detailParam != null);

  // A pushed detail whose song is not in the list (a deep link into another bucket, or a re-grade
  // that moved it out) goes back to the list rather than showing an empty page.
  $effect(() => {
    if (!showDetailPage || !songsLoaded || songsLoading) return;
    if (!displayedSongs.some((s) => s.songId === detailParam)) {
      void replaceUrl(untrack(() => withDetail(null)));
    }
  });

  const position = $derived.by(() => {
    const i = displayedSongs.findIndex((s) => s.songId === selectedId);
    return i < 0 ? undefined : `${(i + 1).toLocaleString()} of ${displayedSongs.length.toLocaleString()}`;
  });

  // The list page's scroll position survives a trip into a detail and back (the two pages are
  // separate scrollers, and the list's is recreated on return).
  let listScroller = $state<HTMLElement | null>(null);
  let savedListScroll = 0;
  function rememberListScroll() {
    savedListScroll = listScroller?.scrollTop ?? 0;
  }
  $effect(() => {
    const el = listScroller;
    if (!el || savedListScroll === 0) return;
    const frame = requestAnimationFrame(() => (el.scrollTop = savedListScroll));
    return () => cancelAnimationFrame(frame);
  });

  const lib = $derived(overview?.library ?? null);
  const coveragePct = $derived(overview ? Math.round(overview.coverage * 100) : 0);
  const avgScore = $derived(lib?.averageScore ?? null);
  const headerMeta = $derived(
    lib
      ? `${lib.graded.toLocaleString()} of ${overview?.gradeableTotal.toLocaleString()} graded · ${coveragePct}% coverage${avgScore != null ? ` · avg ${avgScore}` : ''}`
      : 'An AI grader checks each enrichment so you can benchmark the algorithm'
  );

  const tabCounts = $derived<Record<QualityCategory, number>>({
    all: lib?.graded ?? 0,
    wrong: lib?.verdicts.wrong ?? 0,
    questionable: lib?.verdicts.questionable ?? 0,
    good: lib?.verdicts.good ?? 0,
    excellent: lib?.verdicts.excellent ?? 0,
    ungradeable: lib?.verdicts.ungradeable ?? 0,
    'wrong-or-questionable': overview?.aiFlaggedCount ?? 0,
    silent: overview?.silentFailureCount ?? 0,
    flagged: overview?.flaggedCount ?? 0,
    verified: overview?.verifiedCleanCount ?? 0
  });

  async function pollUntilDone() {
    if (polling) return;
    polling = true;
    try {
      for (let i = 0; i < 600; i++) {
        const p = await fetchQualityProgress();
        gradingConfigured = p.aiGradingConfigured ?? true;
        gradingError = p.lastError ?? null;
        gradeProgress = p.total ? { processed: p.processed ?? 0, total: p.total } : null;
        if (!p.active) break;
        await new Promise((r) => setTimeout(r, 2000));
      }
      await loadOverview();
    } finally {
      polling = false;
      gradeProgress = null;
    }
  }

  // Re-grading the library queues every gradeable song for the LLM — whatever changed since its
  // last grade spends OpenRouter credits — and it is now the one tint-filled control on the page,
  // so it asks first. The outdated-only pass (a smaller, deliberate choice in More) does not.
  let confirmGradeAll = $state(false);

  async function onGradeAll() {
    busy = true;
    try {
      const r = await gradeAllSongs();
      toast.success(`Queued ${r.enqueued.toLocaleString()} songs for grading`);
      void pollUntilDone();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Failed to start grading');
    } finally {
      busy = false;
    }
  }

  async function onGradeOutdated() {
    busy = true;
    try {
      const r = await gradeOutdatedSongs();
      toast.success(`Queued ${r.enqueued.toLocaleString()} outdated songs for re-grading`);
      void pollUntilDone();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Failed to start grading');
    } finally {
      busy = false;
    }
  }

  function onSelectIssue(code: string) {
    // Jump to the full graded set so the issue is findable across the whole library.
    if (category !== 'all') category = 'all';
    issueFilter = code;
    void revealList();
  }
</script>

<!-- ── the list page's nav bar (both shapes) ─────────────────────────────────── -->
{#snippet listToolbar()}
  <PageToolbarV2 title="AI quality" meta={headerMeta} metaFrom="lg" grouped>
    {#snippet actions()}
      <Button
        onclick={() => (confirmGradeAll = true)}
        disabled={busy || polling || !gradingConfigured}
        title={gradingConfigured ? undefined : 'AI grading is not configured on the server'}
        class="rounded-full"
      >
        {#if busy || polling}<Loader2 class="animate-spin" aria-hidden="true" />{:else}<Sparkles aria-hidden="true" />{/if}
        <span class="max-md:sr-only">Re-grade library</span>
      </Button>
    {/snippet}
    {#snippet more()}
      {#if overview && overview.outdatedCount > 0}
        <!-- Grades made with an older prompt or model: re-grade just those. -->
        <DropdownMenu.Item onSelect={onGradeOutdated} disabled={busy || polling || !gradingConfigured}>
          <History />
          Re-grade {overview.outdatedCount.toLocaleString()} outdated
        </DropdownMenu.Item>
      {/if}
      <DropdownMenu.Item onSelect={loadOverview}>
        <RefreshCw />
        Refresh
      </DropdownMenu.Item>
    {/snippet}
  </PageToolbarV2>
{/snippet}

<!-- Configuration, credit and failure notices, and a running pass's progress. -->
{#snippet notices()}
  {#if !gradingConfigured}
    <GroupedList.Section>
      <GroupedList.Row icon={TriangleAlert} iconClass="bg-warning/15 text-warning-text">
        <span class="text-subheadline md:text-[12.5px]">
          AI grading is not configured on the server, so grading does nothing. Set
          <code class="font-mono text-[0.92em]">QUALITY_GRADING_API_KEY</code> (and optionally
          <code class="font-mono text-[0.92em]">QUALITY_GRADING_MODEL</code>) in the deployment environment and
          redeploy.
        </span>
      </GroupedList.Row>
    </GroupedList.Section>
  {/if}
  {#if gradingError}
    <GroupedList.Section>
      <GroupedList.Row icon={TriangleAlert} iconClass="bg-destructive/12 text-destructive-text">
        <span class="text-subheadline md:text-[12.5px]">
          {#if gradingError.code === 'out_of_credits'}
            AI quality grading is paused — your OpenRouter account is out of credits. Add credits at
            <a
              href="https://openrouter.ai/settings/credits"
              target="_blank"
              rel="noopener noreferrer"
              class="text-primary font-medium underline">openrouter.ai/settings/credits</a
            >
            and grading resumes automatically.
          {:else}
            AI quality grading is failing: {gradingError.message ?? 'unknown error'}.
          {/if}
        </span>
      </GroupedList.Row>
    </GroupedList.Section>
  {/if}
  {#if polling}
    <GroupedList.Section footer="Rollups refresh when it finishes.">
      <GroupedList.Row
        label={gradeProgress
          ? `Grading ${gradeProgress.processed.toLocaleString()} of ${gradeProgress.total.toLocaleString()}`
          : 'Grading in progress'}
      >
        {#snippet leading()}<Loader2 class="text-muted-foreground size-5 animate-spin" aria-hidden="true" />{/snippet}
        {#if gradeProgress}
          <span
            role="progressbar"
            aria-label="Grading progress"
            aria-valuemin={0}
            aria-valuemax={gradeProgress.total}
            aria-valuenow={gradeProgress.processed}
            class="bg-muted mt-1.5 mb-0.5 block h-1 w-full overflow-hidden rounded-full"
          >
            <span
              class="bg-primary block h-full w-full origin-left transition-transform duration-300 ease-out"
              style="transform: scaleX({Math.min(1, gradeProgress.processed / gradeProgress.total)})"
            ></span>
          </span>
        {/if}
      </GroupedList.Row>
    </GroupedList.Section>
  {/if}
{/snippet}

<!-- The list section's header on the drill-down: which songs it shows, as a pull-down of every
     category (the three buckets, then the verdicts) with counts, plus the issue filter. -->
{#snippet categoryPicker()}
  <div class="flex flex-wrap items-center justify-between gap-2">
    <DropdownMenu.Root>
      <DropdownMenu.Trigger>
        {#snippet child({ props })}
          <!-- 26px (the 18px header line plus its padding) and 9px of pseudo-element either
               side: a 44pt target that does not push the section down. -->
          <button
            {...props}
            type="button"
            class="text-primary focus-visible:ring-ring/50 relative -my-1 inline-flex items-center gap-1 rounded-md py-1 font-semibold outline-none focus-visible:ring-3 pointer-coarse:after:absolute pointer-coarse:after:-inset-x-2 pointer-coarse:after:-inset-y-[9px]"
          >
            {qualityCategoryLabel(category)}
            <span class="text-muted-foreground font-normal tabular-nums">{tabCounts[category].toLocaleString()}</span>
            <ChevronDown class="size-3.5" aria-hidden="true" />
            <span class="sr-only">— change what the list shows</span>
          </button>
        {/snippet}
      </DropdownMenu.Trigger>
      <DropdownMenu.Content align="start" class="min-w-60">
        <DropdownMenu.RadioGroup value={category} onValueChange={(v) => selectCategory(v as QualityCategory)}>
          {#each QUALITY_BUCKETS as b (b.id)}
            <DropdownMenu.RadioItem value={b.id}>
              <span class="flex-1">{b.label}</span>
              <span class="text-muted-foreground tabular-nums">{tabCounts[b.id].toLocaleString()}</span>
            </DropdownMenu.RadioItem>
          {/each}
          <DropdownMenu.Separator />
          {#each VERDICT_TABS as t (t.id)}
            <DropdownMenu.RadioItem value={t.id}>
              <span class="flex-1">{t.label}</span>
              <span class="text-muted-foreground tabular-nums">{tabCounts[t.id].toLocaleString()}</span>
            </DropdownMenu.RadioItem>
          {/each}
        </DropdownMenu.RadioGroup>
      </DropdownMenu.Content>
    </DropdownMenu.Root>
    {#if issueFilter}
      {@render issuePill()}
    {/if}
  </div>
{/snippet}

<!-- The active issue filter as a removable token, in words ("Wrong recording ✕"); its 44pt hit
     area grows sideways as well as up and down. -->
{#snippet issuePill()}
  {@const label = issueFilter ? issueLabel(issueFilter) : ''}
  <button
    type="button"
    onclick={() => (issueFilter = null)}
    aria-label="Clear the issue filter {label}"
    title={issueFilter ?? undefined}
    class="bg-primary/12 text-primary focus-visible:ring-ring/50 relative inline-flex items-center gap-1.5 rounded-full px-2.5 py-0.5 text-[12px] outline-none focus-visible:ring-3 pointer-coarse:after:absolute pointer-coarse:after:-inset-x-2 pointer-coarse:after:-inset-y-2.5"
  >
    <span>{label}</span>
    <X class="size-3" aria-hidden="true" />
  </button>
{/snippet}

{#if showDetailPage}
  <!-- Drill-down, level 2: one song. QualityDetail renders the nav bar as this scroller's first
       child. -->
  <div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
    {#key detailParam}
      <ScrollArea class="min-h-0 flex-1" viewportClass="overscroll-contain">
        <QualityDetail
          layout="page"
          row={selectedRow}
          back={{ label: 'AI quality', href: withDetail(null) }}
          {position}
          onRegraded={handleRegraded}
        />
      </ScrollArea>
    {/key}
  </div>
{:else}
  <div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
    <!-- The page's one scroller. On a desktop its content is a flex column, so the split view
         grows to fill the viewport when there is room while keeping a height floor (below) so the
         blocks above can never squeeze the detail into a sliver. -->
    <ScrollArea
      bind:viewportRef={listScroller}
      class="min-h-0 flex-1 lg:[&>[data-slot=scroll-area-viewport]>*]:flex lg:[&>[data-slot=scroll-area-viewport]>*]:flex-col"
      viewportClass="overscroll-contain"
    >
      {@render listToolbar()}

      <div
        class={cn(
          'mx-auto flex w-full flex-col gap-7 pt-2 pb-8 md:px-7 md:pt-6',
          narrow ? 'max-w-3xl' : 'gap-4 lg:flex-1'
        )}
      >
        {@render notices()}

        {#if error}
          <GroupedList.Section>
            <GroupedList.Row
              icon={TriangleAlert}
              iconClass="bg-destructive/12 text-destructive-text"
              label="Couldn't load quality rollups"
              sublabel={error}
            />
          </GroupedList.Section>
        {:else if isLoading}
          <div class="text-muted-foreground text-body flex items-center justify-center gap-2 py-10 md:text-[13px]">
            <Loader2 class="size-4 animate-spin" aria-hidden="true" /> Loading quality rollups…
          </div>
        {:else if overview && lib}
          {#if narrow}
            <!-- Drill-down, level 1. -->
            <QualityStatusCards
              layout="rows"
              flagged={overview.flaggedCount}
              silent={overview.silentFailureCount}
              verified={overview.verifiedCleanCount}
              graded={lib.graded}
              active={category}
              onSelect={selectCategory}
            />
            <QualityFixesRow layout="rows" topIssues={lib.topIssues} {onSelectIssue} />
            {#if lib.graded === 0}
              <p class="text-body text-muted-foreground px-8 py-8 text-center md:px-4">
                Nothing graded yet. Use Re-grade library to start grading, or grade a single song from its
                provenance timeline.
              </p>
            {:else}
              <div bind:this={listAnchor} class="scroll-mt-[calc(var(--mh-navbar-h,0px)+8px)]">
                <QualityList
                  layout="inline"
                  songs={displayedSongs}
                  {selectedId}
                  loading={songsLoading}
                  total={issueFilter ? null : songsTotal}
                  hrefFor={(id) => withDetail(id)}
                  onNavigate={rememberListScroll}
                  header={categoryPicker}
                />
              </div>
            {/if}
          {:else}
            <!-- The desktop split view. -->
            <div class="shrink-0">
              <QualityStatusCards
                flagged={overview.flaggedCount}
                silent={overview.silentFailureCount}
                verified={overview.verifiedCleanCount}
                graded={lib.graded}
                active={category}
                onSelect={selectCategory}
              />
            </div>
            <div class="shrink-0">
              <QualityFixesRow topIssues={lib.topIssues} {onSelectIssue} />
            </div>

            {#if lib.graded === 0}
              <div class="bg-card text-muted-foreground rounded-xl px-4 py-10 text-center text-[13px]">
                Nothing graded yet. Use <span class="text-foreground font-medium">Re-grade library</span> to start
                grading, or grade a single song from the Provenance &amp; review page.
              </div>
            {:else}
              <!-- Verdict chips (the same categories as the cards, by AI verdict). -->
              <div class="flex shrink-0 flex-wrap items-center gap-2" role="group" aria-label="Show songs">
                {#each VERDICT_TABS as t (t.id)}
                  <FilterChip pressed={category === t.id} onclick={() => selectCategory(t.id)} count={tabCounts[t.id]}>
                    {#if t.dot}<span class={cn('size-2 rounded-full', t.dot)} aria-hidden="true"></span>{/if}
                    {t.label}
                  </FilterChip>
                {/each}
                {#if issueFilter}
                  <span class="ml-auto">{@render issuePill()}</span>
                {/if}
              </div>

              <!-- Master–detail split: the grid grows to fill the remaining height but never below
                   a usable floor, so each pane keeps real height and scrolls internally, once. -->
              <div class="grid min-h-[640px] flex-1 grid-cols-[340px_1fr] grid-rows-1 gap-3">
                <div class="min-h-0">
                  <QualityList
                    songs={displayedSongs}
                    {selectedId}
                    onSelect={selectSong}
                    loading={songsLoading}
                    total={issueFilter ? null : songsTotal}
                  />
                </div>
                <div class="min-h-0">
                  <QualityDetail row={selectedRow} onRegraded={handleRegraded} />
                </div>
              </div>
            {/if}
          {/if}
        {/if}
      </div>
    </ScrollArea>
  </div>
{/if}

<AlertDialog.Root bind:open={confirmGradeAll}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Re-grade the library?</AlertDialog.Title>
      <AlertDialog.Description>
        {overview
          ? `Queues all ${overview.gradeableTotal.toLocaleString()} gradeable songs for the AI grader.`
          : 'Queues every gradeable song for the AI grader.'}
        Songs unchanged since their last grade are skipped; the rest use your OpenRouter credits.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action
        onclick={() => {
          confirmGradeAll = false;
          void onGradeAll();
        }}>Re-grade</AlertDialog.Action
      >
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

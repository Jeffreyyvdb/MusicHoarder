<script lang="ts">
  import {
    fetchQualityOverview,
    fetchQualityProgress,
    fetchQualitySongs,
    gradeAllSongs,
    type QualityOverview,
    type QualityProgress,
    type QualityCategory,
    type QualitySongRow
  } from '$lib/api-client';
  import QualityStatusCards from '$lib/components/quality/QualityStatusCards.svelte';
  import QualityFixesRow from '$lib/components/quality/QualityFixesRow.svelte';
  import QualityList from '$lib/components/quality/QualityList.svelte';
  import QualityDetail from '$lib/components/quality/QualityDetail.svelte';
  import { VERDICT_TABS } from '$lib/quality-ui';
  import { cn } from '$lib/utils';
  import { Gauge, Loader2, RefreshCw, Sparkles, X } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { untrack } from 'svelte';

  let overview = $state<QualityOverview | null>(null);
  let isLoading = $state(true);
  let error = $state<string | null>(null);
  let busy = $state(false);
  let polling = $state(false);
  // Assume configured until the API tells us otherwise, so the button doesn't flash disabled.
  let gradingConfigured = $state(true);
  let gradingError = $state<QualityProgress['lastError']>(null);

  let category = $state<QualityCategory>('silent');
  let categoryInit = false;
  let songs = $state<QualitySongRow[]>([]);
  let songsTotal = $state(0);
  let songsLoading = $state(false);
  let reqSeq = 0;
  let selectedId = $state<number | null>(null);
  let mobileDetailOpen = $state(false);
  let issueFilter = $state<string | null>(null);

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
      const page = await fetchQualitySongs(cat, 0, 300);
      if (seq !== reqSeq) return; // a newer request superseded this one
      songs = page.items;
      songsTotal = page.total;
      // Preserve the current selection if it survived; otherwise select the first row.
      const cur = untrack(() => selectedId);
      if (!songs.some((s) => s.songId === cur)) selectedId = songs[0]?.songId ?? null;
    } catch {
      if (seq === reqSeq) songs = [];
    } finally {
      if (seq === reqSeq) songsLoading = false;
    }
  }

  function selectCategory(c: QualityCategory) {
    category = c;
    issueFilter = null;
    mobileDetailOpen = false;
  }

  function selectSong(id: number) {
    selectedId = id;
    mobileDetailOpen = true;
  }

  function handleRegraded() {
    // Re-grade may move the song between buckets — refresh counts + the list.
    void loadOverview();
  }

  const displayedSongs = $derived(
    issueFilter ? songs.filter((s) => s.issues.some((i) => i.code === issueFilter)) : songs
  );
  const selectedRow = $derived(displayedSongs.find((s) => s.songId === selectedId) ?? null);

  // Keep a valid selection within the displayed list.
  $effect(() => {
    const list = displayedSongs;
    const cur = untrack(() => selectedId);
    if (list.length === 0) {
      if (cur !== null) selectedId = null;
    } else if (cur == null || !list.some((s) => s.songId === cur)) {
      selectedId = list[0].songId;
    }
  });

  const lib = $derived(overview?.library ?? null);
  const coveragePct = $derived(overview ? Math.round(overview.coverage * 100) : 0);
  const avgScore = $derived(lib?.averageScore ?? null);

  const tabCounts = $derived({
    all: lib?.graded ?? 0,
    wrong: lib?.verdicts.wrong ?? 0,
    questionable: lib?.verdicts.questionable ?? 0,
    good: lib?.verdicts.good ?? 0,
    excellent: lib?.verdicts.excellent ?? 0,
    ungradeable: lib?.verdicts.ungradeable ?? 0
  });

  async function pollUntilDone() {
    if (polling) return;
    polling = true;
    try {
      for (let i = 0; i < 600; i++) {
        const p = await fetchQualityProgress();
        gradingConfigured = p.aiGradingConfigured ?? true;
        gradingError = p.lastError ?? null;
        if (!p.active) break;
        await new Promise((r) => setTimeout(r, 2000));
      }
      await loadOverview();
    } finally {
      polling = false;
    }
  }

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
</script>

<div class="flex h-full min-h-0 flex-col">
  <header class="border-border flex flex-wrap items-center gap-3 border-b px-4 py-4 sm:px-6">
    <Gauge class="text-primary size-5 shrink-0" />
    <div class="min-w-0 flex-1">
      <h1 class="truncate text-base font-semibold">AI library quality</h1>
      <p class="text-muted-foreground text-[12px]">
        {#if lib}
          <span class="font-mono">{lib.graded.toLocaleString()}</span> of
          <span class="font-mono">{overview?.gradeableTotal.toLocaleString()}</span> graded
          <span class="text-muted-foreground/50">·</span>
          {coveragePct}% coverage
          {#if avgScore != null}
            <span class="text-muted-foreground/50">·</span> avg <span class="font-mono">{avgScore}</span>
          {/if}
        {:else}
          An LLM grades each enrichment so you can benchmark and debug the algorithm.
        {/if}
      </p>
    </div>
    <button
      type="button"
      disabled={busy || polling || !gradingConfigured}
      onclick={onGradeAll}
      title={gradingConfigured ? undefined : 'AI grading is not configured on the server'}
      class="bg-primary text-primary-foreground inline-flex items-center gap-1.5 rounded-md px-3 py-1.5 text-[12.5px] font-medium transition-opacity hover:opacity-90 disabled:opacity-50"
    >
      {#if busy || polling}<Loader2 class="size-3.5 animate-spin" />{:else}<Sparkles class="size-3.5" />{/if}
      Re-grade library
    </button>
    <button
      type="button"
      onclick={loadOverview}
      aria-label="Refresh"
      class="border-border hover:bg-accent inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12.5px] transition-colors"
    >
      <RefreshCw class={cn('size-3.5', isLoading && 'animate-spin')} />
    </button>
  </header>

  <!-- The whole area scrolls at every breakpoint. On desktop it's a flex column so the
       master–detail split grows to fill the viewport when there's room, but it keeps a
       height floor (below) so the top blocks can never squeeze the detail pane into a sliver. -->
  <div class="min-h-0 flex-1 space-y-4 overflow-y-auto px-4 py-4 sm:px-6 lg:flex lg:flex-col lg:gap-4 lg:space-y-0">
    {#if !gradingConfigured}
      <div class="rounded-md border border-amber-500/30 bg-amber-500/10 px-4 py-3 text-[12.5px] text-amber-700 dark:text-amber-400">
        AI grading is not configured on the server, so grading does nothing. Set
        <code class="font-mono text-[12px]">QUALITY_GRADING_API_KEY</code> (and optionally
        <code class="font-mono text-[12px]">QUALITY_GRADING_MODEL</code>) in the deployment environment and redeploy.
      </div>
    {/if}
    {#if gradingError}
      <div class="rounded-md border border-red-500/30 bg-red-500/10 px-4 py-3 text-[12.5px] text-red-600 dark:text-red-400">
        {#if gradingError.code === 'out_of_credits'}
          AI quality grading is paused — your OpenRouter account is out of credits. Add credits at
          <a href="https://openrouter.ai/settings/credits" target="_blank" rel="noopener noreferrer" class="font-medium underline">openrouter.ai/settings/credits</a>
          and grading resumes automatically.
        {:else}
          AI quality grading is failing: {gradingError.message ?? 'unknown error'}.
        {/if}
      </div>
    {/if}

    {#if error}
      <div class="rounded-md border border-red-500/30 bg-red-500/10 px-4 py-3 text-[13px] text-red-600 dark:text-red-400">{error}</div>
    {:else if isLoading}
      <div class="text-muted-foreground flex items-center gap-2 py-10 text-[13px]">
        <Loader2 class="size-4 animate-spin" /> Loading quality rollups…
      </div>
    {:else if overview && lib}
      {#if polling}
        <div class="text-muted-foreground flex items-center gap-2 rounded-md border border-amber-500/30 bg-amber-500/10 px-3 py-2 text-[12.5px]">
          <Loader2 class="size-3.5 animate-spin" /> Grading in progress — rollups refresh when it finishes.
        </div>
      {/if}

      <div class="lg:shrink-0">
        <QualityStatusCards
          flagged={overview.flaggedCount}
          silent={overview.silentFailureCount}
          verified={overview.verifiedCleanCount}
          graded={lib.graded}
          active={category}
          onSelect={selectCategory}
        />
      </div>

      <div class="lg:shrink-0">
        <QualityFixesRow
          topIssues={lib.topIssues}
          onSelectIssue={(code) => {
            // Jump to the full graded set so the issue is findable across the whole library.
            if (category !== 'all') category = 'all';
            issueFilter = code;
            mobileDetailOpen = false;
          }}
        />
      </div>

      {#if lib.graded === 0}
        <div class="text-muted-foreground rounded-md border border-dashed px-4 py-10 text-center text-[13px]">
          Nothing graded yet. Click <span class="text-foreground font-medium">Re-grade library</span> to start grading, or grade a
          single song from the Provenance &amp; review page.
        </div>
      {:else}
        <!-- Category tabs -->
        <div class="border-border flex flex-wrap items-center gap-2 border-b pb-2 lg:shrink-0">
          <div class="flex flex-wrap gap-1.5">
            {#each VERDICT_TABS as t (t.id)}
              {@const active = category === t.id}
              <button
                type="button"
                onclick={() => selectCategory(t.id)}
                class={cn(
                  'inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1 text-[12px] transition-colors',
                  active ? 'bg-card border-border text-foreground font-medium shadow-sm' : 'text-muted-foreground hover:bg-muted/60 border-border'
                )}
              >
                {#if t.dot}<span class={cn('size-1.5 rounded-full', t.dot)}></span>{/if}
                <span>{t.label}</span>
                <span class={cn('rounded px-1.5 font-mono text-[10.5px] tabular-nums', active ? 'bg-primary/10 text-primary' : 'bg-muted text-muted-foreground')}>
                  {(tabCounts[t.id as keyof typeof tabCounts] ?? 0).toLocaleString()}
                </span>
              </button>
            {/each}
          </div>
          {#if issueFilter}
            <button
              type="button"
              onclick={() => (issueFilter = null)}
              class="border-primary/40 bg-primary/10 text-primary ml-auto inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5 text-[11px]"
            >
              <span class="font-mono">issue: {issueFilter}</span>
              <X class="size-3" />
            </button>
          {/if}
        </div>

        <!-- Master–detail split: drill-down on mobile (h-[68vh] panes); on desktop the grid
             grows to fill remaining flex height (flex-1) but never below a usable floor
             (min-h), so each pane keeps real height and scrolls internally, once. -->
        <div class="grid gap-3 lg:min-h-[640px] lg:flex-1 lg:grid-cols-[340px_1fr] lg:grid-rows-1">
          <div class={cn('h-[68vh] min-h-0 lg:h-auto', mobileDetailOpen ? 'hidden lg:block' : 'block')}>
            <QualityList
              songs={displayedSongs}
              {selectedId}
              onSelect={selectSong}
              loading={songsLoading}
              total={issueFilter ? null : songsTotal}
            />
          </div>
          <div class={cn('h-[68vh] min-h-0 lg:h-auto', mobileDetailOpen ? 'block' : 'hidden lg:block')}>
            <QualityDetail row={selectedRow} onBack={() => (mobileDetailOpen = false)} onRegraded={handleRegraded} />
          </div>
        </div>
      {/if}
    {/if}
  </div>
</div>

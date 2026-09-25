<script lang="ts">
  import { untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import {
    Check,
    ChevronUp,
    ChevronDown,
    RefreshCw,
    Sparkles,
    ChevronRight,
    Copy,
    TriangleAlert,
    Info,
    History,
    Loader2
  } from '@lucide/svelte';
  import {
    fetchQualityOverview,
    fetchEnrichmentDetail,
    copyQualitySongDossier,
    enrichSong,
    type QualityWorstOffender,
    type QualityVerdict
  } from '$lib/api-client';
  import { issueLabel } from '$lib/quality-ui';
  import { formatDate } from '$lib/formatters';
  import { coverUrlForSongId } from './song-cover';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { toast } from 'svelte-sonner';
  import { cn } from '$lib/utils';
  import InboxDecisionBar from './InboxDecisionBar.svelte';
  import InboxQueueRow from './InboxQueueRow.svelte';
  import InboxQueueStates from './InboxQueueStates.svelte';
  import { QueueSelection } from './queue-selection.svelte';

  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  let offenders = $state<QualityWorstOffender[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let configured = $state(true);

  // Invoke via untrack() so this effect tracks `loading`/`offenders` only, not
  // the `oncount` prop identity — see the note in InboxTagReviewV2 for why
  // tracking it loops (effect_update_depth_exceeded).
  $effect(() => {
    const n = loading ? null : offenders.length;
    untrack(() => oncount?.(n));
  });

  async function load() {
    try {
      loading = true;
      error = null;
      const ov = await fetchQualityOverview();
      // "AI flagged" = worst offenders the grader marked Wrong / Questionable.
      offenders = (ov.worstOffenders ?? []).filter(
        (o) => o.verdict === 'Wrong' || o.verdict === 'Questionable'
      );
      configured = (ov.library?.graded ?? 0) > 0 || offenders.length > 0;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load AI grades';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  const offenderIds = $derived(offenders.map((o) => o.songId));
  const selection = new QueueSelection({
    tab: 'ai',
    param: 'song',
    ids: () => offenderIds,
    compact: () => compact,
    ready: () => !loading
  });
  const position = $derived(selection.position);
  const selected = $derived(offenders.find((o) => o.songId === selection.selectedId) ?? null);

  // One status signal: a small coloured dot AND the verdict word — colour never carries it alone.
  function verdictDot(v: QualityVerdict | undefined): string {
    switch (v) {
      case 'Wrong':
        return 'bg-destructive';
      case 'Questionable':
        return 'bg-warning';
      default:
        return 'bg-muted-foreground-dim';
    }
  }

  // The grader rates each issue low / medium / high (older grades said minor / major). Each level
  // is a word and a glyph, never a dot colour alone.
  type Severity = 'high' | 'medium' | 'low';
  function severityOf(severity: string | null | undefined): Severity {
    const v = severity?.toLowerCase();
    if (v === 'high' || v === 'major') return 'high';
    if (v === 'low' || v === 'minor') return 'low';
    return 'medium';
  }
  const SEVERITY: Record<
    Severity,
    { word: string; icon: typeof Info; tile?: string; text: string }
  > = {
    high: {
      word: 'High',
      icon: TriangleAlert,
      tile: 'bg-destructive/12 text-destructive-text',
      text: 'text-destructive-text'
    },
    medium: {
      word: 'Medium',
      icon: TriangleAlert,
      tile: 'bg-warning/15 text-warning-text',
      text: 'text-warning-text'
    },
    low: { word: 'Low', icon: Info, text: 'text-muted-foreground' }
  };

  async function onCopyDossier(songId: number) {
    try {
      await copyQualitySongDossier(songId);
      toast.success(
        'Copied dossier to clipboard — paste into an AI assistant for a second opinion'
      );
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Copy failed');
    }
  }

  function reviewHref(songId: number): string {
    return `/inbox?tab=review&song=${songId}`;
  }
  function timelineHref(songId: number): string {
    return `/track/${songId}`;
  }

  // "Open in review" only leads somewhere while the track is waiting in Tag review — and an
  // AI-graded track is usually built (Matched), so most of the time it is not. The status at
  // grade time is history; the live one comes from the track's enrichment detail, fetched for
  // the item on screen. Unknown yet: the timeline, which always exists. Unreadable: offer
  // review anyway (Tag review says so if the track is not there).
  let liveStatus = $state<Record<number, string | null>>({});
  $effect(() => {
    const id = selection.selectedId;
    if (id == null) return;
    untrack(() => {
      if (id in liveStatus) return;
      fetchEnrichmentDetail(id)
        .then((d) => (liveStatus = { ...liveStatus, [id]: d.enrichmentStatus ?? '' }))
        .catch(() => (liveStatus = { ...liveStatus, [id]: null }));
    });
  });
  function reviewable(songId: number): boolean {
    if (!(songId in liveStatus)) return false;
    const status = liveStatus[songId];
    return status === null || status.toLowerCase() === 'needsreview';
  }

  // The item's resolving action. A track waiting in Tag review is fixed there (Open in review);
  // a built one the grader calls wrong is fixed by matching it again from its original tags
  // (Re-enrich — the timeline's own action), which lands it in Tag review if the providers still
  // disagree. The grade itself stays until the next grading pass.
  let reenriching = $state<number | null>(null);
  async function reenrich(songId: number) {
    if (reenriching != null) return;
    reenriching = songId;
    try {
      const r = await enrichSong(songId, true);
      const d = await fetchEnrichmentDetail(songId).catch(() => null);
      liveStatus = { ...liveStatus, [songId]: d ? (d.enrichmentStatus ?? '') : null };
      const said: Record<string, string> = {
        Matched: 'Re-enriched — matched again',
        NeedsReview: 'Re-enriched — it’s waiting in Tag review',
        Failed: 'Re-enriched — no provider found a match',
        Skipped: 'Re-enrich skipped this track'
      };
      toast.success(said[r.outcome] ?? `Re-enriched: ${r.outcome}`);
    } catch (err) {
      toast.error('Could not re-enrich this track', {
        description: err instanceof Error ? err.message : undefined
      });
    } finally {
      reenriching = null;
    }
  }

  function algorithmRows(o: QualityWorstOffender): { l: string; v: string; mono?: boolean }[] {
    return [
      { l: 'Title', v: o.title ?? '—' },
      { l: 'Artist', v: o.artist ?? '—' },
      { l: 'Album', v: o.album ?? '—' },
      { l: 'Source', v: o.sourcePath, mono: true },
      { l: 'Destination', v: o.destinationPathPreview ?? '(not written)', mono: true },
      { l: 'Status at grade', v: o.enrichmentStatusAtGrade ?? '—' }
    ];
  }

  // The phone's list unmounts while an item is pushed; put it back where it was on return.
  let listScroller = $state<HTMLElement | null>(null);
  let listScrollTop = 0;
  $effect(() => {
    const el = listScroller;
    if (el && listScrollTop > 0) requestAnimationFrame(() => (el.scrollTop = listScrollTop));
  });
  let detailScroller = $state<HTMLElement | null>(null);
  $effect(() => {
    void selection.selectedId;
    untrack(() => detailScroller?.scrollTo({ top: 0 }));
  });
  function openRow(event: MouseEvent, id: number) {
    if (compact && listScroller) listScrollTop = listScroller.scrollTop;
    selection.onRowClick(event, id);
  }

  const meta = $derived(loading ? undefined : `${offenders.length} flagged by AI`);
</script>

<!-- One line of text after the dot, so the separators are evenly spaced (a second flex item
     would sit a gap away from the score instead of a space). -->
{#snippet verdictLine(o: QualityWorstOffender, outOf = false, after = '')}
  <span
    class={cn('size-2 shrink-0 rounded-full md:size-1.5', verdictDot(o.verdict))}
    aria-hidden="true"
  ></span>
  <span class="min-w-0 truncate tabular-nums"
    >{o.verdict} · {o.score}{outOf ? '/100' : ''}{after ? ` · ${after}` : ''}</span
  >
{/snippet}

{#snippet queueList()}
  {#each offenders as o (o.songId)}
    <InboxQueueRow
      href={selection.href(o.songId)}
      onclick={(e) => openRow(e, o.songId)}
      selected={selection.selectedId === o.songId}
      {compact}
      cover={{
        artist: o.artist ?? 'Unknown',
        title: o.title ?? o.fileName,
        url: coverUrlForSongId(o.songId)
      }}
      title={o.title ?? o.fileName}
    >
      {#snippet detail()}
        {@render verdictLine(o, true, o.artist ?? '—')}
      {/snippet}
    </InboxQueueRow>
  {/each}
{/snippet}

{#snippet refreshItem()}
  <DropdownMenu.Item onSelect={() => void load()}><RefreshCw /> Refresh</DropdownMenu.Item>
{/snippet}

{#snippet refreshAction()}
  <Button
    variant="ghost"
    size="icon"
    aria-label="Refresh AI grades"
    title="Refresh"
    onclick={() => void load()}
  >
    <RefreshCw />
  </Button>
{/snippet}

<!-- The secondary actions, as Tag review has them: the bottom bar keeps the one that resolves
     the item. -->
{#snippet detailMore()}
  {#if selected}
    {@const id = selected.songId}
    <DropdownMenu.Item onSelect={() => void onCopyDossier(id)}>
      <Copy /> Copy dossier
    </DropdownMenu.Item>
    <DropdownMenu.Item onSelect={() => void goto(timelineHref(id))}>
      <History /> View timeline
    </DropdownMenu.Item>
  {/if}
{/snippet}

<!-- The resolving action (see reenrich). Until the track's live status is known it waits,
     disabled, rather than offering one action and swapping it for the other. -->
{#snippet resolveAction(songId: number, cls: string, iconCls: string)}
  {#if reviewable(songId)}
    <Button href={reviewHref(songId)} class={cls}>
      <span class="truncate">Open in review</span>
      <ChevronRight class={iconCls} />
    </Button>
  {:else}
    <Button
      class={cls}
      disabled={!(songId in liveStatus) || reenriching != null}
      onclick={() => void reenrich(songId)}
    >
      {#if reenriching === songId}<Loader2 class="{iconCls} animate-spin" />{:else}<RefreshCw
          class={iconCls}
        />{/if}
      <span class="truncate">Re-enrich</span>
    </Button>
  {/if}
{/snippet}

{#snippet chevrons()}
  <Button
    variant="ghost"
    size="icon"
    aria-label="Previous flagged track"
    disabled={position.prev == null}
    onclick={() => selection.select(position.prev)}
  >
    <ChevronUp />
  </Button>
  <Button
    variant="ghost"
    size="icon"
    aria-label="Next flagged track"
    disabled={position.next == null}
    onclick={() => selection.select(position.next)}
  >
    <ChevronDown />
  </Button>
{/snippet}

{#snippet queueStates()}
  {#if loading}
    <InboxQueueStates state="loading" label="Loading AI grades…" {compact} />
  {:else if error}
    <InboxQueueStates state="error" message={error} onretry={load} />
  {:else}
    <InboxQueueStates
      state="empty"
      icon={configured ? Check : Sparkles}
      title={configured ? 'Nothing flagged by AI' : 'AI grading not run yet'}
    >
      {#if configured}
        The quality grader hasn't marked any built tracks Wrong or Questionable.
      {:else}
        Run AI quality grading from the
        <a href="/quality" class="text-primary hover:underline">AI quality</a> page to surface enrichments
        that look wrong.
      {/if}
    </InboxQueueStates>
  {/if}
{/snippet}

{#if compact}
  {#if selected}
    <!-- ── Phone: the pushed verdict, a grouped page ─────────────────────────────── -->
    <div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
      <ScrollArea
        bind:viewportRef={detailScroller}
        class="min-h-0 flex-1"
        viewportClass="overscroll-contain"
      >
        <PageToolbarV2
          title="{position.position} of {position.total}"
          titleLabel="AI flagged"
          meta="AI flagged"
          largeTitle={false}
          grouped
          actions={chevrons}
          more={detailMore}
        />
        <div class="flex flex-col gap-7 pt-3 pb-6">
          <GroupedList.Section footer="Graded {formatDate(selected.gradedAtUtc)}">
            <div class="flex items-center gap-3 px-4 py-3">
              <Cover
                artist={selected.artist ?? 'Unknown'}
                title={selected.title ?? selected.fileName}
                coverUrl={coverUrlForSongId(selected.songId)}
                size={60}
                corner={6}
                caption={false}
                dprCap={3}
              />
              <div class="min-w-0 flex-1">
                <h2 class="text-headline line-clamp-2 break-words">
                  {selected.title ?? selected.fileName}
                </h2>
                {#if selected.artist}
                  <p class="text-subheadline text-muted-foreground truncate">{selected.artist}</p>
                {/if}
                <p class="text-subheadline mt-0.5 flex items-center gap-1.5 tabular-nums">
                  {@render verdictLine(selected, true)}
                </p>
              </div>
            </div>
          </GroupedList.Section>

          <GroupedList.Section header="AI grader’s verdict">
            <p
              class={cn(
                'text-body after:bg-separator relative px-4 py-3 after:absolute after:right-0 after:bottom-0 after:left-4 after:h-(--hairline) last:after:hidden',
                !selected.summary && 'text-muted-foreground'
              )}
            >
              {selected.summary || 'No summary provided by the grader.'}
            </p>
            {#each selected.issues as issue (issue.code)}
              <!-- Severity is a word and a glyph, not a dot colour alone; the detail (a hover
                   title on desktop) is simply shown. The raw code stays in the dossier. -->
              {@const sev = SEVERITY[severityOf(issue.severity)]}
              <GroupedList.Row
                label={issueLabel(issue.code)}
                sublabel={issue.detail || undefined}
                value={sev.word}
                icon={sev.icon}
                iconClass={sev.tile}
              />
            {/each}
          </GroupedList.Section>

          <GroupedList.Section header="What the algorithm did">
            {#each algorithmRows(selected) as row (row.l)}
              <div
                class="after:bg-separator relative px-4 py-2.5 after:absolute after:right-0 after:bottom-0 after:left-4 after:h-(--hairline) last:after:hidden"
              >
                <div class="text-footnote text-muted-foreground">{row.l}</div>
                <div
                  class={cn(
                    'min-w-0 break-words',
                    row.mono ? 'text-footnote font-mono break-all' : 'text-body'
                  )}
                >
                  {row.v}
                </div>
              </div>
            {/each}
          </GroupedList.Section>
        </div>
      </ScrollArea>

      <!-- One action: the one that resolves the item. Copy dossier and View timeline are in the
           nav bar's More, as on Tag review. Larger text sizes truncate its label (see
           InboxDecisionBar). -->
      <InboxDecisionBar label="Flagged track actions">
        {@render resolveAction(
          selected.songId,
          'text-headline ml-auto h-11 gap-1.5 rounded-full pr-4 pl-5 in-data-tight:min-w-0 in-data-tight:shrink',
          'size-5'
        )}
      </InboxDecisionBar>
    </div>
  {:else}
    <!-- ── Phone: the list ───────────────────────────────────────────────────────── -->
    <div class="flex min-h-0 flex-1 flex-col">
      <ScrollArea
        bind:viewportRef={listScroller}
        class="min-h-0 flex-1"
        viewportClass="overscroll-contain"
      >
        <PageToolbarV2 title="AI flagged" {meta} more={refreshItem} />
        {#if loading || error || offenders.length === 0}
          {@render queueStates()}
        {:else}
          {@render queueList()}
        {/if}
      </ScrollArea>
    </div>
  {/if}
{:else}
  <!-- ── Desktop: list pane + detail pane ──────────────────────────────────────────── -->
  <div class="flex min-h-0 flex-1 flex-col">
    <PageToolbarV2 title="AI flagged" {meta} actions={refreshAction} />
    {#if !loading && (error || offenders.length === 0)}
      <ScrollArea class="min-h-0 flex-1">
        {@render queueStates()}
      </ScrollArea>
    {:else}
      <!-- The list pane gives up width first (down to 240px); see InboxTagReviewV2. -->
      <div
        class="grid min-h-0 flex-1 grid-cols-[clamp(240px,30%,320px)_minmax(0,1fr)] overflow-hidden"
      >
        <aside
          aria-label="Flagged tracks"
          class="border-separator bg-surface-sunken flex min-h-0 flex-col border-r"
        >
          <ScrollArea class="min-h-0 flex-1">
            <div class="p-1.5">
              {#if loading}
                {@render queueStates()}
              {:else}
                {@render queueList()}
              {/if}
            </div>
          </ScrollArea>
        </aside>

        {#if selected}
          <!-- Laid out by the pane's own width (a container): under 36rem the actions wrap
               rather than being cut off by the pane's overflow. -->
          <div class="@container flex min-h-0 min-w-0 flex-col overflow-hidden">
            <div
              class="border-separator flex items-start gap-3 border-b px-4 py-3 @min-[36rem]:px-6"
            >
              <div class="min-w-0 flex-1">
                <div class="flex flex-wrap items-center gap-x-2.5 gap-y-0.5">
                  <h2 class="text-[14px] font-semibold">AI flagged</h2>
                  <span
                    class="text-muted-foreground flex shrink-0 items-center gap-1.5 text-[12px] tabular-nums"
                  >
                    {@render verdictLine(selected, true)}
                  </span>
                </div>
                <div class="text-muted-foreground truncate text-[12px]">
                  {selected.title ?? selected.fileName}{selected.artist
                    ? ` — ${selected.artist}`
                    : ''}
                </div>
              </div>
            </div>

            <ScrollArea class="min-h-0 flex-1" data-mh-no-clearance="">
              <div class="space-y-4 px-4 py-4 @min-[36rem]:px-6">
                <!-- Verdict -->
                <div>
                  <div class="flex items-baseline justify-between gap-2">
                    <span class="text-foreground text-[13px] font-semibold"
                      >AI grader’s verdict</span
                    >
                    <span class="text-muted-foreground text-[11.5px]"
                      >Graded {formatDate(selected.gradedAtUtc)}</span
                    >
                  </div>
                  <div class="mt-2">
                    {#if selected.summary}
                      <p class="text-foreground text-[13px] leading-relaxed">{selected.summary}</p>
                    {:else}
                      <p class="text-muted-foreground text-[13px]">
                        No summary provided by the grader.
                      </p>
                    {/if}
                    {#if selected.issues.length > 0}
                      <div class="mt-3 flex flex-wrap gap-x-4 gap-y-1.5">
                        {#each selected.issues as issue (issue.code)}
                          {@const sev = SEVERITY[severityOf(issue.severity)]}
                          <span
                            class="text-muted-foreground flex items-center gap-1.5 text-[12px]"
                            title={[issue.code, issue.detail].filter(Boolean).join(' — ')}
                          >
                            <sev.icon class="{sev.text} size-3.5" aria-hidden="true" />
                            {issueLabel(issue.code)}
                            <span class="text-muted-foreground-dim">{sev.word.toLowerCase()}</span>
                          </span>
                        {/each}
                      </div>
                    {/if}
                  </div>
                </div>

                <!-- What the algorithm did — plain definition list, spacing not borders. -->
                <div class="border-separator border-t pt-4">
                  <div class="text-foreground text-[13px] font-semibold">
                    What the algorithm did
                  </div>
                  <dl class="mt-3 space-y-3">
                    {#each algorithmRows(selected) as row (row.l)}
                      <div>
                        <dt class="text-muted-foreground text-[11px]">{row.l}</dt>
                        <dd
                          class={cn(
                            'min-w-0 text-[13px] break-words',
                            row.mono && 'font-mono text-[11.5px]'
                          )}
                        >
                          {row.v}
                        </dd>
                      </div>
                    {/each}
                  </dl>
                </div>
              </div>
            </ScrollArea>

            <!-- Action bar — last item in a full-height column, so it carries the mini player's
                 clearance itself. -->
            <div
              class="border-separator bg-background flex flex-wrap items-center justify-end gap-2 border-t px-4 pt-3 pb-[calc(0.75rem_+_var(--mh-content-pad))] @min-[36rem]:px-6"
            >
              <Button
                variant="outline"
                onclick={() => onCopyDossier(selected.songId)}
                class="gap-1.5"
              >
                <Copy class="size-3.5" /> Copy dossier
              </Button>
              <Button variant="outline" href={timelineHref(selected.songId)} class="gap-1.5">
                <History class="size-3.5" /> View timeline
              </Button>
              {@render resolveAction(selected.songId, 'gap-1.5', 'size-3.5')}
            </div>
          </div>
        {/if}
      </div>
    {/if}
  </div>
{/if}

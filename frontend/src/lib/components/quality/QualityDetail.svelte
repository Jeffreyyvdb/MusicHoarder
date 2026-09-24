<script lang="ts">
  import {
    fetchEnrichmentDetail,
    fetchSongQualityGrade,
    gradeSong,
    copyQualitySongDossier,
    type EnrichmentDetail,
    type SongQualityGradeView,
    type QualitySongRow
  } from '$lib/api-client';
  import {
    candidatesFromDetail,
    beforeAfterRows,
    buildOriginMatrix,
    buildDestinationPath,
    type EditableFieldKey
  } from '$lib/review-helpers';
  import {
    verdictTone,
    toneText,
    toneGlyph,
    toneBorder,
    verdictGlyph,
    classifyBucket,
    issueLabel
  } from '$lib/quality-ui';
  import { goto } from '$app/navigation';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { SegmentedControl } from '$lib/components/ui/segmented-control';
  import CandidateGrid from '$lib/components/review/CandidateGrid.svelte';
  import BeforeAfterView from '$lib/components/review/BeforeAfterView.svelte';
  import OriginMatrixView from '$lib/components/review/OriginMatrixView.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import type { NavBack } from '$lib/nav';
  import { cleanDisplayName } from '$lib/formatters';
  import { coverUrlForSongId } from '$lib/components/v2/inbox/song-cover';
  import { cn } from '$lib/utils';
  import { Sparkles, Loader2, Copy, ExternalLink } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { SvelteSet } from 'svelte/reactivity';

  // One graded song: the algorithm's verdict against the AI's, the grader's reasoning, and the
  // provenance dossier. Two presentations:
  //  - `pane` (the desktop split view): a card column with its own scroll and an action bar.
  //  - `page` (the phone and tablet drill-down, pushed as /quality?detail=<id>): it renders the
  //    page's nav bar itself — so the bar is the first child of the page scroller — with Re-grade
  //    as the one prominent action and Copy dossier / Open in review in More; the content flows
  //    in the page's single scroller.
  type Props = {
    row: QualitySongRow | null;
    onRegraded?: () => void;
    layout?: 'pane' | 'page';
    /** `page`: where the nav bar's Back goes (the list). */
    back?: NavBack | null;
    /** `page`: the subtitle under the inline title, e.g. "3 of 57". */
    position?: string;
  };

  const { row, onRegraded, layout = 'pane', back, position }: Props = $props();

  let details = $state<Record<number, EnrichmentDetail>>({});
  let grades = $state<Record<number, SongQualityGradeView>>({});
  const loadingIds = new SvelteSet<number>();
  let regradeBusy = $state(false);
  let view = $state<'before' | 'matrix'>('before');

  // Lazily load the provenance dossier + latest grade for the selected song (cached per id).
  $effect(() => {
    const id = row?.songId;
    if (id == null || details[id] || loadingIds.has(id)) return;
    void loadDetail(id);
  });

  async function loadDetail(id: number) {
    loadingIds.add(id);
    try {
      const [d, g] = await Promise.allSettled([fetchEnrichmentDetail(id), fetchSongQualityGrade(id)]);
      if (d.status === 'fulfilled') details = { ...details, [id]: d.value };
      if (g.status === 'fulfilled') grades = { ...grades, [id]: g.value };
    } finally {
      loadingIds.delete(id);
    }
  }

  const detail = $derived(row ? (details[row.songId] ?? null) : null);
  const grade = $derived(row ? (grades[row.songId] ?? null) : null);
  const loading = $derived(row ? loadingIds.has(row.songId) && !details[row.songId] : false);

  // Prefer the freshly-fetched grade; fall back to the list row so the header paints instantly.
  const verdict = $derived(grade?.verdict ?? row?.verdict);
  const score = $derived(grade?.score ?? row?.score ?? null);
  const summary = $derived(grade?.summary ?? row?.summary ?? null);
  const issues = $derived(grade?.issues ?? row?.issues ?? []);
  const statusAtGrade = $derived(grade?.enrichmentStatusAtGrade ?? row?.enrichmentStatusAtGrade ?? null);
  const bucket = $derived(classifyBucket(statusAtGrade, verdict));

  const title = $derived(detail?.current.title ?? row?.title ?? detail?.fileName ?? row?.fileName ?? '');
  const artist = $derived(detail?.current.artist ?? row?.artist ?? '');
  const album = $derived(detail?.current.album ?? row?.album ?? '');

  // Dossier inputs (read-only audit view — reuses the /review components).
  const candidates = $derived(candidatesFromDetail(detail));
  const beforeRows = $derived(beforeAfterRows(detail));
  const matrix = $derived(buildOriginMatrix(detail));
  const finalValues = $derived<Record<string, string>>(
    detail
      ? {
          title: detail.current.title ?? '',
          artist: detail.current.artist ?? '',
          albumArtist: detail.current.albumArtist ?? '',
          album: detail.current.album ?? '',
          year: detail.current.year != null ? String(detail.current.year) : '',
          trackNumber: detail.current.trackNumber != null ? String(detail.current.trackNumber) : ''
        }
      : {}
  );
  const ext = $derived(
    ((detail?.fileName ?? row?.fileName ?? '').split('.').pop() ?? '').toUpperCase() || 'FLAC'
  );
  const destinationPath = $derived(
    detail?.destinationPath ??
      grade?.destinationPathPreview ??
      row?.destinationPathPreview ??
      (detail ? buildDestinationPath(finalValues, detail.fileName) : '')
  );
  const fromFolder = $derived(
    detail ? detail.sourcePath.slice(0, detail.sourcePath.lastIndexOf('/')) : ''
  );

  // Algorithm verdict (left card) is inferred from the enrichment status at grade time.
  const algoTone = $derived(bucket === 'flagged' ? 'amber' : 'green');
  const algoLabel = $derived(bucket === 'flagged' ? "Wasn't confident" : 'Auto-accepted');
  const algoGlyph = $derived(bucket === 'flagged' ? '⚠' : '✓');
  const algoSub = $derived(
    bucket === 'flagged'
      ? detail?.matchWarnings?.length
        ? detail.matchWarnings.join('; ')
        : 'flagged for review'
      : detail?.matchConfidence != null
        ? `confidence ${detail.matchConfidence.toFixed(2)} · single-pass`
        : 'no flag raised'
  );
  const aiTone = $derived(verdictTone(verdict));
  const marker = $derived(bucket === 'silent' ? 'disagree' : bucket === 'flagged' ? 'neutral' : 'agree');

  function fmtDate(iso: string | null | undefined): string {
    if (!iso) return '';
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? '' : d.toISOString().slice(0, 10);
  }
  // The eyebrow is a status label in sentence case and the system font (it used to be a mono
  // uppercase console tag); its tone is the bucket's status colour.
  const eyebrow = $derived(
    bucket === 'flagged'
      ? 'Flagged for review'
      : bucket === 'silent'
        ? 'Silent failure'
        : bucket === 'verified'
          ? `Verified${grade?.gradedAtUtc ? ' · ' + fmtDate(grade.gradedAtUtc) : ''}`
          : verdict
            ? 'Graded'
            : 'No AI grade yet'
  );
  const eyebrowTone = $derived(
    bucket === 'silent'
      ? 'text-destructive-text'
      : bucket === 'flagged'
        ? 'text-warning-text'
        : 'text-muted-foreground'
  );

  const reviewHref = $derived(row ? `/track/${row.songId}` : '#');
  const writeLabel = $derived(bucket === 'flagged' ? 'Will write to' : 'Current write');

  async function onRegrade() {
    if (!row) return;
    regradeBusy = true;
    try {
      const r = await gradeSong(row.songId);
      grades = { ...grades, [row.songId]: await fetchSongQualityGrade(row.songId) };
      toast.success(`Re-graded: ${r.verdict ?? r.outcome}${r.score != null ? ` (${r.score})` : ''}`);
      onRegraded?.();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Grading failed');
    } finally {
      regradeBusy = false;
    }
  }

  async function onCopy() {
    if (!row) return;
    try {
      await copyQualitySongDossier(row.songId);
      toast.success('Copied dossier to clipboard — paste into an AI assistant for a second opinion');
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Copy failed');
    }
  }

  const noop = (_k: EditableFieldKey, _v: string) => {};
  const views = [
    { value: 'before' as const, label: 'Before → After' },
    { value: 'matrix' as const, label: 'Origin matrix' }
  ];
</script>

{#snippet head(coverSize: number)}
  <div class="flex items-center gap-3.5">
    <Cover
      {artist}
      title={cleanDisplayName(title)}
      coverUrl={row ? coverUrlForSongId(row.songId) : null}
      size={coverSize}
      corner={6}
      caption={false}
    />
    <div class="min-w-0 flex-1">
      <div class={cn('text-footnote font-semibold md:text-[12px]', eyebrowTone)}>{eyebrow}</div>
      <div class="text-title-3 mt-0.5 line-clamp-2 md:truncate md:text-[17px] md:font-semibold">
        {cleanDisplayName(title)}
      </div>
      <div class="text-subheadline text-muted-foreground truncate md:text-[12px]">
        {artist || 'Unknown artist'}{#if album}<span aria-hidden="true" class="mx-1">·</span><span class="sr-only">, </span><em>{album}</em>{/if}
      </div>
    </div>
  </div>
{/snippet}

{#snippet verdictBlock()}
  {#if verdict}
    <div
      class={cn(
        'grid grid-cols-1 items-center gap-3 rounded-xl p-4 sm:grid-cols-[1fr_auto_1fr]',
        bucket === 'silent' ? 'bg-destructive/8 ring-destructive/30 ring-1 ring-inset' : 'bg-card'
      )}
    >
      <div>
        <div class="text-footnote text-muted-foreground font-medium md:text-[11px]">Algorithm</div>
        <div class={cn('text-headline mt-1 md:text-[15px]', toneText(algoTone))}>
          <span class={toneGlyph(algoTone)} aria-hidden="true">{algoGlyph}</span>
          {algoLabel}
        </div>
        <div class="text-subheadline text-muted-foreground mt-1 md:text-[12px]">{algoSub}</div>
      </div>
      <div class="grid place-items-center self-stretch sm:pt-3">
        {#if marker === 'disagree'}
          <span class="text-destructive-text text-footnote font-semibold md:text-[11px]">≠ Disagree</span>
        {:else if marker === 'agree'}
          <span class="text-muted-foreground text-footnote font-semibold md:text-[11px]">= Agree</span>
        {:else}
          <span class="text-muted-foreground text-lg" aria-hidden="true">→</span>
        {/if}
      </div>
      <div>
        <div class="text-footnote text-muted-foreground font-medium md:text-[11px]">AI grader</div>
        <div class={cn('text-headline mt-1 flex items-baseline gap-2 md:text-[15px]', toneText(aiTone))}>
          <span>
            <span class={toneGlyph(aiTone)} aria-hidden="true">{verdictGlyph(verdict)}</span>
            {verdict}
          </span>
          {#if score != null}<span class="text-muted-foreground text-subheadline font-normal tabular-nums md:text-[12px]"
              >{score}/100</span
            >{/if}
        </div>
        {#if summary}<div class="text-subheadline text-muted-foreground mt-1 md:text-[12px]">{summary}</div>{/if}
      </div>
    </div>
  {/if}
{/snippet}

{#snippet reasoning()}
  {#if verdict && (summary || issues.length > 0)}
    <div class={cn('bg-card rounded-xl border-l-[3px] p-4', toneBorder(aiTone))}>
      <div class="flex flex-wrap items-center gap-2">
        <Sparkles class={cn('size-3.5', toneGlyph(aiTone))} aria-hidden="true" />
        <span class="text-subheadline font-semibold md:text-[12px]">Why the AI graded it this way</span>
        <span class="text-footnote text-muted-foreground ml-auto md:text-[11px]">
          {#if grade?.model}<span class="font-mono">{grade.model}</span>{/if}{#if grade?.durationMs}
            · <span class="tabular-nums">{grade.durationMs} ms</span>{/if}{#if grade?.promptVersion}
            · prompt v{grade.promptVersion}{/if}
        </span>
      </div>
      {#if summary}<p class="text-callout text-muted-foreground mt-2 md:text-[12.5px]">{summary}</p>{/if}
      {#if issues.length > 0}
        <ul class="mt-2.5 flex flex-col gap-1.5">
          {#each issues as issue (issue.code)}
            <!-- The detail used to be a hover tooltip; a finger never hovers, so it is text. -->
            <li class="text-subheadline md:text-[12px]">
              <span
                class="bg-muted text-foreground rounded-full px-2 py-0.5 text-[12px] md:text-[11px]"
                title={issue.code}
                >{issueLabel(issue.code)}{#if issue.severity}<span class="text-muted-foreground">{` · ${issue.severity}`}</span>{/if}</span
              >
              {#if issue.detail}<span class="text-muted-foreground ml-1">{issue.detail}</span>{/if}
            </li>
          {/each}
        </ul>
      {/if}
    </div>
  {/if}
{/snippet}

{#snippet dossier()}
  {#if loading && !detail}
    <div class="text-muted-foreground text-subheadline flex items-center gap-2 py-8 md:text-[12.5px]">
      <Loader2 class="size-4 animate-spin" aria-hidden="true" /> Loading provenance dossier…
    </div>
  {:else if detail}
    <div class="space-y-3">
      <div class="flex flex-wrap items-center justify-between gap-2">
        <h3 class="text-footnote text-muted-foreground font-semibold md:text-[12px]">Provenance dossier</h3>
        <SegmentedControl items={views} bind:value={view} label="Dossier view" class="w-full sm:w-auto" />
      </div>

      <!-- The review components are wide tables; they scroll sideways inside the page rather than
           widening it. -->
      <div data-scroll-x="" class="min-w-0 space-y-3 overflow-x-auto">
        {#if candidates.length > 0}
          <CandidateGrid {candidates} pickedKey={null} onpick={() => {}} readonly />
        {/if}

        {#if view === 'before'}
          <BeforeAfterView
            rows={beforeRows}
            values={finalValues}
            readonly={true}
            {fromFolder}
            fileName={detail.fileName}
            fromMeta={ext}
            {destinationPath}
            destFormat={ext}
            onset={noop}
            oncopy={noop}
          />
        {:else}
          <OriginMatrixView {matrix} />
        {/if}
      </div>
    </div>
  {/if}
{/snippet}

{#if layout === 'page'}
  <PageToolbarV2
    title={row ? cleanDisplayName(title) || 'Track' : 'Track'}
    meta={position}
    largeTitle={false}
    {back}
    grouped
  >
    {#snippet actions()}
      {#if row}
        <Button onclick={onRegrade} disabled={regradeBusy} class="rounded-full">
          {#if regradeBusy}<Loader2 class="animate-spin" aria-hidden="true" />{:else}<Sparkles aria-hidden="true" />{/if}
          <span class="max-md:sr-only">Re-grade</span>
        </Button>
      {/if}
    {/snippet}
    {#snippet more()}
      {#if row}
        <DropdownMenu.Item onSelect={onCopy}>
          <Copy />
          Copy dossier
        </DropdownMenu.Item>
        <DropdownMenu.Item onSelect={() => void goto(reviewHref)}>
          <ExternalLink />
          Open in review
        </DropdownMenu.Item>
      {/if}
    {/snippet}
  </PageToolbarV2>

  {#if !row}
    <p class="text-body text-muted-foreground px-8 py-14 text-center">Loading track…</p>
  {:else}
    <div class="mx-auto flex w-full max-w-3xl flex-col gap-5 px-4 pt-3 pb-8 md:px-7">
      {@render head(64)}
      {@render verdictBlock()}
      {@render reasoning()}
      {@render dossier()}
      <div>
        <div class="text-footnote text-muted-foreground px-4 pb-1.5 md:px-0">{writeLabel}</div>
        <div class="bg-card text-footnote text-muted-foreground rounded-xl px-4 py-3 font-mono break-all">
          {destinationPath || '—'}
        </div>
      </div>
    </div>
  {/if}
{:else}
  <div class="bg-card flex h-full min-h-0 flex-col overflow-hidden rounded-xl">
    {#if !row}
      <div class="text-muted-foreground grid flex-1 place-items-center p-6 text-[13px]">Pick a track from the list.</div>
    {:else}
      <!-- Detail head -->
      <div class="border-separator shrink-0 border-b px-[18px] py-3.5">
        {@render head(52)}
      </div>

      <div class="bg-background-grouped min-h-0 flex-1 space-y-3.5 overflow-y-auto px-[18px] py-3.5">
        {@render verdictBlock()}
        {@render reasoning()}
        {@render dossier()}
      </div>

      <!-- Action bar — the app's own Button, 28px to look at with a 44px hit area on touch. -->
      <div class="border-separator bg-card flex shrink-0 flex-wrap items-center gap-3 border-t px-[18px] py-3">
        <div class="min-w-0 flex-1">
          <div class="text-muted-foreground text-[11px] font-medium">{writeLabel}</div>
          <div class="text-muted-foreground truncate font-mono text-[11px]" title={destinationPath}>{destinationPath || '—'}</div>
        </div>
        <Button variant="gray" size="sm" class="relative shrink-0 pointer-coarse:after:absolute pointer-coarse:after:-inset-x-1.5 pointer-coarse:after:-inset-y-2" onclick={onCopy}>
          <Copy aria-hidden="true" /> Copy dossier
        </Button>
        <Button variant="gray" size="sm" class="relative shrink-0 pointer-coarse:after:absolute pointer-coarse:after:-inset-x-1.5 pointer-coarse:after:-inset-y-2" href={reviewHref}>
          <ExternalLink aria-hidden="true" /> Open in review
        </Button>
        <Button size="sm" class="relative shrink-0 pointer-coarse:after:absolute pointer-coarse:after:-inset-x-1.5 pointer-coarse:after:-inset-y-2" disabled={regradeBusy} onclick={onRegrade}>
          {#if regradeBusy}<Loader2 class="animate-spin" aria-hidden="true" />{:else}<Sparkles aria-hidden="true" />{/if}
          Re-grade
        </Button>
      </div>
    {/if}
  </div>
{/if}

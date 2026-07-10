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
  import { verdictTone, toneText, toneBorder, verdictGlyph, classifyBucket } from '$lib/quality-ui';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import CandidateGrid from '$lib/components/review/CandidateGrid.svelte';
  import BeforeAfterView from '$lib/components/review/BeforeAfterView.svelte';
  import OriginMatrixView from '$lib/components/review/OriginMatrixView.svelte';
  import { cleanDisplayName } from '$lib/formatters';
  import { cn } from '$lib/utils';
  import { ChevronLeft, Sparkles, Loader2, Copy, ExternalLink } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { SvelteSet } from 'svelte/reactivity';

  type Props = {
    row: QualitySongRow | null;
    onBack?: () => void;
    onRegraded?: () => void;
  };

  const { row, onBack, onRegraded }: Props = $props();

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
  const eyebrow = $derived(
    bucket === 'flagged'
      ? 'FLAGGED FOR REVIEW'
      : bucket === 'silent'
        ? 'SILENT FAILURE'
        : bucket === 'verified'
          ? `VERIFIED${grade?.gradedAtUtc ? ' · ' + fmtDate(grade.gradedAtUtc) : ''}`
          : verdict
            ? 'GRADED'
            : 'NO LLM GRADE YET'
  );

  const reviewHref = $derived(row ? `/track/${row.songId}` : '#');

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
      toast.success('Copied to clipboard — paste into Claude Code');
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Copy failed');
    }
  }

  const noop = (_k: EditableFieldKey, _v: string) => {};
</script>

<div class="bg-card border-border flex h-full min-h-0 flex-col overflow-hidden rounded-lg border">
  {#if onBack}
    <button
      type="button"
      onclick={onBack}
      class="text-muted-foreground hover:text-foreground border-border flex shrink-0 items-center gap-1 border-b px-4 py-3 text-xs lg:hidden"
    >
      <ChevronLeft class="size-4" /> Back to list
    </button>
  {/if}

  {#if !row}
    <div class="text-muted-foreground grid flex-1 place-items-center p-6 text-[13px]">Pick a track from the list.</div>
  {:else}
    <!-- Detail head -->
    <div class="border-border flex shrink-0 items-center gap-3.5 border-b px-4 py-3.5 sm:px-[18px]">
      <Cover {artist} title={cleanDisplayName(title)} size={52} corner={4} caption={false} />
      <div class="min-w-0 flex-1">
        <div class="text-muted-foreground font-mono text-[9.5px] font-bold tracking-[0.12em] uppercase">{eyebrow}</div>
        <div class="mt-0.5 truncate text-[17px] font-semibold tracking-tight">{cleanDisplayName(title)}</div>
        <div class="text-muted-foreground truncate text-[12px]">
          {artist || 'Unknown artist'}{#if album}<span class="text-muted-foreground/60"> · </span><em>{album}</em>{/if}
        </div>
      </div>
    </div>

    <div class="min-h-0 flex-1 space-y-3.5 overflow-y-auto px-4 py-3.5 sm:px-[18px]">
      <!-- Verdict conflict -->
      {#if verdict}
        <div
          class={cn(
            'grid grid-cols-1 items-center gap-2 rounded-md border p-3.5 sm:grid-cols-[1fr_auto_1fr] sm:gap-3',
            bucket === 'silent'
              ? 'border-red-500/40 bg-gradient-to-b from-red-500/[0.08] to-transparent'
              : 'border-border bg-background'
          )}
        >
          <div>
            <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.1em]">ALGORITHM SAID</div>
            <div class={cn('mt-1 text-[15px] font-semibold', toneText(algoTone))}>{algoGlyph} {algoLabel}</div>
            <div class="text-muted-foreground mt-1 text-[12px] leading-snug">{algoSub}</div>
          </div>
          <div class="grid place-items-center self-stretch pt-1 sm:pt-3">
            {#if marker === 'disagree'}
              <span class="font-mono text-[11px] font-semibold tracking-wide text-red-600 dark:text-red-400">≠ disagree</span>
            {:else if marker === 'agree'}
              <span class="font-mono text-[11px] font-semibold tracking-wide text-emerald-600 dark:text-emerald-400">= agree</span>
            {:else}
              <span class="text-muted-foreground text-lg">→</span>
            {/if}
          </div>
          <div>
            <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.1em]">AI SAID</div>
            <div class={cn('mt-1 flex items-baseline gap-2 text-[15px] font-semibold', toneText(aiTone))}>
              <span>{verdictGlyph(verdict)} {verdict}</span>
              {#if score != null}<span class="text-muted-foreground font-mono text-[12px]">{score}/100</span>{/if}
            </div>
            {#if summary}<div class="text-muted-foreground mt-1 text-[12px] leading-snug">{summary}</div>{/if}
          </div>
        </div>
      {/if}

      <!-- LLM reasoning -->
      {#if verdict && (summary || issues.length > 0)}
        <div class={cn('bg-background rounded-md border border-l-[3px] p-3.5', toneBorder(aiTone))}>
          <div class="flex flex-wrap items-center gap-2">
            <Sparkles class={cn('size-3', toneText(aiTone))} />
            <span class="text-[12px] font-semibold">Why the AI graded it this way</span>
            <span class="text-muted-foreground ml-auto font-mono text-[10px]">
              {#if grade?.model}{grade.model}{/if}{#if grade?.durationMs} · {grade.durationMs}ms{/if}{#if grade?.promptVersion} · prompt v{grade.promptVersion}{/if}
            </span>
          </div>
          {#if summary}<div class="text-muted-foreground mt-2 text-[12.5px] leading-relaxed">{summary}</div>{/if}
          {#if issues.length > 0}
            <div class="mt-2.5 flex flex-wrap gap-1.5">
              {#each issues as issue (issue.code)}
                <span
                  class="bg-muted/60 inline-flex items-center gap-1 rounded px-1.5 py-0.5 font-mono text-[10px]"
                  title={issue.detail ?? undefined}
                >
                  {issue.code}{#if issue.severity}<span class="text-muted-foreground/70">· {issue.severity}</span>{/if}
                </span>
              {/each}
            </div>
          {/if}
        </div>
      {/if}

      <!-- Provenance dossier -->
      {#if loading && !detail}
        <div class="text-muted-foreground flex items-center gap-2 py-8 text-[12.5px]">
          <Loader2 class="size-4 animate-spin" /> Loading provenance dossier…
        </div>
      {:else if detail}
        <div class="space-y-3">
          <div class="border-border flex items-center justify-between border-b pb-2">
            <span class="text-muted-foreground font-mono text-[9.5px] font-bold tracking-[0.12em]">PROVENANCE DOSSIER</span>
            <div class="bg-surface-sunken flex items-center gap-1 rounded-lg p-0.5">
              {#each [{ id: 'before' as const, label: 'Before → After' }, { id: 'matrix' as const, label: 'Origin matrix' }] as v (v.id)}
                <button
                  type="button"
                  onclick={() => (view = v.id)}
                  class={cn(
                    'rounded-md px-3 py-1.5 text-[12px] font-medium transition-colors active:translate-y-px',
                    view === v.id ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-accent'
                  )}>{v.label}</button
                >
              {/each}
            </div>
          </div>

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
      {/if}
    </div>

    <!-- Action bar -->
    <div class="border-border bg-card flex shrink-0 flex-wrap items-center gap-3 border-t px-4 py-3 sm:px-[18px]">
      <div class="min-w-0 flex-1">
        <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.1em]">
          {bucket === 'flagged' ? 'WILL WRITE TO' : 'CURRENT WRITE'}
        </div>
        <div class="text-muted-foreground truncate font-mono text-[11px]" title={destinationPath}>{destinationPath || '—'}</div>
      </div>
      <button
        type="button"
        onclick={onCopy}
        class="border-border hover:bg-accent inline-flex shrink-0 items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12px] transition-colors active:translate-y-px"
      >
        <Copy class="size-3.5" /> Copy dossier
      </button>
      <a
        href={reviewHref}
        class="border-border hover:bg-accent inline-flex shrink-0 items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12px] transition-colors active:translate-y-px"
      >
        <ExternalLink class="size-3.5" /> Open in review
      </a>
      <button
        type="button"
        disabled={regradeBusy}
        onclick={onRegrade}
        class="bg-primary text-primary-foreground inline-flex shrink-0 items-center gap-1.5 rounded-md px-3 py-1.5 text-[12px] font-medium transition-opacity hover:opacity-90 active:not-disabled:translate-y-px disabled:opacity-50"
      >
        {#if regradeBusy}<Loader2 class="size-3.5 animate-spin" />{:else}<Sparkles class="size-3.5" />{/if}
        Re-grade
      </button>
    </div>
  {/if}
</div>

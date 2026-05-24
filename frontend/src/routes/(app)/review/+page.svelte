<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Label } from '$lib/components/ui/label';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import {
    Check,
    X,
    ChevronRight,
    Play,
    Pause,
    RefreshCw,
    Loader2,
    CheckCheck,
    Trash2,
    AlertTriangle,
    Sparkles,
    Copy
  } from '@lucide/svelte';
  import type { ApiSong, EnrichmentDetail, SongQualityGradeView, QualityVerdict } from '$lib/api-client';
  import {
    fetchReviewQueue,
    fetchEnrichmentDetail,
    submitManualReview,
    softDeleteSong,
    bulkApprove,
    enrichSong,
    toPlayerSong,
    fetchSongQualityGrade,
    gradeSong,
    copyQualitySongDossier
  } from '$lib/api-client';
  import { toast } from 'svelte-sonner';
  import {
    reasonFor,
    candidatesFromDetail,
    buildDestinationPath,
    bestGuess,
    bannerFor,
    decisionLabel,
    elapsedMs,
    formatElapsed,
    contributedProviders,
    beforeAfterRows,
    buildTimeline,
    buildOriginMatrix,
    originalInfo,
    EDITABLE_FIELDS,
    type ReviewCandidate,
    type EditableFieldKey
  } from '$lib/review-helpers';
  import { formatFileSize } from '$lib/formatters';
  import { playerStore } from '$lib/stores/player.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import MobileReview from '$lib/components/mobile/MobileReview.svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import CandidateGrid from '$lib/components/review/CandidateGrid.svelte';
  import BeforeAfterView from '$lib/components/review/BeforeAfterView.svelte';
  import TimelineView from '$lib/components/review/TimelineView.svelte';
  import OriginMatrixView from '$lib/components/review/OriginMatrixView.svelte';
  import { cn } from '$lib/utils';

  // Below this width the fixed 340px queue + detail two-pane is too cramped;
  // fall back to the single-column mobile review layout.
  const isMobile = new IsMobile(1100);

  type MetadataEdits = Partial<Record<EditableFieldKey, string>>;
  type Decision = 'accept' | 'reject' | 'skip';
  type QueueFilter = 'needsreview' | 'done' | 'all';
  type ViewKey = 'before' | 'timeline' | 'matrix';

  let reviewTracks = $state<ApiSong[]>([]);
  let doneTracks = $state<ApiSong[]>([]);
  let queueFilter = $state<QueueFilter>('needsreview');
  let view = $state<ViewKey>('before');

  let selectedId = $state<number | null>(null);
  let editedMetadata = $state<Record<number, MetadataEdits>>({});
  let pickedKey = $state<Record<number, string>>({});
  let decisions = $state<Record<number, Decision>>({});
  let details = $state<Record<number, EnrichmentDetail>>({});
  let detailLoading = $state<Record<number, boolean>>({});
  let songGrades = $state<Record<number, SongQualityGradeView>>({});
  let gradeBusy = $state(false);

  let loading = $state(true);
  let actionLoading = $state(false);
  let error = $state<string | null>(null);
  let bulkApproveMinConfidence = $state(0.75);
  let bulkApproveResult = $state<{ count: number } | null>(null);

  const PILL_TINT: Record<'warn' | 'info' | 'err', string> = {
    warn: 'bg-amber-500/15 text-amber-600 dark:text-amber-400',
    info: 'bg-primary/15 text-primary',
    err: 'bg-red-500/15 text-red-600 dark:text-red-400'
  };

  const tracks = $derived(
    queueFilter === 'needsreview'
      ? reviewTracks
      : queueFilter === 'done'
        ? doneTracks
        : [...reviewTracks, ...doneTracks]
  );
  const totalCount = $derived(reviewTracks.length + doneTracks.length);

  async function loadQueues() {
    try {
      loading = true;
      error = null;
      const [review, done] = await Promise.all([
        fetchReviewQueue('needsreview'),
        fetchReviewQueue('matched')
      ]);
      reviewTracks = review;
      // Done can be the bulk of the library — show the most-recent slice.
      doneTracks = [...done]
        .sort(
          (a, b) =>
            new Date(b.libraryBuiltAtUtc ?? b.indexedAtUtc ?? 0).getTime() -
            new Date(a.libraryBuiltAtUtc ?? a.indexedAtUtc ?? 0).getTime()
        )
        .slice(0, 100);
      editedMetadata = {};
      pickedKey = {};
      decisions = {};
      details = {};
      detailLoading = {};
      selectedId = tracks[0]?.id ?? null;
      // Prefetch detail for the review queue so each row shows its guess + provenance.
      for (const track of reviewTracks) void loadDetail(track.id);
      if (selectedId != null) void loadDetail(selectedId);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load tracks';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    if (isMobile.current) return;
    void loadQueues();
  });

  // Keep a valid selection as the filter changes.
  $effect(() => {
    const list = tracks;
    if (list.length === 0) {
      selectedId = null;
    } else if (selectedId == null || !list.some((t) => t.id === selectedId)) {
      selectedId = list[0].id;
    }
  });

  const selectedTrack = $derived(tracks.find((t) => t.id === selectedId) ?? null);
  const selectedDetail = $derived(selectedTrack ? (details[selectedTrack.id] ?? null) : null);
  const selectedGrade = $derived(selectedTrack ? (songGrades[selectedTrack.id] ?? null) : null);

  // Lazily load the AI quality grade for whichever track is selected.
  $effect(() => {
    const id = selectedId;
    if (id == null || songGrades[id]) return;
    void (async () => {
      try {
        songGrades = { ...songGrades, [id]: await fetchSongQualityGrade(id) };
      } catch {
        // grade is optional UI; ignore load failures
      }
    })();
  });

  function verdictTint(v: QualityVerdict | undefined): string {
    switch (v) {
      case 'Excellent':
        return 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400 border-emerald-500/30';
      case 'Good':
        return 'bg-teal-500/15 text-teal-600 dark:text-teal-400 border-teal-500/30';
      case 'Questionable':
        return 'bg-amber-500/15 text-amber-600 dark:text-amber-400 border-amber-500/30';
      case 'Wrong':
        return 'bg-red-500/15 text-red-600 dark:text-red-400 border-red-500/30';
      default:
        return 'bg-muted text-muted-foreground border-border';
    }
  }

  async function onGradeNow() {
    const id = selectedId;
    if (id == null) return;
    gradeBusy = true;
    try {
      const r = await gradeSong(id);
      songGrades = { ...songGrades, [id]: await fetchSongQualityGrade(id) };
      toast.success(`Graded: ${r.verdict ?? r.outcome}${r.score != null ? ` (${r.score})` : ''}`);
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Grading failed');
    } finally {
      gradeBusy = false;
    }
  }

  async function onCopyDossier() {
    const id = selectedId;
    if (id == null) return;
    try {
      await copyQualitySongDossier(id);
      toast.success('Copied to clipboard — paste into Claude Code');
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Copy failed');
    }
  }
  const candidates = $derived(candidatesFromDetail(selectedDetail));
  const selectedIsReview = $derived(
    (selectedDetail?.enrichmentStatus ?? String(selectedTrack?.enrichmentStatus ?? '')).toLowerCase() ===
      'needsreview'
  );

  function seedFields(track: ApiSong, top: ReviewCandidate | undefined, detail: EnrichmentDetail | null): MetadataEdits {
    const cur = detail?.current;
    return {
      title: top?.title ?? cur?.title ?? track.title ?? '',
      artist: top?.artist ?? cur?.artist ?? track.artist ?? '',
      album: top?.album ?? cur?.album ?? track.album ?? '',
      albumArtist: cur?.albumArtist ?? track.albumArtist ?? '',
      year: top?.year || (cur?.year != null ? String(cur.year) : (track.year != null ? String(track.year) : '')),
      trackNumber: cur?.trackNumber != null ? String(cur.trackNumber) : track.trackNumber != null ? String(track.trackNumber) : ''
    };
  }

  async function loadDetail(id: number) {
    if (details[id] || detailLoading[id]) return;
    detailLoading = { ...detailLoading, [id]: true };
    try {
      const detail = await fetchEnrichmentDetail(id);
      details = { ...details, [id]: detail };
      if (!editedMetadata[id]) {
        const cands = candidatesFromDetail(detail);
        const track = tracks.find((t) => t.id === id);
        if (track) editedMetadata = { ...editedMetadata, [id]: seedFields(track, cands[0], detail) };
        if (cands[0]) pickedKey = { ...pickedKey, [id]: cands[0].key };
      }
    } catch {
      // detail is optional — the form still works from embedded tags
    } finally {
      detailLoading = { ...detailLoading, [id]: false };
    }
  }

  $effect(() => {
    if (selectedId != null) void loadDetail(selectedId);
  });

  function selectTrack(id: number) {
    selectedId = id;
  }

  function pickCandidate(c: ReviewCandidate) {
    const id = selectedTrack?.id;
    if (id == null) return;
    pickedKey = { ...pickedKey, [id]: c.key };
    editedMetadata = {
      ...editedMetadata,
      [id]: {
        ...editedMetadata[id],
        title: c.fields.title,
        artist: c.fields.artist,
        album: c.fields.album,
        year: c.fields.year
      }
    };
  }

  function setField(key: EditableFieldKey, value: string) {
    const id = selectedTrack?.id;
    if (id == null) return;
    editedMetadata = { ...editedMetadata, [id]: { ...editedMetadata[id], [key]: value } };
  }

  function copyEmbedded(key: EditableFieldKey, embedded: string) {
    setField(key, embedded);
  }

  const finalValues = $derived<Record<string, string>>(
    selectedTrack ? { ...(editedMetadata[selectedTrack.id] ?? {}) } : {}
  );

  const decidedCount = $derived(Object.values(decisions).filter(Boolean).length);

  function advanceToNextUndecided(fromId: number) {
    const next = tracks.find((t) => t.id !== fromId && !decisions[t.id]);
    if (next) selectedId = next.id;
  }

  function buildOverrides(id: number) {
    const edits = editedMetadata[id] ?? {};
    const out: Record<string, string | number> = {};
    for (const f of EDITABLE_FIELDS) {
      const v = edits[f.key];
      if (v == null || v === '') continue;
      if (f.key === 'year' || f.key === 'trackNumber') {
        const n = parseInt(v, 10);
        if (Number.isFinite(n)) out[f.key] = n;
      } else {
        out[f.key] = v;
      }
    }
    return out;
  }

  async function handleAccept() {
    const track = selectedTrack;
    if (!track || actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      await submitManualReview(track.id, { decision: 'approve', ...buildOverrides(track.id) });
      decisions = { ...decisions, [track.id]: 'accept' };
      reviewTracks = reviewTracks.filter((t) => t.id !== track.id);
      advanceToNextUndecided(track.id);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to accept track';
    } finally {
      actionLoading = false;
    }
  }

  async function handleReject() {
    const track = selectedTrack;
    if (!track || actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      await submitManualReview(track.id, { decision: 'reject' });
      decisions = { ...decisions, [track.id]: 'reject' };
      advanceToNextUndecided(track.id);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to reject track';
    } finally {
      actionLoading = false;
    }
  }

  function handleSkip() {
    const track = selectedTrack;
    if (!track) return;
    decisions = { ...decisions, [track.id]: 'skip' };
    advanceToNextUndecided(track.id);
  }

  async function handleReenrich() {
    const track = selectedTrack;
    if (!track || actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      await enrichSong(track.id, true);
      await loadQueues();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to re-enrich track';
    } finally {
      actionLoading = false;
    }
  }

  async function handleDelete() {
    const track = selectedTrack;
    if (!track || actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      await softDeleteSong(track.id);
      const removedId = track.id;
      reviewTracks = reviewTracks.filter((t) => t.id !== removedId);
      doneTracks = doneTracks.filter((t) => t.id !== removedId);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to delete track';
    } finally {
      actionLoading = false;
    }
  }

  async function handleBulkApprove() {
    if (actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      bulkApproveResult = null;
      const result = await bulkApprove(bulkApproveMinConfidence);
      bulkApproveResult = { count: result.approvedCount };
      if (result.approvedCount > 0) await loadQueues();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to bulk approve';
    } finally {
      actionLoading = false;
    }
  }

  const eligibleForBulk = $derived(
    reviewTracks.filter((t) => t.matchConfidence != null && t.matchConfidence >= bulkApproveMinConfidence)
      .length
  );

  const isPlayingSelected = $derived(
    !!selectedTrack && playerStore.currentSong?.id === selectedTrack.id && playerStore.isPlaying
  );

  function handlePlayPause() {
    const track = selectedTrack;
    if (!track) return;
    if (playerStore.currentSong?.id === track.id) {
      playerStore.togglePlay();
      return;
    }
    const queue = tracks.map((t) => toPlayerSong(t, 'Unknown Artist'));
    const index = tracks.findIndex((t) => t.id === track.id);
    void playerStore.playSong(toPlayerSong(track, 'Unknown Artist'), queue, index);
  }

  function fullStamp(track: ApiSong | null, detail: EnrichmentDetail | null): string {
    const iso = detail?.providerAttempts?.[0]?.attemptedAtUtc ?? track?.indexedAtUtc;
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return (
      d.toISOString().slice(0, 10) +
      ' ' +
      d.toLocaleTimeString([], { hour12: false }) +
      '.' +
      String(d.getMilliseconds()).padStart(3, '0')
    );
  }

  function clock(iso: string | null | undefined): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
  }

  // Header / KPI derivations for the selected track.
  const guess = $derived(selectedTrack ? bestGuess(selectedTrack, candidates, selectedDetail) : null);
  const original = $derived(selectedTrack ? originalInfo(selectedTrack, selectedDetail) : null);
  const banner = $derived(selectedTrack ? bannerFor(selectedTrack, selectedDetail) : null);
  const kpiElapsed = $derived(selectedTrack ? formatElapsed(elapsedMs(selectedTrack, selectedDetail)) : '—');
  const contributed = $derived(contributedProviders(selectedDetail));
  const beforeRows = $derived(beforeAfterRows(selectedDetail));
  const timeline = $derived(selectedTrack ? buildTimeline(selectedTrack, selectedDetail) : []);
  const matrix = $derived(buildOriginMatrix(selectedDetail));
  const flaggedAt = $derived(clock(selectedDetail?.providerAttempts?.[0]?.attemptedAtUtc));

  function fmtFromMeta(track: ApiSong): string {
    const parts: string[] = [];
    const fmt = (track.extension ?? '').replace(/^\./, '').toUpperCase();
    if (fmt) parts.push(track.bitRate ? `${fmt} ${track.bitRate}kbps` : fmt);
    parts.push(formatFileSize(track.fileSizeBytes));
    if (track.indexedAtUtc) parts.push(`indexed ${track.indexedAtUtc.slice(0, 10)}`);
    return parts.join(' · ');
  }
  function destFormat(track: ApiSong): string {
    const fmt = (track.extension ?? '').replace(/^\./, '').toUpperCase() || 'FLAC';
    return track.bitRate ? `${fmt} ${track.bitRate}kbps` : fmt;
  }

  const destinationPath = $derived(
    selectedTrack ? buildDestinationPath(finalValues, selectedTrack.extension) : ''
  );
  const fromFolder = $derived(
    selectedTrack ? selectedTrack.sourcePath.slice(0, selectedTrack.sourcePath.lastIndexOf('/')) : ''
  );

  // Original metadata for the queue row (uses prefetched detail when present).
  function rowOriginal(track: ApiSong): { title: string; subtitle: string; titleFromFilename: boolean } {
    const o = originalInfo(track, details[track.id]);
    // Surface the filename as the subtitle only when it isn't already the (fallback) title.
    const subtitle = o.subtitle || (o.titleFromFilename ? '' : o.fileName);
    return { title: o.title, subtitle, titleFromFilename: o.titleFromFilename };
  }

  // The enriched best-guess title shown as a secondary hint when it differs from the original.
  function rowGuessTitle(track: ApiSong): string {
    const d = details[track.id];
    if (!d) return '';
    return bestGuess(track, candidatesFromDetail(d), d).title;
  }

  const FILTERS: { key: QueueFilter; label: string }[] = [
    { key: 'needsreview', label: 'Needs review' },
    { key: 'done', label: 'Done' },
    { key: 'all', label: 'All' }
  ];
  const VIEWS: { key: ViewKey; label: string }[] = [
    { key: 'before', label: 'Before → After' },
    { key: 'timeline', label: 'Timeline' },
    { key: 'matrix', label: 'Origin matrix' }
  ];
</script>

{#if isMobile.current}
  <div class="mx-auto h-full w-full max-w-2xl">
    <MobileReview />
  </div>
{:else if loading}
  <main class="flex flex-1 items-center justify-center p-4">
    <div class="flex flex-col items-center gap-4">
      <Loader2 class="text-primary size-8 animate-spin" />
      <p class="text-muted-foreground">Loading enrichments…</p>
    </div>
  </main>
{:else if error && totalCount === 0}
  <main class="flex flex-1 items-center justify-center p-4">
    <div class="max-w-md text-center">
      <div class="bg-destructive/10 mx-auto mb-4 flex size-16 items-center justify-center rounded-full">
        <X class="text-destructive size-8" />
      </div>
      <h2 class="mb-2 text-xl font-semibold">Error</h2>
      <p class="text-muted-foreground mb-4">{error}</p>
      <Button onclick={loadQueues}>Retry</Button>
    </div>
  </main>
{:else}
  <main class="grid min-h-0 flex-1 grid-cols-[340px_1fr] overflow-hidden">
    <!-- Queue -->
    <aside class="bg-surface-sunken border-border flex min-h-0 flex-col border-r">
      <div class="border-border flex items-start justify-between gap-2 border-b px-[18px] py-3">
        <div class="min-w-0">
          <div class="text-sm font-semibold">Enrichments</div>
          <div class="text-muted-foreground mt-0.5 text-[11.5px]">
            {totalCount} total · <span class="font-mono">{decidedCount} decided</span>
          </div>
        </div>
        <div class="flex items-center gap-1">
          <AlertDialog.Root>
            <AlertDialog.Trigger>
              {#snippet child({ props })}
                <Button {...props} variant="ghost" size="icon" class="size-8" title="Bulk approve">
                  <CheckCheck class="size-4" />
                </Button>
              {/snippet}
            </AlertDialog.Trigger>
            <AlertDialog.Content>
              <AlertDialog.Header>
                <AlertDialog.Title>Bulk approve tracks</AlertDialog.Title>
                <AlertDialog.Description>
                  Approve all tracks with match confidence at or above the threshold.
                  {#if eligibleForBulk > 0}
                    &nbsp;{eligibleForBulk} track{eligibleForBulk !== 1 ? 's' : ''} eligible.
                  {:else}
                    &nbsp;No tracks are currently eligible at this threshold.
                  {/if}
                </AlertDialog.Description>
              </AlertDialog.Header>
              <div class="py-2">
                <Label for="minConfidence" class="text-sm">Minimum confidence</Label>
                <Input
                  id="minConfidence"
                  type="number"
                  min={0}
                  max={1}
                  step={0.05}
                  value={bulkApproveMinConfidence}
                  oninput={(e) => {
                    const v = parseFloat((e.target as HTMLInputElement).value);
                    bulkApproveMinConfidence = Number.isFinite(v) ? v : 0.75;
                  }}
                  class="mt-1"
                />
              </div>
              {#if bulkApproveResult}
                <p class="text-muted-foreground text-sm">
                  Approved {bulkApproveResult.count} track{bulkApproveResult.count !== 1 ? 's' : ''}.
                </p>
              {/if}
              <AlertDialog.Footer>
                <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                <AlertDialog.Action onclick={handleBulkApprove} disabled={actionLoading}>
                  {#if actionLoading}<Loader2 class="mr-2 size-4 animate-spin" />{/if}
                  Approve {eligibleForBulk} track{eligibleForBulk !== 1 ? 's' : ''}
                </AlertDialog.Action>
              </AlertDialog.Footer>
            </AlertDialog.Content>
          </AlertDialog.Root>
          <Button variant="ghost" size="icon" class="size-8" onclick={loadQueues} title="Refresh">
            <RefreshCw class="size-4" />
          </Button>
        </div>
      </div>

      <!-- Segmented filter -->
      <div class="border-border bg-surface-sunken flex items-center gap-1 border-b px-[14px] py-2">
        {#each FILTERS as f (f.key)}
          {@const count = f.key === 'needsreview' ? reviewTracks.length : f.key === 'done' ? doneTracks.length : totalCount}
          <button
            type="button"
            onclick={() => (queueFilter = f.key)}
            class={cn(
              'flex items-center gap-1.5 rounded-md px-2.5 py-1.5 text-[12px] font-medium transition-colors',
              queueFilter === f.key ? 'bg-primary/15 text-primary' : 'text-muted-foreground hover:bg-accent'
            )}
          >
            {#if f.key === 'needsreview'}<AlertTriangle class="size-3.5" />{/if}
            {#if f.key === 'done'}<Check class="size-3.5" />{/if}
            <span>{f.label}</span>
            <span
              class={cn(
                'rounded-full px-1.5 text-[10px] font-semibold tabular-nums',
                queueFilter === f.key ? 'bg-primary text-primary-foreground' : 'bg-muted text-muted-foreground'
              )}>{count}</span
            >
          </button>
        {/each}
      </div>

      <div class="min-h-0 flex-1 overflow-y-auto p-1.5">
        {#if tracks.length === 0}
          <div class="text-muted-foreground px-4 py-12 text-center text-sm">
            {queueFilter === 'needsreview' ? 'Nothing needs review.' : 'No items here yet.'}
          </div>
        {/if}
        {#each tracks as track (track.id)}
          {@const r = reasonFor(track)}
          {@const d = decisions[track.id]}
          {@const info = rowOriginal(track)}
          {@const guessTitle = rowGuessTitle(track)}
          {@const detail = details[track.id]}
          {@const provs = contributedProviders(detail)}
          {@const ms = detail ? formatElapsed(elapsedMs(track, detail)) : null}
          <button
            class={cn(
              'mb-0.5 flex w-full items-center gap-2.5 rounded-md border-l-2 border-transparent py-2.5 pr-2.5 pl-2 text-left transition-colors',
              selectedId === track.id ? 'border-l-primary bg-card' : 'hover:bg-accent',
              d === 'accept' && 'opacity-60',
              d === 'reject' && 'opacity-45',
              d === 'skip' && 'opacity-60'
            )}
            onclick={() => selectTrack(track.id)}
          >
            <Cover artist={track.artist ?? 'Unknown'} title={info.title} size={42} corner={6} caption={false} />
            <div class="min-w-0 flex-1">
              <div class={cn('truncate text-[13px] font-medium', info.titleFromFilename && 'font-mono text-[12px]')}>{info.title}</div>
              <div class="text-muted-foreground truncate text-[11.5px]">{info.subtitle || '—'}</div>
              {#if guessTitle && guessTitle !== info.title}
                <div class="text-muted-foreground/70 truncate text-[11px]">→ {guessTitle}</div>
              {/if}
              <div class="mt-1 flex flex-wrap items-center gap-1.5">
                <span
                  class={cn('rounded px-1.5 py-0.5 text-[9px] font-bold tracking-wide uppercase', PILL_TINT[r.tint])}
                  >{r.label}</span
                >
                {#each provs.slice(0, 3) as p (p.label)}
                  <span class="size-1.5 rounded-full" style="background: {p.color}" title={p.label}></span>
                {/each}
              </div>
            </div>
            <div class="flex shrink-0 flex-col items-end gap-1">
              {#if ms}<span class="text-muted-foreground font-mono text-[10.5px]">{ms}</span>{/if}
              {#if d}
                <span
                  class={cn(
                    'grid size-[18px] place-items-center rounded-full text-white',
                    d === 'accept' && 'bg-primary',
                    d === 'reject' && 'bg-[#c23a3a]',
                    d === 'skip' && 'bg-muted-foreground'
                  )}
                >
                  {#if d === 'accept'}<Check size={10} strokeWidth={2.5} />{/if}
                  {#if d === 'reject'}<X size={10} strokeWidth={2.5} />{/if}
                  {#if d === 'skip'}<ChevronRight size={10} strokeWidth={2} />{/if}
                </span>
              {/if}
            </div>
          </button>
        {/each}
      </div>
    </aside>

    <!-- Detail -->
    {#if selectedTrack && guess && banner}
      <div class="flex min-w-0 flex-col overflow-hidden">
        <!-- Banner -->
        <div
          class={cn(
            'flex items-center gap-3 px-7 py-3.5',
            banner.tone === 'warn' && 'bg-amber-500/10',
            banner.tone === 'info' && 'bg-primary/10',
            banner.tone === 'err' && 'bg-red-500/10',
            banner.tone === 'ok' && 'bg-primary/10'
          )}
        >
          <span
            class={cn(
              'grid size-9 shrink-0 place-items-center rounded-full text-white',
              banner.tone === 'warn' && 'bg-amber-500',
              banner.tone === 'info' && 'bg-primary',
              banner.tone === 'err' && 'bg-red-500',
              banner.tone === 'ok' && 'bg-primary'
            )}
          >
            {#if banner.tone === 'ok'}<Check class="size-4" strokeWidth={2.5} />{:else}<AlertTriangle class="size-4" strokeWidth={2.5} />{/if}
          </span>
          <div class="min-w-0 flex-1">
            <div class="text-[15px] font-semibold">{banner.title}</div>
            <div class="text-muted-foreground text-[12.5px]">{banner.body}</div>
          </div>
          {#if flaggedAt}
            <span class="text-muted-foreground shrink-0 font-mono text-[11px]">flagged {flaggedAt}</span>
          {/if}
        </div>

        <!-- Header -->
        <div class="border-border flex items-start justify-between gap-6 border-b px-7 pt-3 pb-4">
          <div class="flex min-w-0 items-start gap-4">
            <Cover artist={original?.subtitle ?? 'Unknown'} title={original?.title ?? ''} size={56} corner={8} caption={false} />
            <div class="min-w-0">
              <div class="text-muted-foreground font-mono text-[10px] tracking-[0.1em]">
                ORIGINAL · {fullStamp(selectedTrack, selectedDetail)}
              </div>
              <h1 class={cn('my-0.5 truncate text-[24px] font-semibold tracking-tight', original?.titleFromFilename && 'font-mono text-[18px]')}>
                {original?.title}
              </h1>
              {#if original?.subtitle}
                <div class="text-muted-foreground truncate text-[13px]">{original.subtitle}</div>
              {/if}
              <div class="text-muted-foreground/70 truncate font-mono text-[11px]">{original?.fileName}</div>
              {#if guess.title && guess.title !== original?.title}
                <div class="text-muted-foreground/80 mt-1.5 truncate text-[12px]">
                  <span class="font-mono text-[10px] tracking-[0.08em]">BEST GUESS →</span>
                  {guess.title}{guess.subtitle ? ' · ' + guess.subtitle : ''}{#if guess.isGuess}<span class="font-normal"> (best guess)</span>{/if}
                </div>
              {/if}
            </div>
          </div>
          <div class="grid shrink-0 grid-cols-4 gap-5">
            <div class="text-right">
              <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.08em]">ELAPSED</div>
              <div class="mt-0.5 font-mono text-[15px] font-semibold">{kpiElapsed}</div>
            </div>
            <div class="text-right">
              <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.08em]">PROVIDERS</div>
              <div class="mt-0.5 font-mono text-[15px] font-semibold">{selectedDetail?.providerAttempts.length ?? 0}</div>
            </div>
            <div class="text-right">
              <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.08em]">CANDIDATES</div>
              <div class="mt-0.5 font-mono text-[15px] font-semibold">{candidates.length}</div>
            </div>
            <div class="text-right">
              <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.08em]">DECISION</div>
              <div class="text-primary mt-0.5 font-mono text-[15px] font-semibold">{decisionLabel(selectedDetail)}</div>
            </div>
          </div>
        </div>

        <!-- AI quality grade -->
        <div class="border-border flex flex-wrap items-center gap-3 border-b px-7 py-2.5">
          <span class="text-muted-foreground font-mono text-[10px] tracking-[0.08em]">AI QUALITY</span>
          {#if selectedGrade?.graded}
            <span class={cn('rounded-md border px-1.5 py-0.5 text-[10.5px] font-semibold', verdictTint(selectedGrade.verdict))}>
              {selectedGrade.verdict} · {selectedGrade.score}
            </span>
            {#if selectedGrade.summary}
              <span class="text-muted-foreground min-w-0 flex-1 truncate text-[12px]">{selectedGrade.summary}</span>
            {/if}
            {#each selectedGrade.issues ?? [] as issue (issue.code)}
              <code class="bg-muted/60 rounded px-1 py-px font-mono text-[10px]">{issue.code}</code>
            {/each}
          {:else}
            <span class="text-muted-foreground/70 flex-1 text-[12px]">Not graded yet.</span>
          {/if}
          <button
            type="button"
            disabled={gradeBusy}
            onclick={onGradeNow}
            class="border-border hover:bg-accent inline-flex items-center gap-1 rounded-md border px-2 py-1 text-[11px] transition-colors disabled:opacity-50"
          >
            {#if gradeBusy}<Loader2 class="size-3 animate-spin" />{:else}<Sparkles class="size-3" />{/if}
            {selectedGrade?.graded ? 'Re-grade' : 'Grade now'}
          </button>
          <button
            type="button"
            onclick={onCopyDossier}
            class="border-border hover:bg-accent inline-flex items-center gap-1 rounded-md border px-2 py-1 text-[11px] transition-colors"
          >
            <Copy class="size-3" /> Copy dossier
          </button>
        </div>

        <!-- Contributed + VIEW tabs -->
        <div class="border-border flex flex-wrap items-center justify-between gap-3 border-b px-7 py-2.5">
          <div class="flex items-center gap-2">
            <span class="text-muted-foreground font-mono text-[10px] tracking-[0.08em]">CONTRIBUTED</span>
            {#if contributed.length === 0}
              <span class="text-muted-foreground/60 text-[12px]">none</span>
            {/if}
            {#each contributed as c (c.label)}
              <span class="border-border inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5">
                <span class="size-2 rounded-full" style="background: {c.color}"></span>
                <span class="text-[12px]">{c.label}</span>
              </span>
            {/each}
          </div>
          <div class="bg-surface-sunken flex items-center gap-1 rounded-lg p-0.5">
            {#each VIEWS as v (v.key)}
              <button
                type="button"
                onclick={() => (view = v.key)}
                class={cn(
                  'rounded-md px-3 py-1.5 text-[12.5px] font-medium transition-colors',
                  view === v.key ? 'bg-primary text-primary-foreground' : 'text-muted-foreground hover:bg-accent'
                )}>{v.label}</button
              >
            {/each}
          </div>
        </div>

        {#if error}
          <div class="border-destructive/50 bg-destructive/10 text-destructive mx-7 mt-3 rounded-lg border p-3 text-sm">
            {error}
            <Button variant="ghost" size="sm" class="ml-2" onclick={() => (error = null)}>Dismiss</Button>
          </div>
        {/if}

        <!-- Scrollable content -->
        <div class="min-h-0 flex-1 space-y-4 overflow-y-auto px-7 py-5">
          <CandidateGrid {candidates} pickedKey={pickedKey[selectedTrack.id] ?? null} loading={detailLoading[selectedTrack.id]} onpick={pickCandidate} />

          {#if view === 'before'}
            <BeforeAfterView
              rows={beforeRows}
              values={finalValues}
              readonly={!selectedIsReview}
              {fromFolder}
              fileName={selectedTrack.fileName}
              fromMeta={fmtFromMeta(selectedTrack)}
              {destinationPath}
              destFormat={destFormat(selectedTrack)}
              onset={setField}
              oncopy={copyEmbedded}
            />
          {:else if view === 'timeline'}
            <TimelineView events={timeline} />
          {:else}
            <OriginMatrixView {matrix} />
          {/if}
        </div>

        <!-- Action bar -->
        <div class="border-border bg-background flex items-center gap-3 border-t px-7 py-3">
          <div class="min-w-0 flex-1">
            <div class="text-muted-foreground font-mono text-[10px] tracking-[0.08em]">WRITES TO</div>
            <div class="text-muted-foreground truncate font-mono text-[11px]">{destinationPath}</div>
          </div>
          <Button variant="outline" class="gap-1.5" onclick={handlePlayPause}>
            {#if isPlayingSelected}<Pause class="size-3.5" />{:else}<Play class="size-3.5" />{/if}
            Preview
          </Button>
          <AlertDialog.Root>
            <AlertDialog.Trigger>
              {#snippet child({ props })}
                <Button {...props} variant="ghost" size="icon" class="text-destructive hover:text-destructive size-9 shrink-0" title="Delete">
                  <Trash2 class="size-4" />
                </Button>
              {/snippet}
            </AlertDialog.Trigger>
            <AlertDialog.Content>
              <AlertDialog.Header>
                <AlertDialog.Title>Delete this track?</AlertDialog.Title>
                <AlertDialog.Description>
                  This soft-deletes the track so it is excluded from review and library build. The original file is
                  not deleted.
                </AlertDialog.Description>
              </AlertDialog.Header>
              <AlertDialog.Footer>
                <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                <AlertDialog.Action onclick={handleDelete}>Delete</AlertDialog.Action>
              </AlertDialog.Footer>
            </AlertDialog.Content>
          </AlertDialog.Root>

          {#if selectedIsReview}
            <Button variant="outline" onclick={handleReject} disabled={actionLoading} class="gap-1.5">
              {#if actionLoading}<Loader2 class="size-3.5 animate-spin" />{:else}<X class="size-3.5" />{/if}
              Reject
            </Button>
            <Button variant="outline" onclick={handleSkip} disabled={actionLoading}>Skip for now</Button>
            <Button onclick={handleAccept} disabled={actionLoading} class="gap-1.5">
              {#if actionLoading}<Loader2 class="size-3.5 animate-spin" />{:else}<Check class="size-3.5" strokeWidth={2} />{/if}
              Accept &amp; write
            </Button>
          {:else}
            <Button onclick={handleReenrich} disabled={actionLoading} class="gap-1.5">
              {#if actionLoading}<Loader2 class="size-3.5 animate-spin" />{:else}<RefreshCw class="size-3.5" />{/if}
              Re-enrich
            </Button>
          {/if}
        </div>
      </div>
    {/if}
  </main>
{/if}

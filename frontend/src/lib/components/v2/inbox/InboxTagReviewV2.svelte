<script lang="ts">
  import { untrack } from 'svelte';
  import { Check, X, ChevronLeft, ChevronRight, Loader2, RefreshCw, History, Copy, CheckCheck } from '@lucide/svelte';
  import { page } from '$app/state';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import type { ApiSong, EnrichmentDetail } from '$lib/api-client';
  import {
    fetchReviewQueue,
    fetchEnrichmentDetail,
    submitManualReview,
    copyQualitySongDossier,
    bulkApprove
  } from '$lib/api-client';
  import {
    reasonFor,
    candidatesFromDetail,
    buildDestinationPath,
    bestGuess,
    bannerFor,
    originalInfo,
    beforeAfterRows,
    EDITABLE_FIELDS,
    type ReviewCandidate,
    type EditableFieldKey
  } from '$lib/review-helpers';
  import { formatFileSize } from '$lib/formatters';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import CandidateGrid from '$lib/components/review/CandidateGrid.svelte';
  import BeforeAfterView from '$lib/components/review/BeforeAfterView.svelte';
  import { Button } from '$lib/components/ui/button';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as Tooltip from '$lib/components/ui/tooltip';
  import { toast } from 'svelte-sonner';
  import { cn } from '$lib/utils';

  // Reports its live remaining count up to the Inbox header (for the subtab pill).
  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();

  // Behavior-only: drives the single-pane drill-down on phones (md == 768 ==
  // IsMobile boundary) — the master/detail back button and CandidateGrid's
  // single-column layout. Never gates v1-vs-v2.
  const isMobile = new IsMobile();

  type MetadataEdits = Partial<Record<EditableFieldKey, string>>;
  type Decision = 'accept' | 'reject' | 'skip';

  // One status signal per row/header: a small colored dot + sentence-case label.
  // Amber/red are reserved for genuinely actionable states; ambiguous/info stays neutral.
  const STATUS_DOT: Record<'warn' | 'info' | 'err' | 'ok', string> = {
    warn: 'bg-amber-500',
    info: 'bg-muted-foreground/50',
    err: 'bg-red-500',
    ok: 'bg-primary'
  };

  let tracks = $state<ApiSong[]>([]);
  let selectedId = $state<number | null>(null);
  let editedMetadata = $state<Record<number, MetadataEdits>>({});
  let pickedKey = $state<Record<number, string>>({});
  let decisions = $state<Record<number, Decision>>({});
  let details = $state<Record<number, EnrichmentDetail>>({});
  let detailLoading = $state<Record<number, boolean>>({});

  let loading = $state(true);
  let actionLoading = $state(false);
  let error = $state<string | null>(null);

  // Bulk-approve: approves every NeedsReview row at/above the chosen confidence.
  let bulkOpen = $state(false);
  let bulkThreshold = $state(0.9);
  const BULK_THRESHOLDS = [0.95, 0.9, 0.85, 0.8, 0.75] as const;
  const bulkAffectedCount = $derived(
    tracks.filter((t) => t.matchConfidence != null && t.matchConfidence >= bulkThreshold).length
  );

  // Accepted/rejected rows leave `tracks` entirely; skipped rows stay (dimmed,
  // badged) but no longer count as remaining work.
  const remainingCount = $derived(tracks.filter((t) => !decisions[t.id]).length);
  const skippedCount = $derived(tracks.length - remainingCount);

  // Report the live count up to the parent Inbox tab strip. The callback is
  // invoked via untrack() so the effect depends only on `loading`/`tracks` — not
  // on the `oncount` prop's identity. The parent passes a fresh inline arrow on
  // every render, and tracking it here would re-run this effect each time the
  // parent re-renders, which (because the parent reassigns its counts object on
  // every call) is a self-sustaining loop → effect_update_depth_exceeded.
  $effect(() => {
    const n = loading ? null : remainingCount;
    untrack(() => oncount?.(n));
  });

  async function loadQueue() {
    try {
      loading = true;
      error = null;
      tracks = await fetchReviewQueue('needsreview');
      editedMetadata = {};
      pickedKey = {};
      decisions = {};
      details = {};
      detailLoading = {};
      // Honor a ?song=<id> deep-link from Quality / AI flagged.
      const deepLinkId = Number(page.url.searchParams.get('song'));
      selectedId =
        Number.isFinite(deepLinkId) && tracks.some((t) => t.id === deepLinkId)
          ? deepLinkId
          : (tracks[0]?.id ?? null);
      // Prefetch just the first few rows so the detail pane feels instant; the rest
      // load lazily on selection (see the selectedId effect). Eagerly fetching every
      // row would fire one request per queue item — a fetch storm at scale.
      for (const t of tracks.slice(0, 5)) void loadDetail(t.id);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load review queue';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void loadQueue();
  });

  const selectedTrack = $derived(tracks.find((t) => t.id === selectedId) ?? null);
  const selectedDetail = $derived(selectedTrack ? (details[selectedTrack.id] ?? null) : null);
  const candidates = $derived(candidatesFromDetail(selectedDetail));

  // Render-only grouping of the flat queue by album (artist + album), biggest group
  // first so the largest batches surface at the top. Pure $derived — writes no state.
  type AlbumGroup = { key: string; album: string; artist: string; items: ApiSong[] };
  const grouped = $derived.by<AlbumGroup[]>(() => {
    const map = new Map<string, AlbumGroup>();
    for (const t of tracks) {
      const artist = (t.albumArtist ?? t.artist ?? 'Unknown artist').trim();
      const album = (t.album ?? 'Unknown album').trim();
      const key = `${artist.toLowerCase()}::${album.toLowerCase()}`;
      const g = map.get(key) ?? { key, album, artist, items: [] };
      g.items.push(t);
      map.set(key, g);
    }
    return [...map.values()].sort((a, b) => b.items.length - a.items.length);
  });

  function seedFields(track: ApiSong, top: ReviewCandidate | undefined, detail: EnrichmentDetail | null): MetadataEdits {
    const cur = detail?.current;
    return {
      title: top?.title ?? cur?.title ?? track.title ?? '',
      artist: top?.artist ?? cur?.artist ?? track.artist ?? '',
      album: top?.album ?? cur?.album ?? track.album ?? '',
      albumArtist: cur?.albumArtist ?? track.albumArtist ?? '',
      year: top?.year || (cur?.year != null ? String(cur.year) : track.year != null ? String(track.year) : ''),
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
    const id = selectedId;
    if (id == null) return;
    void loadDetail(id);
    // Prefetch the next track so advancing feels instant. Read `tracks` via untrack
    // so this effect tracks only `selectedId` (not every queue mutation).
    untrack(() => {
      const idx = tracks.findIndex((t) => t.id === id);
      const next = idx >= 0 ? tracks[idx + 1] : undefined;
      if (next) void loadDetail(next.id);
    });
  });

  function pickCandidate(c: ReviewCandidate) {
    const id = selectedTrack?.id;
    if (id == null) return;
    pickedKey = { ...pickedKey, [id]: c.key };
    editedMetadata = {
      ...editedMetadata,
      [id]: { ...editedMetadata[id], title: c.fields.title, artist: c.fields.artist, album: c.fields.album, year: c.fields.year }
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
  const beforeRows = $derived(beforeAfterRows(selectedDetail));
  const banner = $derived(selectedTrack ? bannerFor(selectedTrack, selectedDetail) : null);
  const original = $derived(selectedTrack ? originalInfo(selectedTrack, selectedDetail) : null);
  const guess = $derived(selectedTrack ? bestGuess(selectedTrack, candidates, selectedDetail) : null);

  const destinationPath = $derived(
    selectedTrack ? buildDestinationPath(finalValues, selectedTrack.extension) : ''
  );
  const fromFolder = $derived(
    selectedTrack ? selectedTrack.sourcePath.slice(0, selectedTrack.sourcePath.lastIndexOf('/')) : ''
  );

  function fmtFromMeta(track: ApiSong): string {
    const parts: string[] = [];
    const fmt = (track.extension ?? '').replace(/^\./, '').toUpperCase();
    if (fmt) parts.push(track.bitRate ? `${fmt} ${track.bitRate}kbps` : fmt);
    parts.push(formatFileSize(track.fileSizeBytes));
    return parts.join(' · ');
  }
  function destFormat(track: ApiSong): string {
    const fmt = (track.extension ?? '').replace(/^\./, '').toUpperCase() || 'FLAC';
    return track.bitRate ? `${fmt} ${track.bitRate}kbps` : fmt;
  }

  function rowOriginal(track: ApiSong): { title: string; subtitle: string; titleFromFilename: boolean } {
    const o = originalInfo(track, details[track.id]);
    const subtitle = o.subtitle || (o.titleFromFilename ? '' : o.fileName);
    return { title: o.title, subtitle, titleFromFilename: o.titleFromFilename };
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

  function advanceToNextUndecided(fromId: number) {
    const next = tracks.find((t) => t.id !== fromId && !decisions[t.id]);
    if (next) selectedId = next.id;
  }

  async function handleAccept() {
    const track = selectedTrack;
    if (!track || actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      await submitManualReview(track.id, { decision: 'approve', ...buildOverrides(track.id) });
      decisions = { ...decisions, [track.id]: 'accept' };
      const next = tracks.find((t) => t.id !== track.id && !decisions[t.id]);
      tracks = tracks.filter((t) => t.id !== track.id);
      selectedId = next?.id ?? tracks[0]?.id ?? null;
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
      const next = tracks.find((t) => t.id !== track.id && !decisions[t.id]);
      tracks = tracks.filter((t) => t.id !== track.id);
      selectedId = next?.id ?? tracks[0]?.id ?? null;
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

  async function onCopyDossier(songId: number) {
    try {
      await copyQualitySongDossier(songId);
      toast.success('Copied dossier to clipboard — paste into Claude Code');
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Copy failed');
    }
  }

  async function handleBulkApprove() {
    if (actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      const res = await bulkApprove(bulkThreshold);
      bulkOpen = false;
      toast.success(
        `Approved ${res.approvedCount} track${res.approvedCount === 1 ? '' : 's'}` +
          (res.skippedCount > 0 ? ` · ${res.skippedCount} skipped` : '')
      );
      await loadQueue();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to bulk approve';
      toast.error(error);
    } finally {
      actionLoading = false;
    }
  }

  function selectAt(delta: number) {
    // Traverse only undecided rows (plus the current one, which may itself be
    // skipped) so arrow keys reflect actual remaining work.
    const pool = tracks.filter((t) => t.id === selectedId || !decisions[t.id]);
    if (pool.length === 0) return;
    const idx = pool.findIndex((t) => t.id === selectedId);
    const next = Math.max(0, Math.min(pool.length - 1, (idx < 0 ? 0 : idx) + delta));
    selectedId = pool[next].id;
  }

  // Keyboard: A accept · S skip · R reject · ←/→ nav. Ignore while typing.
  function onKeydown(e: KeyboardEvent) {
    const el = e.target as HTMLElement | null;
    if (el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable)) return;
    if (e.metaKey || e.ctrlKey || e.altKey) return;
    switch (e.key.toLowerCase()) {
      case 'a':
        e.preventDefault();
        void handleAccept();
        break;
      case 's':
        e.preventDefault();
        handleSkip();
        break;
      case 'r':
        e.preventDefault();
        void handleReject();
        break;
      case 'arrowdown':
      case 'arrowright':
        e.preventDefault();
        selectAt(1);
        break;
      case 'arrowup':
      case 'arrowleft':
        e.preventDefault();
        selectAt(-1);
        break;
    }
  }
</script>

<svelte:window onkeydown={onKeydown} />

{#if loading}
  <div class="flex flex-1 items-center justify-center p-8">
    <div class="text-muted-foreground flex items-center gap-2 text-sm">
      <Loader2 class="size-5 animate-spin" /> Loading review queue…
    </div>
  </div>
{:else if error && tracks.length === 0}
  <div class="flex flex-1 items-center justify-center p-8">
    <div class="max-w-md text-center">
      <p class="text-destructive mb-3 text-sm">{error}</p>
      <Button onclick={loadQueue}>Retry</Button>
    </div>
  </div>
{:else if tracks.length === 0}
  <div class="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
    <span class="bg-primary/10 text-primary grid size-12 place-items-center rounded-full">
      <Check class="size-6" />
    </span>
    <div class="text-[15px] font-semibold">Nothing needs review</div>
    <p class="text-muted-foreground max-w-sm text-[12.5px]">
      Every track either matched with enough confidence or has already been decided.
    </p>
  </div>
{:else}
  <div class="grid min-h-0 flex-1 grid-cols-1 overflow-hidden md:grid-cols-[320px_1fr]">
    <!-- List — single-pane on mobile: hidden once a track is selected. -->
    <aside
      class="border-border bg-surface-sunken flex min-h-0 flex-col border-r md:flex"
      class:hidden={selectedId != null}
    >
      <div class="border-border flex items-center justify-between gap-2 border-b px-4 py-2.5">
        <span class="text-muted-foreground text-[11px]">
          {remainingCount} awaiting review{#if skippedCount > 0}
            · {skippedCount} skipped{/if}
        </span>
        <div class="flex items-center gap-1.5">
          <Button
            variant="outline"
            size="sm"
            class="h-7 gap-1.5 px-2 text-[11px]"
            disabled={actionLoading || tracks.length === 0}
            onclick={() => (bulkOpen = true)}
          >
            <CheckCheck class="size-3.5" /> Bulk approve
          </Button>
          <button
            type="button"
            onclick={loadQueue}
            title="Refresh"
            class="text-muted-foreground hover:bg-accent hover:text-foreground grid size-7 place-items-center rounded-md transition-colors"
          >
            <RefreshCw class="size-3.5" />
          </button>
        </div>
      </div>
      <AlertDialog.Root bind:open={bulkOpen}>
        <AlertDialog.Content>
          <AlertDialog.Header>
            <AlertDialog.Title>Bulk approve high-confidence matches</AlertDialog.Title>
            <AlertDialog.Description>
              Approves every track whose top match is at or above the chosen confidence,
              applying its winning candidate. This writes to the library and can't be undone in bulk.
            </AlertDialog.Description>
          </AlertDialog.Header>
          <div class="space-y-3 py-1">
            <div class="flex flex-wrap gap-1.5">
              {#each BULK_THRESHOLDS as t (t)}
                <button
                  type="button"
                  onclick={() => (bulkThreshold = t)}
                  class={cn(
                    'rounded-md border px-2.5 py-1 text-[12px] font-medium transition-colors',
                    bulkThreshold === t
                      ? 'border-primary bg-primary/10 text-primary'
                      : 'border-border hover:bg-accent'
                  )}
                >
                  {Math.round(t * 100)}%
                </button>
              {/each}
            </div>
            <p class="text-muted-foreground text-[12.5px]">
              Approves <b class="text-foreground">{bulkAffectedCount}</b> of {tracks.length} tracks
              at ≥{Math.round(bulkThreshold * 100)}% confidence.
            </p>
          </div>
          <AlertDialog.Footer>
            <AlertDialog.Cancel disabled={actionLoading}>Cancel</AlertDialog.Cancel>
            <AlertDialog.Action
              disabled={actionLoading || bulkAffectedCount === 0}
              onclick={(e) => {
                e.preventDefault();
                void handleBulkApprove();
              }}
            >
              {#if actionLoading}<Loader2 class="mr-1.5 size-3.5 animate-spin" />{/if}
              Approve {bulkAffectedCount}
            </AlertDialog.Action>
          </AlertDialog.Footer>
        </AlertDialog.Content>
      </AlertDialog.Root>
      <div class="min-h-0 flex-1 overflow-y-auto p-1.5 pb-[calc(0.375rem_+_var(--mh-content-pad))]">
        {#each grouped as group (group.key)}
          <div
            class="bg-surface-sunken text-muted-foreground sticky top-0 z-10 flex items-center gap-2 px-2 py-1.5"
          >
            <div class="min-w-0 flex-1">
              <div class="text-foreground truncate text-[12px] font-semibold">{group.album}</div>
              <div class="truncate text-[11px]">{group.artist}</div>
            </div>
            <span class="text-muted-foreground shrink-0 text-[11px] tabular-nums">
              {group.items.length}
            </span>
          </div>
          {#each group.items as track (track.id)}
            {@const r = reasonFor(track)}
            {@const info = rowOriginal(track)}
            {@const decided = decisions[track.id] != null}
            <button
              type="button"
              onclick={() => (selectedId = track.id)}
              class={cn(
                'mb-0.5 flex w-full items-center gap-2.5 rounded-md border-l-2 border-transparent py-2 pr-2.5 pl-2 text-left transition-[background-color,transform] duration-100 ease-out active:scale-[0.99]',
                selectedId === track.id ? 'border-l-primary bg-card' : 'hover:bg-accent',
                decided && 'opacity-60'
              )}
            >
              <Cover artist={track.artist ?? 'Unknown'} title={info.title} size={40} corner={6} caption={false} />
              <div class="min-w-0 flex-1">
                <div class={cn('truncate text-[13px] font-medium', info.titleFromFilename && 'font-mono text-[12px]')}>{info.title}</div>
                <div class="text-muted-foreground truncate text-[11.5px]">{info.subtitle || '—'}</div>
              </div>
              <span class="text-muted-foreground flex shrink-0 items-center gap-1.5 text-[11px]">
                <span class={cn('size-1.5 rounded-full', decided ? 'bg-muted-foreground/40' : STATUS_DOT[r.tint])}></span>
                {decided ? 'Skipped' : r.label}
              </span>
            </button>
          {/each}
        {/each}
      </div>
    </aside>

    <!-- Detail — single-pane on mobile: hidden until a track is selected. -->
    {#if selectedTrack && banner && original && guess}
      <div
        class="flex min-h-0 min-w-0 flex-col overflow-hidden md:flex"
        class:hidden={selectedId == null}
      >
        <!-- Header — status is a small inline dot + label beside the title, not a banner. -->
        <div class="border-border flex flex-wrap items-start gap-x-4 gap-y-3 border-b px-4 pt-4 pb-4 sm:px-6">
          <button
            type="button"
            onclick={() => (selectedId = null)}
            class="text-muted-foreground hover:bg-accent hover:text-foreground -ml-1 grid size-8 shrink-0 place-items-center rounded-md transition-colors md:hidden"
            title="Back to list"
            aria-label="Back to list"
          >
            <ChevronLeft class="size-5" />
          </button>
          <Cover artist={original.subtitle || 'Unknown'} title={original.title} size={52} corner={8} caption={false} />
          <div class="min-w-0 flex-1">
            <div class="text-muted-foreground hidden text-[11px] sm:block">Original file</div>
            <div class="flex min-w-0 flex-wrap items-center gap-x-2.5 gap-y-0.5">
              <h2 class={cn('my-0.5 max-w-full min-w-0 truncate text-[20px] font-semibold tracking-tight', original.titleFromFilename && 'font-mono text-[16px]')}>{original.title}</h2>
              <span class="text-muted-foreground flex shrink-0 items-center gap-1.5 text-[12px]" title={banner.body}>
                <span class={cn('size-1.5 rounded-full', STATUS_DOT[banner.tone])}></span>
                {banner.title}
              </span>
            </div>
            {#if original.subtitle}<div class="text-muted-foreground truncate text-[12.5px]">{original.subtitle}</div>{/if}
            <div class="text-muted-foreground/70 truncate font-mono text-[11px]">{original.fileName}</div>
            {#if guess.title && guess.title !== original.title}
              <div class="text-muted-foreground mt-1.5 truncate text-[12px]">
                Best guess: <span class="text-foreground/80">{guess.title}</span>{guess.subtitle ? ' · ' + guess.subtitle : ''}
              </div>
            {/if}
          </div>
          <div class="flex w-full shrink-0 items-center gap-2 sm:w-auto">
            <button
              type="button"
              onclick={() => onCopyDossier(selectedTrack.id)}
              class="border-border bg-card hover:bg-muted text-foreground inline-flex shrink-0 items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12px] font-medium transition-colors"
            >
              <Copy class="size-3.5" /> Copy dossier
            </button>
            <a
              href={`/track/${selectedTrack.id}`}
              class="border-border bg-card hover:bg-muted text-foreground inline-flex shrink-0 items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12px] font-medium transition-colors"
            >
              <History class="size-3.5" /> View timeline
            </a>
          </div>
        </div>

        {#if error}
          <div class="border-destructive/50 bg-destructive/10 text-destructive mx-4 mt-3 rounded-lg border p-3 text-sm sm:mx-6">
            {error}
            <Button variant="ghost" size="sm" class="ml-2" onclick={() => (error = null)}>Dismiss</Button>
          </div>
        {/if}

        <!-- Scrollable body: candidates + before/after diff -->
        <div class="min-h-0 flex-1 space-y-4 overflow-y-auto px-4 py-4 pb-[calc(1rem_+_var(--mh-content-pad))] sm:px-6">
          <div class="flex items-baseline gap-2">
            <span class="text-foreground text-[13px] font-semibold">Candidates</span>
            <span class="text-muted-foreground text-[11.5px]">Pick a provider's answer, or override fields below.</span>
          </div>
          <CandidateGrid {candidates} pickedKey={pickedKey[selectedTrack.id] ?? null} loading={detailLoading[selectedTrack.id]} onpick={pickCandidate} single={isMobile.current} />

          <div class="flex items-baseline gap-2 pt-1">
            <span class="text-foreground text-[13px] font-semibold">Field diff</span>
            <span class="text-muted-foreground text-[11.5px]">Embedded tags → what we'll write.</span>
          </div>
          <BeforeAfterView
            rows={beforeRows}
            values={finalValues}
            {fromFolder}
            fileName={selectedTrack.fileName}
            fromMeta={fmtFromMeta(selectedTrack)}
            {destinationPath}
            destFormat={destFormat(selectedTrack)}
            onset={setField}
            oncopy={copyEmbedded}
          />
        </div>

        <!-- Action bar — keyboard shortcuts live in a "?" tooltip so they stay discoverable at any width. -->
        <div class="border-border bg-background flex flex-wrap items-center gap-2 border-t px-4 py-3 sm:gap-3 sm:px-6">
          <div class="flex flex-1 items-center">
            <Tooltip.Provider delayDuration={150}>
              <Tooltip.Root>
                <Tooltip.Trigger
                  class="border-border text-muted-foreground hover:bg-accent hover:text-foreground grid size-7 place-items-center rounded-full border text-[12px] font-medium transition-colors"
                  aria-label="Keyboard shortcuts"
                >?</Tooltip.Trigger>
                <Tooltip.Content side="top" align="start">
                  A accept · S skip · R reject · ← → navigate
                </Tooltip.Content>
              </Tooltip.Root>
            </Tooltip.Provider>
          </div>
          <div class="flex items-center justify-end gap-2">
            <Button variant="outline" onclick={handleSkip} disabled={actionLoading}>Skip</Button>
            <Button variant="outline" onclick={handleReject} disabled={actionLoading} class="gap-1.5">
              {#if actionLoading}<Loader2 class="size-3.5 animate-spin" />{:else}<X class="size-3.5" />{/if}
              Reject
            </Button>
            <Button onclick={handleAccept} disabled={actionLoading} class="gap-1.5">
              {#if actionLoading}<Loader2 class="size-3.5 animate-spin" />{:else}<Check class="size-3.5" strokeWidth={2} />{/if}
              Accept &amp; next <ChevronRight class="size-3.5" />
            </Button>
          </div>
        </div>
      </div>
    {/if}
  </div>
{/if}

<script lang="ts">
  import { untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import {
    Check,
    X,
    ChevronUp,
    ChevronDown,
    Loader2,
    RefreshCw,
    History,
    Copy,
    CheckCheck,
    SkipForward
  } from '@lucide/svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import type { ApiSong, EnrichmentDetail } from '$lib/api-client';
  import {
    fetchReviewQueue,
    fetchEnrichmentDetail,
    submitManualReview,
    resetSongEnrichment,
    copyQualitySongDossier,
    bulkApprove,
    coverUrlForSong
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
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Button } from '$lib/components/ui/button';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import * as Tooltip from '$lib/components/ui/tooltip';
  import { toast } from 'svelte-sonner';
  import { cn } from '$lib/utils';
  import InboxDecisionBar from './InboxDecisionBar.svelte';
  import InboxQueueRow from './InboxQueueRow.svelte';
  import InboxQueueStates from './InboxQueueStates.svelte';
  import { QueueSelection } from './queue-selection.svelte';
  import { nextAfterDecision } from './queue-selection';
  import { radioGroup, radioTabIndex } from '$lib/components/review/radio-group';

  // Reports its live remaining count up to the Inbox header (for the subtab pill).
  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();
  // Ids for the album headers that name the queue list's groups.
  const listUid = $props.id();

  // Compact (below md, the IsMobile boundary) is a Mail-style push: the list, then a pushed
  // detail with a bottom decision toolbar. md+ keeps the 320px list pane beside the detail and
  // the keyboard triage.
  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  type MetadataEdits = Partial<Record<EditableFieldKey, string>>;
  type Decision = 'accept' | 'reject' | 'skip';

  // One status signal per row/header: a small coloured dot + sentence-case label — the word is
  // always there, so colour never carries the state alone. Amber/red are reserved for genuinely
  // actionable states; ambiguous/info stays neutral.
  const STATUS_DOT: Record<'warn' | 'info' | 'err' | 'ok', string> = {
    warn: 'bg-warning',
    info: 'bg-muted-foreground-dim',
    err: 'bg-destructive',
    ok: 'bg-primary'
  };

  let tracks = $state<ApiSong[]>([]);
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
  function countAtOrAbove(threshold: number): number {
    return tracks.filter((t) => t.matchConfidence != null && t.matchConfidence >= threshold).length;
  }
  const bulkAffectedCount = $derived(countAtOrAbove(bulkThreshold));

  // Accepted/rejected rows leave `tracks` entirely; skipped rows stay (in secondary text,
  // labelled) but no longer count as remaining work.
  const remainingCount = $derived(tracks.filter((t) => !decisions[t.id]).length);
  const skippedCount = $derived(tracks.length - remainingCount);
  const queueMeta = $derived(
    loading
      ? undefined
      : `${remainingCount.toLocaleString()} awaiting review` +
          (skippedCount > 0 ? ` · ${skippedCount} skipped` : '')
  );

  // Report the live count to whoever passes `oncount`. The callback is invoked
  // via untrack() so the effect depends only on `loading`/`tracks` — not on the
  // `oncount` prop's identity. A parent passing a fresh inline arrow on every
  // render would otherwise re-run this effect each time it re-renders, and a
  // parent that writes the count into its own state on every call turns that
  // into a self-sustaining loop → effect_update_depth_exceeded.
  $effect(() => {
    const n = loading ? null : remainingCount;
    untrack(() => oncount?.(n));
  });

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
  // The order the list shows, which is the order "next", the chevrons and "3 of 57" follow.
  const orderedIds = $derived(grouped.flatMap((g) => g.items.map((t) => t.id)));

  // The selection lives in the URL (?song=): a push to the detail on a phone, a replace for
  // every desktop selection and every auto-advance. See QueueSelection.
  const selection = new QueueSelection({
    tab: 'review',
    param: 'song',
    ids: () => orderedIds,
    compact: () => compact,
    ready: () => !loading,
    // An AI-flagged track's "Open in review", or an old link, can name a track that is not
    // waiting here; the queue drops it, and says why.
    onmissing: () =>
      toast('That track isn’t awaiting review', {
        description: 'It was decided already, or it matched without needing a look.'
      })
  });
  const selectedId = $derived(selection.selectedId);
  const position = $derived(selection.position);
  const selectedTrack = $derived(tracks.find((t) => t.id === selectedId) ?? null);
  const selectedDetail = $derived(selectedTrack ? (details[selectedTrack.id] ?? null) : null);
  const candidates = $derived(candidatesFromDetail(selectedDetail));
  const compactDetail = $derived(compact && selectedTrack != null);

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
      // Prefetch just the first few rows so the detail pane feels instant; the rest
      // load lazily on selection (see the selectedId effect). Eagerly fetching every
      // row would fire one request per queue item — a fetch storm at scale.
      for (const id of orderedIds.slice(0, 5)) void loadDetail(id);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load review queue';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void loadQueue();
  });

  function seedFields(
    track: ApiSong,
    top: ReviewCandidate | undefined,
    detail: EnrichmentDetail | null
  ): MetadataEdits {
    const cur = detail?.current;
    return {
      title: top?.title ?? cur?.title ?? track.title ?? '',
      artist: top?.artist ?? cur?.artist ?? track.artist ?? '',
      album: top?.album ?? cur?.album ?? track.album ?? '',
      albumArtist: cur?.albumArtist ?? track.albumArtist ?? '',
      year:
        top?.year ||
        (cur?.year != null ? String(cur.year) : track.year != null ? String(track.year) : ''),
      trackNumber:
        cur?.trackNumber != null
          ? String(cur.trackNumber)
          : track.trackNumber != null
            ? String(track.trackNumber)
            : ''
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
        if (track)
          editedMetadata = { ...editedMetadata, [id]: seedFields(track, cands[0], detail) };
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
    // Prefetch the next track so advancing feels instant. Read the order via untrack
    // so this effect tracks only `selectedId` (not every queue mutation).
    untrack(() => {
      const next = position.next;
      if (next != null) void loadDetail(next);
    });
  });

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
  const beforeRows = $derived(beforeAfterRows(selectedDetail));
  const banner = $derived(selectedTrack ? bannerFor(selectedTrack, selectedDetail) : null);
  const original = $derived(selectedTrack ? originalInfo(selectedTrack, selectedDetail) : null);
  const guess = $derived(
    selectedTrack ? bestGuess(selectedTrack, candidates, selectedDetail) : null
  );

  const destinationPath = $derived(
    selectedTrack ? buildDestinationPath(finalValues, selectedTrack.extension) : ''
  );
  const fromFolder = $derived(
    selectedTrack
      ? selectedTrack.sourcePath.slice(0, selectedTrack.sourcePath.lastIndexOf('/'))
      : ''
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

  function rowOriginal(track: ApiSong): {
    title: string;
    subtitle: string;
    titleFromFilename: boolean;
  } {
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

  // After a decision the queue moves on to the next undecided track after this one (a replace,
  // so Back still returns to the list). With none left, a phone goes back to the list and a
  // desktop shows the first remaining row.
  function advanceAfter(id: number, removed: boolean) {
    const next = nextAfterDecision(id, orderedIds, (c) => c !== id && !decisions[c]);
    if (next != null) selection.select(next);
    else if (removed || compact) selection.select(null);
  }

  async function handleAccept() {
    const track = selectedTrack;
    if (!track || actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      await submitManualReview(track.id, { decision: 'approve', ...buildOverrides(track.id) });
      decisions = { ...decisions, [track.id]: 'accept' };
      advanceAfter(track.id, true);
      tracks = tracks.filter((t) => t.id !== track.id);
      const title = editedMetadata[track.id]?.title || track.title || track.fileName;
      // Ten seconds, not sonner's four: long enough to notice the wrong one went, read the
      // toast and reach it.
      toast.success(`Accepted “${title}”`, {
        duration: 10_000,
        action: { label: 'Undo', onClick: () => void undoAccept(track.id, title) }
      });
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to accept track';
    } finally {
      actionLoading = false;
    }
  }

  // Undo is a forced reset with restore: it puts back the tags the approval overwrote (the approve
  // path snapshots them first), lifts the approval lock and re-queues matching. The track isn't put
  // back in the list here — its old candidates are gone — it returns once re-matching lands it in
  // NeedsReview again.
  async function undoAccept(songId: number, title: string) {
    try {
      await resetSongEnrichment(songId, true, true);
      toast.success(`Restored the original tags of “${title}”`, {
        description: 'Re-matching it now. It comes back here if it still needs review.'
      });
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Undo failed');
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
      advanceAfter(track.id, true);
      tracks = tracks.filter((t) => t.id !== track.id);
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
    advanceAfter(track.id, false);
  }

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
    const pool = orderedIds.filter((id) => id === selectedId || !decisions[id]);
    if (pool.length === 0) return;
    const idx = pool.findIndex((id) => id === selectedId);
    const next = Math.max(0, Math.min(pool.length - 1, (idx < 0 ? 0 : idx) + delta));
    selection.select(pool[next]);
  }

  // Keyboard (desktop): ⇧A accept · S skip · ⇧R reject · ←/→ nav. Ignore while typing.
  // Accept and Reject write to the library, so they take a deliberate Shift chord instead of a
  // bare letter a stray keypress can fire (Accept has an Undo toast; Reject has none). Skip only
  // moves the selection, so it stays a single key. A phone has the decision toolbar instead.
  //
  // The shortcuts belong to the queue, so they stand down whenever something else has the
  // keyboard: a modal (Bulk approve, any dialog), an open menu (the account menu's arrow keys),
  // a radio group or listbox that uses the arrows itself, or a handler that already took the key.
  const KEY_OWNERS =
    '[role="dialog"],[role="alertdialog"],[role="menu"],[role="listbox"],[role="radiogroup"]';
  function onKeydown(e: KeyboardEvent) {
    if (compact || e.defaultPrevented || bulkOpen) return;
    const el = e.target as HTMLElement | null;
    if (
      el &&
      (el.tagName === 'INPUT' ||
        el.tagName === 'TEXTAREA' ||
        el.tagName === 'SELECT' ||
        el.isContentEditable ||
        el.closest?.(KEY_OWNERS))
    )
      return;
    // A modal or menu that is open but does not hold focus (focus left on the page behind it).
    if (
      document.querySelector(
        '[role="dialog"][data-state="open"],[role="alertdialog"][data-state="open"],[role="menu"][data-state="open"]'
      )
    )
      return;
    if (e.metaKey || e.ctrlKey || e.altKey) return;
    switch (e.key.toLowerCase()) {
      case 'a':
        if (!e.shiftKey) return;
        e.preventDefault();
        void handleAccept();
        break;
      case 's':
        if (e.shiftKey) return;
        e.preventDefault();
        handleSkip();
        break;
      case 'r':
        if (!e.shiftKey) return;
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

  // The phone's list unmounts while a detail is pushed; put it back where it was on return.
  let listScroller = $state<HTMLElement | null>(null);
  let listScrollTop = 0;
  $effect(() => {
    const el = listScroller;
    if (el && listScrollTop > 0) requestAnimationFrame(() => (el.scrollTop = listScrollTop));
  });
  // Each item opens at its top: the chevrons and an auto-advance reuse the same scroller.
  let detailScroller = $state<HTMLElement | null>(null);
  $effect(() => {
    void selectedId;
    untrack(() => detailScroller?.scrollTo({ top: 0 }));
  });

  function openRow(event: MouseEvent, id: number) {
    if (compact && listScroller) listScrollTop = listScroller.scrollTop;
    selection.onRowClick(event, id);
  }
</script>

<svelte:window onkeydown={onKeydown} />

<!-- ── Pieces shared by both widths ───────────────────────────────────────────────── -->

{#snippet statusDot(tone: 'warn' | 'info' | 'err' | 'ok')}
  <span class={cn('size-2 shrink-0 rounded-full md:size-1.5', STATUS_DOT[tone])} aria-hidden="true"
  ></span>
{/snippet}

{#snippet queueList()}
  {#each grouped as group, gi (group.key)}
    <!-- A labelled group, not a <section>: a named section is a region landmark, and ten albums
         of them crowded VoiceOver's Landmarks rotor. -->
    <div role="group" aria-labelledby="{listUid}-album-{gi}">
      <!-- Sticky album header: below the phone's nav bar, at the top of the desktop pane. -->
      <div
        id="{listUid}-album-{gi}"
        class="bg-background md:bg-surface-sunken text-footnote sticky top-[var(--mh-navbar-h,0px)] z-10 flex items-baseline gap-2 px-4 pt-4 pb-1 md:top-0 md:px-2 md:pt-1.5 md:pb-1.5 md:text-[11px] md:leading-4"
      >
        <div class="min-w-0 flex-1 truncate">
          <span class="font-semibold">{group.album}</span>
          <span class="text-muted-foreground"> · {group.artist}</span>
        </div>
        <span class="text-muted-foreground shrink-0 tabular-nums">{group.items.length}</span>
      </div>
      <div>
        {#each group.items as track (track.id)}
          {@const r = reasonFor(track)}
          {@const info = rowOriginal(track)}
          {@const decided = decisions[track.id] != null}
          <InboxQueueRow
            href={selection.href(track.id)}
            onclick={(e) => openRow(e, track.id)}
            selected={selectedId === track.id}
            {compact}
            cover={{
              artist: track.artist ?? 'Unknown',
              title: info.title,
              url: coverUrlForSong(track)
            }}
            title={info.title}
            titleMono={info.titleFromFilename}
            muted={decided}
          >
            {#snippet detail()}
              {@render statusDot(decided ? 'info' : r.tint)}
              <span class="min-w-0 truncate">
                {decided ? 'Skipped' : r.label}<span class="md:hidden"
                  >{info.subtitle ? ` · ${info.subtitle}` : ''}</span
                ></span
              >
            {/snippet}
          </InboxQueueRow>
        {/each}
      </div>
    </div>
  {/each}
{/snippet}

{#snippet listMore()}
  <DropdownMenu.Item
    disabled={actionLoading || tracks.length === 0}
    onSelect={() => (bulkOpen = true)}
  >
    <CheckCheck /> Bulk approve…
  </DropdownMenu.Item>
  <DropdownMenu.Item onSelect={() => void loadQueue()}>
    <RefreshCw /> Refresh
  </DropdownMenu.Item>
{/snippet}

{#snippet desktopActions()}
  <Button
    variant="outline"
    size="sm"
    class="h-8 gap-1.5 px-2.5"
    disabled={actionLoading || tracks.length === 0}
    onclick={() => (bulkOpen = true)}
  >
    <CheckCheck class="size-4" />
    <span class="text-nav-sm">Bulk approve…</span>
  </Button>
  <Button
    variant="ghost"
    size="icon"
    aria-label="Refresh review queue"
    title="Refresh"
    onclick={() => void loadQueue()}
  >
    <RefreshCw />
  </Button>
{/snippet}

{#snippet detailChevrons()}
  <Button
    variant="ghost"
    size="icon"
    aria-label="Previous track"
    disabled={position.prev == null}
    onclick={() => selection.select(position.prev)}
  >
    <ChevronUp />
  </Button>
  <Button
    variant="ghost"
    size="icon"
    aria-label="Next track"
    disabled={position.next == null}
    onclick={() => selection.select(position.next)}
  >
    <ChevronDown />
  </Button>
{/snippet}

{#snippet detailMore()}
  {#if selectedTrack}
    {@const id = selectedTrack.id}
    <DropdownMenu.Item onSelect={() => void onCopyDossier(id)}>
      <Copy /> Copy dossier
    </DropdownMenu.Item>
    <DropdownMenu.Item onSelect={() => void goto(`/track/${id}`)}>
      <History /> View timeline
    </DropdownMenu.Item>
  {/if}
{/snippet}

{#snippet queueStates()}
  {#if loading}
    <InboxQueueStates state="loading" label="Loading review queue…" {compact} />
  {:else if error && tracks.length === 0}
    <InboxQueueStates state="error" message={error} onretry={loadQueue} />
  {:else}
    <InboxQueueStates state="empty" icon={Check} title="Nothing needs review">
      Every track either matched with enough confidence or has already been decided.
    </InboxQueueStates>
  {/if}
{/snippet}

{#snippet errorNote()}
  {#if error}
    <div
      role="alert"
      class="bg-destructive/10 text-destructive-text text-subheadline flex items-center gap-2 rounded-xl py-1.5 pr-1.5 pl-4 md:rounded-lg md:text-sm"
    >
      <span class="min-w-0 flex-1">{error}</span>
      <Button
        variant="ghost"
        class="text-destructive-text h-11 shrink-0 rounded-full md:h-7"
        onclick={() => (error = null)}>Dismiss</Button
      >
    </div>
  {/if}
{/snippet}

{#if compact}
  {#if compactDetail && selectedTrack && banner && original && guess}
    <!-- ── Phone: the pushed detail, a grouped form ──────────────────────────────── -->
    <div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
      <div
        bind:this={detailScroller}
        class="min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)"
      >
        <!-- "5 of 10" alone says nothing about where you are; the queue's name rides under it
             (and follows the heading for VoiceOver). -->
        <PageToolbarV2
          title="{position.position} of {position.total}"
          titleLabel="Tag review"
          meta="Tag review"
          largeTitle={false}
          grouped
          actions={detailChevrons}
          more={detailMore}
        />
        <div class="flex flex-col gap-7 pt-3 pb-6">
          <GroupedList.Section>
            <div class="flex items-center gap-3 px-4 py-3">
              <Cover
                artist={original.subtitle || 'Unknown'}
                title={original.title}
                coverUrl={coverUrlForSong(selectedTrack)}
                size={60}
                corner={6}
                caption={false}
                dprCap={3}
              />
              <div class="min-w-0 flex-1">
                <h2
                  class={cn(
                    'text-headline line-clamp-2 break-words',
                    original.titleFromFilename && 'font-mono'
                  )}
                >
                  {original.title}
                </h2>
                {#if original.subtitle}
                  <p class="text-subheadline text-muted-foreground truncate">
                    {original.subtitle}
                  </p>
                {/if}
                <p class="text-subheadline mt-0.5 flex items-center gap-1.5">
                  {@render statusDot(banner.tone)}
                  {banner.title}
                </p>
              </div>
            </div>
            {#snippet footer()}
              <!-- The status explanation is a hover title on desktop; a finger never hovers,
                     so here it is simply said. -->
              <p>{banner.body}</p>
              {#if guess.title && guess.title !== original.title}
                <p class="mt-1">
                  Best guess: <span class="text-foreground">{guess.title}</span>{guess.subtitle
                    ? ' · ' + guess.subtitle
                    : ''}
                </p>
              {/if}
              <p class="mt-1 font-mono break-all">{original.fileName}</p>
            {/snippet}
          </GroupedList.Section>

          {#if error}
            <div class="mx-4">{@render errorNote()}</div>
          {/if}

          <GroupedList.Section
            header="Candidates"
            footer="Pick a provider’s answer, or edit the tags below."
          >
            <CandidateGrid
              layout="rows"
              {candidates}
              pickedKey={pickedKey[selectedTrack.id] ?? null}
              loading={detailLoading[selectedTrack.id]}
              onpick={pickCandidate}
            />
          </GroupedList.Section>

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
      </div>

      <!-- At larger text sizes the secondary actions fall back to glyphs (their words stay for
           VoiceOver) — see InboxDecisionBar. -->
      <InboxDecisionBar label="Review decision">
        <Button
          variant="ghost"
          class="text-destructive-text hover:text-destructive-text text-body h-11 rounded-full px-4 in-data-tight:px-3"
          onclick={handleReject}
          disabled={actionLoading}
        >
          <X class="hidden size-5 in-data-tight:block" aria-hidden="true" />
          <span class="in-data-tight:sr-only">Reject</span>
        </Button>
        <Button
          variant="ghost"
          class="text-body h-11 rounded-full px-4 in-data-tight:px-3"
          onclick={handleSkip}
          disabled={actionLoading}
        >
          <SkipForward class="hidden size-5 in-data-tight:block" aria-hidden="true" />
          <span class="in-data-tight:sr-only">Skip</span>
        </Button>
        <Button
          class="text-headline ml-auto h-11 gap-1.5 rounded-full px-5 in-data-tight:min-w-0 in-data-tight:shrink"
          onclick={handleAccept}
          disabled={actionLoading}
        >
          {#if actionLoading}<Loader2 class="size-5 animate-spin" />{:else}<Check
              class="size-5"
              strokeWidth={2.5}
            />{/if}
          <span class="truncate">Accept</span>
        </Button>
      </InboxDecisionBar>
    </div>
  {:else}
    <!-- ── Phone: the queue list ─────────────────────────────────────────────────── -->
    <div class="flex min-h-0 flex-1 flex-col">
      <div
        bind:this={listScroller}
        class="min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)"
      >
        <PageToolbarV2 title="Tag review" meta={queueMeta} more={listMore} />
        {#if loading || tracks.length === 0}
          {@render queueStates()}
        {:else}
          {#if error}
            <div class="px-4 pb-2">{@render errorNote()}</div>
          {/if}
          {@render queueList()}
        {/if}
      </div>
    </div>
  {/if}
{:else}
  <!-- ── Desktop: list pane + detail pane ──────────────────────────────────────────── -->
  <div class="flex min-h-0 flex-1 flex-col">
    <PageToolbarV2
      title="Tag review"
      meta={queueMeta}
      actions={tracks.length > 0 ? desktopActions : undefined}
    />
    {#if !loading && tracks.length === 0}
      <div class="min-h-0 flex-1 overflow-y-auto pb-(--mh-content-pad)">
        {@render queueStates()}
      </div>
    {:else}
      <!-- The list pane gives up width first (down to 240px) so a narrow window — iPad portrait,
           a laptop with the sidebar open — still leaves the detail room for its actions. -->
      <div
        class="grid min-h-0 flex-1 grid-cols-[clamp(240px,30%,320px)_minmax(0,1fr)] overflow-hidden"
      >
        <aside
          aria-label="Tracks awaiting review"
          class="border-separator bg-surface-sunken flex min-h-0 flex-col border-r"
        >
          <div
            class="min-h-0 flex-1 overflow-y-auto px-1.5 pb-[calc(0.375rem_+_var(--mh-content-pad))]"
          >
            {#if loading}
              {@render queueStates()}
            {:else}
              {@render queueList()}
            {/if}
          </div>
        </aside>

        {#if selectedTrack && banner && original && guess}
          <!-- The pane lays itself out by its own width (a container), not the window's: under
               36rem the header's buttons and the action bar wrap onto their own rows instead of
               being cut off by the pane's overflow. -->
          <div class="@container flex min-h-0 min-w-0 flex-col overflow-hidden">
            <!-- Header — status is a small inline dot + label beside the title, not a banner. -->
            <div
              class="border-separator flex flex-wrap items-start gap-x-4 gap-y-3 border-b px-4 py-4 @min-[36rem]:px-6"
            >
              <Cover
                artist={original.subtitle || 'Unknown'}
                title={original.title}
                coverUrl={coverUrlForSong(selectedTrack)}
                size={52}
                corner={8}
                caption={false}
              />
              <div class="min-w-0 flex-1">
                <div class="text-muted-foreground text-[11px]">Original file</div>
                <div class="flex min-w-0 flex-wrap items-center gap-x-2.5 gap-y-0.5">
                  <h2
                    class={cn(
                      'my-0.5 max-w-full min-w-0 truncate text-[20px] font-semibold tracking-tight',
                      original.titleFromFilename && 'font-mono text-[16px]'
                    )}
                  >
                    {original.title}
                  </h2>
                  <span
                    class="text-muted-foreground flex shrink-0 items-center gap-1.5 text-[12px]"
                    title={banner.body}
                  >
                    {@render statusDot(banner.tone)}
                    {banner.title}
                  </span>
                </div>
                {#if original.subtitle}<div class="text-muted-foreground truncate text-[12.5px]">
                    {original.subtitle}
                  </div>{/if}
                <div class="text-muted-foreground-dim truncate font-mono text-[11px]">
                  {original.fileName}
                </div>
                {#if guess.title && guess.title !== original.title}
                  <div class="text-muted-foreground mt-1.5 truncate text-[12px]">
                    Best guess: <span class="text-foreground">{guess.title}</span>{guess.subtitle
                      ? ' · ' + guess.subtitle
                      : ''}
                  </div>
                {/if}
              </div>
              <div class="flex w-full flex-wrap items-center gap-2 @min-[36rem]:w-auto">
                <Button
                  variant="outline"
                  size="sm"
                  class="gap-1.5"
                  onclick={() => onCopyDossier(selectedTrack.id)}
                >
                  <Copy class="size-3.5" /> Copy dossier
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  class="gap-1.5"
                  href={`/track/${selectedTrack.id}`}
                >
                  <History class="size-3.5" /> View timeline
                </Button>
              </div>
            </div>

            {#if error}
              <div class="mx-4 mt-3 @min-[36rem]:mx-6">{@render errorNote()}</div>
            {/if}

            <!-- Scrollable body: candidates + before/after diff -->
            <div class="min-h-0 flex-1 space-y-4 overflow-y-auto px-4 py-4 @min-[36rem]:px-6">
              <div class="flex items-baseline gap-2">
                <span class="text-foreground text-[13px] font-semibold">Candidates</span>
                <span class="text-muted-foreground text-[11.5px]"
                  >Pick a provider's answer, or override fields below.</span
                >
              </div>
              <CandidateGrid
                {candidates}
                pickedKey={pickedKey[selectedTrack.id] ?? null}
                loading={detailLoading[selectedTrack.id]}
                onpick={pickCandidate}
              />

              <div class="flex items-baseline gap-2 pt-1">
                <span class="text-foreground text-[13px] font-semibold">Field diff</span>
                <span class="text-muted-foreground text-[11.5px]"
                  >Embedded tags → what we'll write.</span
                >
              </div>
              <!-- A narrow pane gets the grouped form; on this white pane its cells take the fill
                   the path cards use, or the group would not show. -->
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
                cellClass="bg-muted"
              />
            </div>

            <!-- Action bar — keyboard shortcuts live in a "?" tooltip. It is the last thing in a
                 full-height column, so it carries the mini player's clearance (--mh-content-pad,
                 88px on desktop while the player shows) as its own bottom padding. -->
            <div
              class="border-separator bg-background flex flex-wrap items-center gap-x-3 gap-y-2 border-t px-4 pt-3 pb-[calc(0.75rem_+_var(--mh-content-pad))] @min-[36rem]:px-6"
            >
              <div class="flex items-center">
                <Tooltip.Provider delayDuration={150}>
                  <Tooltip.Root>
                    <Tooltip.Trigger
                      class="border-border text-muted-foreground hover:bg-accent hover:text-foreground grid size-7 place-items-center rounded-full border text-[12px] font-medium transition-colors"
                      aria-label="Keyboard shortcuts">?</Tooltip.Trigger
                    >
                    <Tooltip.Content side="top" align="start">
                      ⇧A accept · S skip · ⇧R reject · ← → navigate
                    </Tooltip.Content>
                  </Tooltip.Root>
                </Tooltip.Provider>
              </div>
              <!-- The phone's order and words: the destructive action first, the prominent one
                   last. -->
              <div class="ml-auto flex flex-wrap items-center justify-end gap-2">
                <Button
                  variant="outline"
                  onclick={handleReject}
                  disabled={actionLoading}
                  class="text-destructive-text hover:text-destructive-text gap-1.5"
                >
                  {#if actionLoading}<Loader2 class="size-3.5 animate-spin" />{:else}<X
                      class="size-3.5"
                    />{/if}
                  Reject
                </Button>
                <Button variant="outline" onclick={handleSkip} disabled={actionLoading}>Skip</Button
                >
                <Button onclick={handleAccept} disabled={actionLoading} class="gap-1.5">
                  {#if actionLoading}<Loader2 class="size-3.5 animate-spin" />{:else}<Check
                      class="size-3.5"
                      strokeWidth={2}
                    />{/if}
                  Accept
                </Button>
              </div>
            </div>
          </div>
        {/if}
      </div>
    {/if}
  </div>
{/if}

<!-- Bulk approve: a choice with consequences, so a sheet with the thresholds as rows (each with
     its live count) and the confirming action trailing — not an alert with chips. A centred
     dialog on desktop, the same content. -->
<BottomSheet.Root
  bind:open={bulkOpen}
  title="Bulk approve"
  description="Approves every track whose top match is at or above the chosen confidence, applying its winning candidate. This writes to the library and can’t be undone in bulk."
>
  {#snippet leading()}
    <BottomSheet.Action disabled={actionLoading} onclick={() => (bulkOpen = false)}
      >Cancel</BottomSheet.Action
    >
  {/snippet}
  {#snippet trailing()}
    <BottomSheet.Action
      prominent
      disabled={actionLoading || bulkAffectedCount === 0}
      onclick={() => void handleBulkApprove()}
    >
      {#if actionLoading}<Loader2 class="size-4 animate-spin" />{/if}
      Approve {bulkAffectedCount}
    </BottomSheet.Action>
  {/snippet}
  <GroupedList.Section
    header="Minimum match confidence"
    footer="Approves {bulkAffectedCount} of {tracks.length} tracks at ≥{Math.round(
      bulkThreshold * 100
    )}% confidence."
  >
    <!-- One threshold at a time: a radio group, not toggle buttons. -->
    <div role="radiogroup" aria-label="Minimum match confidence" use:radioGroup>
      {#each BULK_THRESHOLDS as t, ti (t)}
        {@const chosen = bulkThreshold === t}
        {@const n = countAtOrAbove(t)}
        <GroupedList.Row
          onclick={() => (bulkThreshold = t)}
          role="radio"
          aria-checked={chosen}
          tabindex={radioTabIndex(
            ti,
            BULK_THRESHOLDS.indexOf(bulkThreshold as (typeof BULK_THRESHOLDS)[number])
          )}
          label="{Math.round(t * 100)}% or higher"
          value="{n} track{n === 1 ? '' : 's'}"
        >
          {#snippet trailing()}
            <Check
              class={cn('text-primary size-5', !chosen && 'invisible')}
              strokeWidth={2.5}
              aria-hidden="true"
            />
          {/snippet}
        </GroupedList.Row>
      {/each}
    </div>
  </GroupedList.Section>
</BottomSheet.Root>

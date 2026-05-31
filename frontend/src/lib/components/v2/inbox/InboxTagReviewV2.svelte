<script lang="ts">
  import { untrack } from 'svelte';
  import { Check, X, ChevronLeft, ChevronRight, Loader2, RefreshCw, History } from '@lucide/svelte';
  import { page } from '$app/state';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import type { ApiSong, EnrichmentDetail } from '$lib/api-client';
  import {
    fetchReviewQueue,
    fetchEnrichmentDetail,
    submitManualReview
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

  const PILL_TINT: Record<'warn' | 'info' | 'err', string> = {
    warn: 'bg-amber-500/15 text-amber-600 dark:text-amber-400',
    info: 'bg-primary/15 text-primary',
    err: 'bg-red-500/15 text-red-600 dark:text-red-400'
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

  // Report the live count up to the parent Inbox tab strip. The callback is
  // invoked via untrack() so the effect depends only on `loading`/`tracks` — not
  // on the `oncount` prop's identity. The parent passes a fresh inline arrow on
  // every render, and tracking it here would re-run this effect each time the
  // parent re-renders, which (because the parent reassigns its counts object on
  // every call) is a self-sustaining loop → effect_update_depth_exceeded.
  $effect(() => {
    const n = loading ? null : tracks.length;
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
      for (const t of tracks) void loadDetail(t.id);
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
    if (selectedId != null) void loadDetail(selectedId);
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

  function selectAt(delta: number) {
    if (tracks.length === 0) return;
    const idx = tracks.findIndex((t) => t.id === selectedId);
    const next = Math.max(0, Math.min(tracks.length - 1, (idx < 0 ? 0 : idx) + delta));
    selectedId = tracks[next].id;
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
        <span class="text-muted-foreground text-[11px]">{tracks.length} awaiting review</span>
        <button
          type="button"
          onclick={loadQueue}
          title="Refresh"
          class="text-muted-foreground hover:bg-accent hover:text-foreground grid size-7 place-items-center rounded-md transition-colors"
        >
          <RefreshCw class="size-3.5" />
        </button>
      </div>
      <div class="min-h-0 flex-1 overflow-y-auto p-1.5">
        {#each tracks as track (track.id)}
          {@const r = reasonFor(track)}
          {@const info = rowOriginal(track)}
          <button
            type="button"
            onclick={() => (selectedId = track.id)}
            class={cn(
              'mb-0.5 flex w-full items-center gap-2.5 rounded-md border-l-2 border-transparent py-2 pr-2.5 pl-2 text-left transition-colors',
              selectedId === track.id ? 'border-l-primary bg-card' : 'hover:bg-accent'
            )}
          >
            <Cover artist={track.artist ?? 'Unknown'} title={info.title} size={40} corner={6} caption={false} />
            <div class="min-w-0 flex-1">
              <div class={cn('truncate text-[13px] font-medium', info.titleFromFilename && 'font-mono text-[12px]')}>{info.title}</div>
              <div class="text-muted-foreground truncate text-[11.5px]">{info.subtitle || '—'}</div>
            </div>
            <span class={cn('shrink-0 rounded px-1.5 py-0.5 text-[9px] font-bold tracking-wide uppercase', PILL_TINT[r.tint])}>{r.label}</span>
          </button>
        {/each}
      </div>
    </aside>

    <!-- Detail — single-pane on mobile: hidden until a track is selected. -->
    {#if selectedTrack && banner && original && guess}
      <div
        class="flex min-h-0 min-w-0 flex-col overflow-hidden md:flex"
        class:hidden={selectedId == null}
      >
        <!-- Banner -->
        <div
          class={cn(
            'flex items-center gap-3 px-4 py-3 sm:px-6',
            banner.tone === 'warn' && 'bg-amber-500/10',
            banner.tone === 'info' && 'bg-primary/10',
            banner.tone === 'err' && 'bg-red-500/10',
            banner.tone === 'ok' && 'bg-primary/10'
          )}
        >
          <span
            class={cn(
              'grid size-8 shrink-0 place-items-center rounded-full text-white',
              banner.tone === 'warn' && 'bg-amber-500',
              banner.tone === 'info' && 'bg-primary',
              banner.tone === 'err' && 'bg-red-500',
              banner.tone === 'ok' && 'bg-primary'
            )}
          >
            {#if banner.tone === 'ok'}<Check class="size-4" strokeWidth={2.5} />{:else}<X class="size-4" strokeWidth={2.5} />{/if}
          </span>
          <div class="min-w-0 flex-1">
            <div class="text-[14px] font-semibold">{banner.title}</div>
            <div class="text-muted-foreground text-[12px]">{banner.body}</div>
          </div>
        </div>

        <!-- Header -->
        <div class="border-border flex items-start gap-4 border-b px-4 pt-3 pb-4 sm:px-6">
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
            <div class="text-muted-foreground font-mono text-[10px] tracking-[0.1em]">ORIGINAL</div>
            <h2 class={cn('my-0.5 truncate text-[20px] font-semibold tracking-tight', original.titleFromFilename && 'font-mono text-[16px]')}>{original.title}</h2>
            {#if original.subtitle}<div class="text-muted-foreground truncate text-[12.5px]">{original.subtitle}</div>{/if}
            <div class="text-muted-foreground/70 truncate font-mono text-[11px]">{original.fileName}</div>
            {#if guess.title && guess.title !== original.title}
              <div class="text-muted-foreground/80 mt-1.5 truncate text-[12px]">
                <span class="font-mono text-[10px] tracking-[0.08em]">BEST GUESS →</span>
                {guess.title}{guess.subtitle ? ' · ' + guess.subtitle : ''}
              </div>
            {/if}
          </div>
          <a
            href={`/track/${selectedTrack.id}`}
            class="border-border bg-card hover:bg-muted text-foreground inline-flex shrink-0 items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12px] font-medium transition-colors"
          >
            <History class="size-3.5" /> View timeline
          </a>
        </div>

        {#if error}
          <div class="border-destructive/50 bg-destructive/10 text-destructive mx-4 mt-3 rounded-lg border p-3 text-sm sm:mx-6">
            {error}
            <Button variant="ghost" size="sm" class="ml-2" onclick={() => (error = null)}>Dismiss</Button>
          </div>
        {/if}

        <!-- Scrollable body: candidates + before/after diff -->
        <div class="min-h-0 flex-1 space-y-4 overflow-y-auto px-4 py-4 sm:px-6">
          <div class="text-muted-foreground flex items-baseline gap-2">
            <span class="text-[12px] font-semibold tracking-wide uppercase">Candidates</span>
            <span class="text-[11.5px]">Pick a provider's answer, or override fields below.</span>
          </div>
          <CandidateGrid {candidates} pickedKey={pickedKey[selectedTrack.id] ?? null} loading={detailLoading[selectedTrack.id]} onpick={pickCandidate} single={isMobile.current} />

          <div class="text-muted-foreground flex items-baseline gap-2 pt-1">
            <span class="text-[12px] font-semibold tracking-wide uppercase">Field diff</span>
            <span class="text-[11.5px]">Embedded tags → what we'll write.</span>
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

        <!-- Action bar -->
        <div class="border-border bg-background flex flex-wrap items-center gap-2 border-t px-4 py-3 sm:gap-3 sm:px-6">
          <div class="text-muted-foreground hidden flex-1 items-center gap-1 text-[11px] lg:flex">
            <b class="text-foreground/80 mr-1">Keys</b>
            <kbd class="bg-muted rounded border px-1.5 py-px font-mono text-[10px]">A</kbd> accept
            <kbd class="bg-muted ml-1 rounded border px-1.5 py-px font-mono text-[10px]">S</kbd> skip
            <kbd class="bg-muted ml-1 rounded border px-1.5 py-px font-mono text-[10px]">R</kbd> reject
            <kbd class="bg-muted ml-1 rounded border px-1.5 py-px font-mono text-[10px]">←</kbd>
            <kbd class="bg-muted rounded border px-1.5 py-px font-mono text-[10px]">→</kbd> nav
          </div>
          <div class="flex flex-1 items-center justify-end gap-2 lg:flex-none">
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

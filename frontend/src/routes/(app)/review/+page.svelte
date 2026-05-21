<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Label } from '$lib/components/ui/label';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import {
    Check,
    X,
    ChevronRight,
    Plus,
    Play,
    Pause,
    RefreshCw,
    Loader2,
    CheckCheck,
    Trash2
  } from '@lucide/svelte';
  import type { ApiSong, EnrichmentDetail } from '$lib/api-client';
  import {
    fetchReviewTracks,
    fetchEnrichmentDetail,
    submitManualReview,
    softDeleteSong,
    bulkApprove,
    getSongStreamUrl
  } from '$lib/api-client';
  import {
    reasonFor,
    candidatesFromDetail,
    embeddedTags,
    buildDestinationPath,
    fingerprintBars,
    fingerprintHash,
    type ReviewCandidate
  } from '$lib/review-helpers';
  import { formatDuration, formatFileSize } from '$lib/formatters';
  import { playerStore } from '$lib/stores/player.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import MobileReview from '$lib/components/mobile/MobileReview.svelte';
  import { cn } from '$lib/utils';

  const isMobile = new IsMobile();

  type MetadataEdits = { title?: string; artist?: string; album?: string; year?: number };
  type Decision = 'accept' | 'reject' | 'skip';

  let tracks = $state<ApiSong[]>([]);
  let selectedIndex = $state(0);
  let editedMetadata = $state<Record<number, MetadataEdits>>({});
  let pickedKey = $state<Record<number, string>>({});
  let decisions = $state<Record<number, Decision>>({});
  let details = $state<Record<number, EnrichmentDetail>>({});
  let detailLoading = $state<Record<number, boolean>>({});

  let loading = $state(true);
  let actionLoading = $state(false);
  let error = $state<string | null>(null);
  let rejectReason = $state('');
  let bulkApproveMinConfidence = $state(0.75);
  let bulkApproveResult = $state<{ count: number } | null>(null);

  const PILL_TINT: Record<'warn' | 'info' | 'err', string> = {
    warn: 'bg-amber-500/15 text-amber-600 dark:text-amber-400',
    info: 'bg-primary/15 text-primary',
    err: 'bg-red-500/15 text-red-600 dark:text-red-400'
  };

  async function loadTracks() {
    try {
      loading = true;
      error = null;
      tracks = await fetchReviewTracks();
      selectedIndex = 0;
      editedMetadata = {};
      pickedKey = {};
      decisions = {};
      details = {};
      detailLoading = {};
      // Background-prefetch candidate detail for each queue item so the sidebar
      // can show each row's top guess (and the selected item is ready instantly).
      for (const track of tracks) void loadDetail(track.id);
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load tracks';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    if (isMobile.current) return;
    void loadTracks();
  });

  const selectedTrack = $derived(tracks[selectedIndex]);
  const selectedDetail = $derived(selectedTrack ? (details[selectedTrack.id] ?? null) : null);
  const candidates = $derived(candidatesFromDetail(selectedDetail));

  function seedFields(track: ApiSong, top: ReviewCandidate | undefined): MetadataEdits {
    if (top) {
      return {
        title: top.fields.title,
        artist: top.fields.artist,
        album: top.fields.album,
        year: top.fields.year ? parseInt(top.fields.year, 10) : undefined
      };
    }
    return {
      title: track.title ?? '',
      artist: track.artist ?? '',
      album: track.album ?? '',
      year: track.year ?? undefined
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
        if (track) editedMetadata = { ...editedMetadata, [id]: seedFields(track, cands[0]) };
        if (cands[0]) pickedKey = { ...pickedKey, [id]: cands[0].key };
      }
    } catch {
      // detail is optional — the form still works from embedded tags
    } finally {
      detailLoading = { ...detailLoading, [id]: false };
    }
  }

  $effect(() => {
    const track = selectedTrack;
    if (!track) return;
    void loadDetail(track.id);
  });

  function selectTrack(index: number) {
    selectedIndex = index;
    rejectReason = '';
  }

  function pickCandidate(c: ReviewCandidate) {
    const id = selectedTrack?.id;
    if (id == null) return;
    pickedKey = { ...pickedKey, [id]: c.key };
    editedMetadata = {
      ...editedMetadata,
      [id]: {
        title: c.fields.title,
        artist: c.fields.artist,
        album: c.fields.album,
        year: c.fields.year ? parseInt(c.fields.year, 10) : undefined
      }
    };
  }

  function setField(field: keyof MetadataEdits, value: string) {
    const id = selectedTrack?.id;
    if (id == null) return;
    const next: MetadataEdits = { ...editedMetadata[id] };
    if (field === 'year') {
      const n = parseInt(value, 10);
      next.year = Number.isFinite(n) ? n : undefined;
    } else {
      next[field] = value;
    }
    editedMetadata = { ...editedMetadata, [id]: next };
  }

  function fieldValue(field: keyof MetadataEdits): string {
    const id = selectedTrack?.id;
    if (id == null) return '';
    const v = editedMetadata[id]?.[field];
    return v == null ? '' : String(v);
  }

  const formFields = $derived<MetadataEdits>(
    selectedTrack ? (editedMetadata[selectedTrack.id] ?? {}) : {}
  );

  const decidedCount = $derived(Object.values(decisions).filter(Boolean).length);

  function advanceToNextUndecided(fromId: number) {
    const next = tracks.find((t) => t.id !== fromId && !decisions[t.id]);
    if (next) selectedIndex = tracks.indexOf(next);
  }

  function buildOverrides(id: number) {
    const edits = editedMetadata[id];
    if (!edits) return {};
    const out: MetadataEdits = {};
    if (edits.title !== undefined) out.title = edits.title;
    if (edits.artist !== undefined) out.artist = edits.artist;
    if (edits.album !== undefined) out.album = edits.album;
    if (edits.year !== undefined) out.year = edits.year;
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
      await submitManualReview(track.id, {
        decision: 'reject',
        rejectReason: rejectReason || undefined
      });
      decisions = { ...decisions, [track.id]: 'reject' };
      rejectReason = '';
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

  async function handleDelete() {
    const track = selectedTrack;
    if (!track || actionLoading) return;
    try {
      actionLoading = true;
      error = null;
      await softDeleteSong(track.id);
      const removedId = track.id;
      const next = tracks.filter((t) => t.id !== removedId);
      tracks = next;
      if (selectedIndex >= next.length && selectedIndex > 0) selectedIndex = next.length - 1;
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
      if (result.approvedCount > 0) await loadTracks();
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to bulk approve';
    } finally {
      actionLoading = false;
    }
  }

  const eligibleForBulk = $derived(
    tracks.filter((t) => t.matchConfidence != null && t.matchConfidence >= bulkApproveMinConfidence)
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
    void playerStore.playSong({
      id: track.id,
      title: (track.title ?? track.fileName ?? 'Unknown').trim() || 'Unknown',
      artist: (track.artist ?? 'Unknown Artist').trim() || 'Unknown Artist',
      streamUrl: getSongStreamUrl(track.id)
    });
  }

  function formatClock(iso: string | null | undefined): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    return d.toLocaleTimeString([], {
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      hour12: false
    });
  }

  const flaggedAt = $derived(
    selectedDetail?.providerAttempts?.[0]?.attemptedAtUtc
      ? formatClock(selectedDetail.providerAttempts[0].attemptedAtUtc)
      : ''
  );
  const selectedReason = $derived(selectedTrack ? reasonFor(selectedTrack) : null);
  const formatLabel = $derived((selectedTrack?.extension ?? '').replace(/^\./, '').toUpperCase() || '—');
</script>

{#if isMobile.current}
  <MobileReview />
{:else if loading}
  <main class="flex flex-1 items-center justify-center p-4">
    <div class="flex flex-col items-center gap-4">
      <Loader2 class="text-primary size-8 animate-spin" />
      <p class="text-muted-foreground">Loading review queue…</p>
    </div>
  </main>
{:else if error && tracks.length === 0}
  <main class="flex flex-1 items-center justify-center p-4">
    <div class="max-w-md text-center">
      <div class="bg-destructive/10 mx-auto mb-4 flex size-16 items-center justify-center rounded-full">
        <X class="text-destructive size-8" />
      </div>
      <h2 class="mb-2 text-xl font-semibold">Error</h2>
      <p class="text-muted-foreground mb-4">{error}</p>
      <Button onclick={loadTracks}>Retry</Button>
    </div>
  </main>
{:else if tracks.length === 0}
  <main class="flex flex-1 items-center justify-center p-4">
    <div class="max-w-md text-center">
      <div class="bg-primary/10 mx-auto mb-4 flex size-16 items-center justify-center rounded-full">
        <Check class="text-primary size-8" />
      </div>
      <h2 class="mb-2 text-xl font-semibold">All clear</h2>
      <p class="text-muted-foreground mb-4">No items in the review queue.</p>
      <div class="flex justify-center gap-2">
        <Button variant="outline" onclick={loadTracks}>
          <RefreshCw class="mr-2 size-4" />
          Refresh
        </Button>
        <Button href="/runs">Back to Runs</Button>
      </div>
    </div>
  </main>
{:else}
  <main class="grid min-h-0 flex-1 grid-cols-[320px_1fr] overflow-hidden">
    <!-- Queue list -->
    <aside class="bg-surface-sunken border-border flex min-h-0 flex-col border-r">
      <div class="border-border flex items-center justify-between gap-2 border-b px-[18px] py-3">
        <div class="min-w-0">
          <div class="text-sm font-semibold">Review queue</div>
          <div class="text-muted-foreground mt-1 flex items-center gap-1.5 text-[11.5px]">
            <span>{tracks.length} items</span>
            <span>·</span>
            <span class="font-mono">{decidedCount} decided</span>
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
          <Button variant="ghost" size="icon" class="size-8" onclick={loadTracks} title="Refresh">
            <RefreshCw class="size-4" />
          </Button>
        </div>
      </div>

      <div class="min-h-0 flex-1 overflow-y-auto p-1.5">
        {#each tracks as track, index (track.id)}
          {@const r = reasonFor(track)}
          {@const d = decisions[track.id]}
          {@const top = candidatesFromDetail(details[track.id])[0]}
          <button
            class={cn(
              'mb-0.5 flex w-full items-start gap-2 rounded-md border border-transparent p-2.5 text-left transition-colors',
              selectedIndex === index ? 'bg-card border-border' : 'hover:bg-accent',
              d === 'accept' && 'opacity-60',
              d === 'reject' && 'opacity-45',
              d === 'skip' && 'opacity-60'
            )}
            onclick={() => selectTrack(index)}
          >
            <div class="min-w-0 flex-1">
              <div class="mb-1 flex flex-wrap items-center gap-2">
                <span
                  class={cn(
                    'rounded px-1.5 py-0.5 text-[9px] font-bold tracking-wide uppercase',
                    PILL_TINT[r.tint]
                  )}>{r.label}</span>
                {#if track.matchConfidence != null}
                  <span class="text-muted-foreground font-mono text-[11px]">
                    {track.matchConfidence.toFixed(2)}
                  </span>
                {/if}
              </div>
              <div class="truncate text-[11.5px]">{track.fileName}</div>
              <div class="text-muted-foreground mt-0.5 truncate text-[11px]">
                {#if top}{top.artist} — {top.title}{:else}no candidates{/if}
              </div>
            </div>
            {#if d}
              <span
                class={cn(
                  'mt-0.5 grid size-[18px] shrink-0 place-items-center rounded-full text-white',
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
          </button>
        {/each}
      </div>
    </aside>

    <!-- Detail -->
    {#if selectedTrack}
      <div class="flex min-w-0 flex-col overflow-hidden">
        <div class="border-border flex items-end justify-between gap-6 border-b px-7 py-5">
          <div class="min-w-0">
            <div class="text-muted-foreground font-mono text-[10px] tracking-[0.1em]">
              REVIEW · {selectedIndex + 1} of {tracks.length}{#if flaggedAt} · flagged {flaggedAt}{/if}
            </div>
            <h1 class="my-1.5 text-[22px] font-semibold tracking-tight break-all">
              {selectedTrack.fileName}
            </h1>
            <div class="text-muted-foreground font-mono text-[11px]">{selectedTrack.sourcePath}</div>
          </div>
          <div class="grid shrink-0 grid-cols-4 gap-[18px]">
            <div class="text-right">
              <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.08em]">DURATION</div>
              <div class="mt-0.5 text-[13px] font-medium">{formatDuration(selectedTrack.durationSeconds)}</div>
            </div>
            <div class="text-right">
              <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.08em]">SIZE</div>
              <div class="mt-0.5 text-[13px] font-medium">{formatFileSize(selectedTrack.fileSizeBytes)}</div>
            </div>
            <div class="text-right">
              <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.08em]">FORMAT</div>
              <div class="mt-0.5 text-[13px] font-medium">{formatLabel}</div>
            </div>
            <div class="text-right">
              <div class="text-muted-foreground font-mono text-[9.5px] font-semibold tracking-[0.08em]">REASON</div>
              <div class="mt-0.5 text-[13px] font-medium text-amber-600 dark:text-amber-400">
                {selectedReason?.label ?? '—'}
              </div>
            </div>
          </div>
        </div>

        {#if error}
          <div class="border-destructive/50 bg-destructive/10 text-destructive m-4 mb-0 rounded-lg border p-3 text-sm">
            {error}
            <Button variant="ghost" size="sm" class="ml-2" onclick={() => (error = null)}>Dismiss</Button>
          </div>
        {/if}

        <div class="bg-border grid min-h-0 flex-1 grid-cols-3 gap-px">
          <!-- Candidate matches -->
          <div class="bg-background overflow-y-auto px-[22px] py-[18px]">
            <div class="text-muted-foreground mb-3 flex items-center justify-between text-[10.5px] font-semibold tracking-[0.06em] uppercase">
              <span>Candidate matches</span>
              <span class="font-mono">{candidates.length}</span>
            </div>
            {#if detailLoading[selectedTrack.id] && candidates.length === 0}
              <div class="text-muted-foreground flex items-center gap-2 py-2 text-sm">
                <Loader2 class="size-4 animate-spin" /> Loading candidates…
              </div>
            {/if}
            {#if !detailLoading[selectedTrack.id] && candidates.length === 0}
              <div class="bg-surface-sunken rounded-md px-3.5 py-4">
                <div class="text-[12.5px] font-semibold">No fingerprint matches</div>
                <div class="text-muted-foreground mt-1 text-[11.5px] leading-relaxed">
                  AcoustID returned nothing. You can still enter metadata manually below or skip this file.
                </div>
              </div>
            {/if}
            {#each candidates as c (c.key)}
              {@const picked = pickedKey[selectedTrack.id] === c.key}
              <button
                class={cn(
                  'mb-1.5 flex w-full items-start gap-3 rounded-md border p-3 text-left transition-colors',
                  picked ? 'border-primary bg-primary/10' : 'border-border bg-background hover:bg-accent'
                )}
                onclick={() => pickCandidate(c)}
              >
                <div class="w-14 shrink-0">
                  <div class={cn('font-mono text-base font-semibold', picked ? 'text-primary' : '')}>
                    {c.score != null ? c.score.toFixed(2) : '—'}
                  </div>
                  <div class="bg-border mt-1 h-[3px] overflow-hidden rounded-full">
                    <div class="bg-primary h-full" style="width: {Math.round((c.score ?? 0) * 100)}%"></div>
                  </div>
                </div>
                <div class="min-w-0 flex-1">
                  <div class="text-[13px] font-medium">{c.title}</div>
                  <div class="text-muted-foreground mt-0.5 text-[11.5px]">
                    {c.artist} · <em class="text-foreground/70 italic">{c.album}</em> · {c.year}
                  </div>
                  <div class="text-muted-foreground mt-1 font-mono text-[10.5px]">{c.source}</div>
                </div>
                {#if picked}
                  <div class="text-primary self-center font-mono text-[9px] font-bold tracking-[0.08em]">PICKED</div>
                {/if}
              </button>
            {/each}
            <button
              class="border-border text-muted-foreground hover:text-primary hover:border-primary mt-1 flex w-full items-center gap-1.5 rounded-md border border-dashed px-3 py-2.5 text-xs"
              type="button"
              title="Manual search isn't available yet"
            >
              <Plus size={11} /> Search MusicBrainz manually
            </button>
          </div>

          <!-- Fingerprint + embedded tags -->
          <div class="bg-background overflow-y-auto px-[22px] py-[18px]">
            <div class="text-muted-foreground mb-3 flex items-center justify-between text-[10.5px] font-semibold tracking-[0.06em] uppercase">
              <span>Audio fingerprint</span>
              <span class="font-mono">Chromaprint v1.5</span>
            </div>
            <div class="bg-surface-sunken flex h-16 items-end gap-0.5 rounded p-1.5">
              {#each fingerprintBars(selectedTrack.fingerprint) as h, i (i)}
                <div
                  class="from-primary flex-1 rounded-[1px] bg-gradient-to-t to-[oklch(0.7_0.1_200)]"
                  style="height: {h}%"
                ></div>
              {/each}
            </div>
            {#if selectedTrack.fingerprint}
              <div class="bg-surface-sunken text-muted-foreground mt-2 rounded p-2.5 font-mono text-[10px] leading-relaxed break-all">
                {fingerprintHash(selectedTrack.fingerprint)}
              </div>
            {/if}

            <div class="text-muted-foreground mt-4 mb-3 flex items-center justify-between text-[10.5px] font-semibold tracking-[0.06em] uppercase">
              <span>Embedded tags</span>
              <span class="font-mono">id3v2.4</span>
            </div>
            <div class="bg-surface-sunken rounded-md px-3 py-2">
              {#each embeddedTags(selectedTrack) as tag (tag.key)}
                <div class="grid grid-cols-[70px_1fr] items-baseline gap-2.5 py-0.5 text-xs">
                  <span class="text-muted-foreground font-mono text-[10.5px]">{tag.key}</span>
                  <span class="truncate">
                    {#if tag.value}{tag.value}{:else}<em class="text-muted-foreground/60 text-[11px]">(empty)</em>{/if}
                  </span>
                </div>
              {/each}
            </div>

            <Button variant="outline" class="mt-3.5 w-full gap-1.5" onclick={handlePlayPause}>
              {#if isPlayingSelected}<Pause class="size-3.5" />Pause{:else}<Play class="size-3.5" />Preview audio{/if}
            </Button>
          </div>

          <!-- Final metadata -->
          <div class="bg-background overflow-y-auto px-[22px] py-[18px]">
            <div class="text-muted-foreground mb-3 flex items-center justify-between text-[10.5px] font-semibold tracking-[0.06em] uppercase">
              <span>Final metadata</span>
              <span class="font-mono">writes to file</span>
            </div>
            <div class="flex flex-col gap-3">
              {#each [['title', 'Title'], ['artist', 'Artist'], ['album', 'Album'], ['year', 'Year']] as [field, label] (field)}
                <div class="flex flex-col gap-1">
                  <Label class="text-[10.5px] font-semibold tracking-[0.04em]">{label}</Label>
                  <Input
                    value={fieldValue(field as keyof MetadataEdits)}
                    type={field === 'year' ? 'number' : 'text'}
                    placeholder={`enter ${label.toLowerCase()}`}
                    oninput={(e) => setField(field as keyof MetadataEdits, (e.target as HTMLInputElement).value)}
                    class="h-9"
                  />
                </div>
              {/each}
            </div>

            <div class="border-primary bg-primary/10 mt-4 rounded-md border p-3">
              <div class="text-primary text-[9.5px] font-semibold tracking-[0.08em]">DESTINATION</div>
              <div class="mt-1 font-mono text-[11px] leading-relaxed break-all">
                {buildDestinationPath(formFields, selectedTrack.extension)}
              </div>
            </div>

            <div class="mt-4 flex flex-col gap-2 border-t pt-3">
              <Input
                bind:value={rejectReason}
                placeholder="Reject reason (optional)"
                class="h-8"
              />
              <div class="flex items-center gap-2">
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
                        This soft-deletes the track so it is excluded from review and library build. The
                        original file is not deleted.
                      </AlertDialog.Description>
                    </AlertDialog.Header>
                    <AlertDialog.Footer>
                      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
                      <AlertDialog.Action onclick={handleDelete}>Delete</AlertDialog.Action>
                    </AlertDialog.Footer>
                  </AlertDialog.Content>
                </AlertDialog.Root>
                <Button variant="outline" onclick={handleReject} disabled={actionLoading} class="flex-1 gap-1.5">
                  {#if actionLoading}<Loader2 class="size-3.5 animate-spin" />{:else}<X class="size-3.5" />{/if}
                  Reject
                </Button>
                <Button variant="outline" onclick={handleSkip} disabled={actionLoading} class="flex-1">Skip</Button>
              </div>
              <Button onclick={handleAccept} disabled={actionLoading} class="w-full gap-1.5">
                {#if actionLoading}<Loader2 class="size-3.5 animate-spin" />{:else}<Check class="size-3.5" strokeWidth={2} />{/if}
                Accept &amp; write
              </Button>
            </div>
          </div>
        </div>
      </div>
    {/if}
  </main>
{/if}

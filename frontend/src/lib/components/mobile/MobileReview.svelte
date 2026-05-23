<script lang="ts">
  import { onMount } from 'svelte';
  import { Check, X, Play, RefreshCw, Loader2, AlertTriangle, Plus } from '@lucide/svelte';
  import MobileHeader from '$lib/components/mobile/MobileHeader.svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import CandidateGrid from '$lib/components/review/CandidateGrid.svelte';
  import TimelineView from '$lib/components/review/TimelineView.svelte';
  import OriginMatrixView from '$lib/components/review/OriginMatrixView.svelte';
  import {
    fetchReviewQueue,
    fetchEnrichmentDetail,
    submitManualReview,
    enrichSong,
    getSongStreamUrl,
    type ApiSong,
    type EnrichmentDetail
  } from '$lib/api-client';
  import {
    reasonFor,
    candidatesFromDetail,
    bestGuess,
    bannerFor,
    decisionLabel,
    elapsedMs,
    formatElapsed,
    contributedProviders,
    beforeAfterRows,
    buildTimeline,
    buildOriginMatrix,
    EDITABLE_FIELDS,
    type ReviewCandidate,
    type EditableFieldKey
  } from '$lib/review-helpers';
  import { formatFileSize } from '$lib/formatters';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  type Decision = 'accept' | 'reject' | 'skip';
  type QueueFilter = 'needsreview' | 'done' | 'all';
  type ViewKey = 'before' | 'timeline' | 'matrix';
  type MetadataEdits = Partial<Record<EditableFieldKey, string>>;

  let reviewTracks = $state<ApiSong[]>([]);
  let doneTracks = $state<ApiSong[]>([]);
  let queueFilter = $state<QueueFilter>('needsreview');
  let loading = $state(true);
  let openId = $state<number | null>(null);
  let view = $state<ViewKey>('before');
  let decisions = $state<Record<number, Decision>>({});
  let details = $state<Record<number, EnrichmentDetail>>({});
  let detailLoading = $state<Record<number, boolean>>({});
  let pickedKey = $state<Record<number, string>>({});
  let editedMetadata = $state<Record<number, MetadataEdits>>({});
  let busy = $state(false);

  const PILL_TINT: Record<'warn' | 'info' | 'err', string> = {
    warn: 'bg-amber-500/15 text-amber-600 dark:text-amber-400',
    info: 'bg-primary/15 text-primary',
    err: 'bg-red-500/15 text-red-600 dark:text-red-400'
  };
  const BANNER_TONE = {
    warn: 'bg-amber-500/10 text-amber-700 dark:text-amber-400',
    info: 'bg-primary/10 text-primary',
    err: 'bg-red-500/10 text-red-600 dark:text-red-400',
    ok: 'bg-primary/10 text-primary'
  } as const;

  const tracks = $derived(
    queueFilter === 'needsreview'
      ? reviewTracks
      : queueFilter === 'done'
        ? doneTracks
        : [...reviewTracks, ...doneTracks]
  );

  async function load() {
    loading = true;
    try {
      const [review, done] = await Promise.all([
        fetchReviewQueue('needsreview'),
        fetchReviewQueue('matched')
      ]);
      reviewTracks = review;
      doneTracks = [...done]
        .sort(
          (a, b) =>
            new Date(b.libraryBuiltAtUtc ?? b.indexedAtUtc ?? 0).getTime() -
            new Date(a.libraryBuiltAtUtc ?? a.indexedAtUtc ?? 0).getTime()
        )
        .slice(0, 100);
      for (const track of reviewTracks) void loadDetail(track.id);
    } catch {
      reviewTracks = [];
      doneTracks = [];
    } finally {
      loading = false;
    }
  }

  onMount(load);

  const open = $derived(openId != null ? (tracks.find((t) => t.id === openId) ?? null) : null);
  const openDetail = $derived(open ? (details[open.id] ?? null) : null);
  const openCandidates = $derived(candidatesFromDetail(openDetail));
  const openIsReview = $derived(
    (openDetail?.enrichmentStatus ?? String(open?.enrichmentStatus ?? '')).toLowerCase() === 'needsreview'
  );

  async function loadDetail(id: number) {
    if (details[id] || detailLoading[id]) return;
    detailLoading = { ...detailLoading, [id]: true };
    try {
      const detail = await fetchEnrichmentDetail(id);
      details = { ...details, [id]: detail };
      const cands = candidatesFromDetail(detail);
      if (cands[0] && !pickedKey[id]) pickedKey = { ...pickedKey, [id]: cands[0].key };
      if (!editedMetadata[id]) {
        const track = tracks.find((t) => t.id === id);
        const top = cands[0];
        const cur = detail.current;
        editedMetadata = {
          ...editedMetadata,
          [id]: {
            title: top?.title ?? cur?.title ?? track?.title ?? '',
            artist: top?.artist ?? cur?.artist ?? track?.artist ?? '',
            album: top?.album ?? cur?.album ?? track?.album ?? '',
            albumArtist: cur?.albumArtist ?? track?.albumArtist ?? '',
            year: top?.year || (cur?.year != null ? String(cur.year) : ''),
            trackNumber: cur?.trackNumber != null ? String(cur.trackNumber) : ''
          }
        };
      }
    } catch {
      // candidates optional
    } finally {
      detailLoading = { ...detailLoading, [id]: false };
    }
  }

  function openItem(id: number) {
    openId = id;
    view = 'before';
    void loadDetail(id);
  }

  function pick(c: ReviewCandidate) {
    if (!open) return;
    pickedKey = { ...pickedKey, [open.id]: c.key };
    editedMetadata = {
      ...editedMetadata,
      [open.id]: {
        ...editedMetadata[open.id],
        title: c.fields.title,
        artist: c.fields.artist,
        album: c.fields.album,
        year: c.fields.year
      }
    };
  }

  function setField(key: EditableFieldKey, value: string) {
    if (!open) return;
    editedMetadata = { ...editedMetadata, [open.id]: { ...editedMetadata[open.id], [key]: value } };
  }

  function play(t: ApiSong) {
    void playerStore.playSong({
      id: t.id,
      title: (t.title ?? t.fileName).trim() || t.fileName,
      artist: (t.artist ?? 'Unknown Artist').trim() || 'Unknown Artist',
      streamUrl: getSongStreamUrl(t.id)
    });
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
      } else out[f.key] = v;
    }
    return out;
  }

  async function decide(action: Decision) {
    const t = open;
    if (!t || busy) return;
    if (action === 'skip') {
      decisions = { ...decisions, [t.id]: 'skip' };
      openId = null;
      return;
    }
    busy = true;
    try {
      if (action === 'accept') {
        await submitManualReview(t.id, { decision: 'approve', ...buildOverrides(t.id) });
      } else {
        await submitManualReview(t.id, { decision: 'reject' });
      }
      decisions = { ...decisions, [t.id]: action };
      reviewTracks = reviewTracks.filter((x) => x.id !== t.id);
      openId = null;
    } catch {
      // leave open on failure
    } finally {
      busy = false;
    }
  }

  async function reenrich() {
    const t = open;
    if (!t || busy) return;
    busy = true;
    try {
      await enrichSong(t.id, true);
      openId = null;
      await load();
    } catch {
      // ignore
    } finally {
      busy = false;
    }
  }

  function rowGuess(track: ApiSong): { title: string; subtitle: string } {
    const d = details[track.id];
    if (d) {
      const g = bestGuess(track, candidatesFromDetail(d), d);
      return { title: g.title, subtitle: g.subtitle };
    }
    return { title: track.title || track.fileName, subtitle: [track.artist, track.album].filter(Boolean).join(' · ') };
  }

  function clock(iso: string | null | undefined): string {
    if (!iso) return '';
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? '' : d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false });
  }

  const FILTERS: { key: QueueFilter; label: string }[] = [
    { key: 'needsreview', label: 'Needs review' },
    { key: 'done', label: 'Done' },
    { key: 'all', label: 'All' }
  ];
  const VIEWS: { key: ViewKey; label: string }[] = [
    { key: 'before', label: 'Before → After' },
    { key: 'timeline', label: 'Timeline' },
    { key: 'matrix', label: 'Origins' }
  ];

  // Derivations for the open detail.
  const guess = $derived(open ? bestGuess(open, openCandidates, openDetail) : null);
  const banner = $derived(open ? bannerFor(open, openDetail) : null);
  const beforeRows = $derived(beforeAfterRows(openDetail));
  const timeline = $derived(open ? buildTimeline(open, openDetail) : []);
  const matrix = $derived(buildOriginMatrix(openDetail));
  const finalValues = $derived<Record<string, string>>(open ? { ...(editedMetadata[open.id] ?? {}) } : {});
</script>

{#if open && guess && banner}
  <div class="mob">
    <MobileHeader back="Queue" onback={() => (openId = null)} title="Provenance">
      {#snippet right()}
        <span class="text-muted-foreground font-mono text-[11px]">{clock(openDetail?.providerAttempts?.[0]?.attemptedAtUtc ?? open.indexedAtUtc)}</span>
      {/snippet}
    </MobileHeader>
    <div class="mob-scroll">
      <!-- title -->
      <div class="flex items-center gap-3 px-4 pt-4 pb-3">
        <Cover artist={open.artist ?? 'Unknown'} title={guess.title} size={52} corner={8} caption={false} />
        <div class="min-w-0">
          <div class="truncate text-[19px] font-semibold">
            {guess.title}{#if guess.isGuess}<span class="text-muted-foreground font-normal"> (best guess)</span>{/if}
          </div>
          <div class="text-muted-foreground truncate text-[13px]">{guess.subtitle || open.fileName}</div>
        </div>
      </div>

      <!-- banner -->
      <div class={cn('mx-4 mb-3 flex items-start gap-2.5 rounded-xl p-3.5', BANNER_TONE[banner.tone])}>
        {#if banner.tone === 'ok'}<Check class="mt-0.5 size-4 shrink-0" strokeWidth={2.5} />{:else}<AlertTriangle class="mt-0.5 size-4 shrink-0" strokeWidth={2.5} />{/if}
        <div class="min-w-0">
          <div class="text-[14px] font-semibold">{banner.title}</div>
          <div class="text-foreground/70 mt-0.5 text-[12.5px]">{banner.body}</div>
        </div>
      </div>

      <!-- KPIs -->
      <div class="mob-rv-kpis mx-4 rounded-xl border-t-0">
        <div><div class="mob-rv-kpi-k">ELAPSED</div><div class="mob-rv-kpi-v">{formatElapsed(elapsedMs(open, openDetail))}</div></div>
        <div><div class="mob-rv-kpi-k">PROVIDERS</div><div class="mob-rv-kpi-v">{openDetail?.providerAttempts.length ?? 0}</div></div>
        <div><div class="mob-rv-kpi-k">CANDIDATES</div><div class="mob-rv-kpi-v">{openCandidates.length}</div></div>
        <div><div class="mob-rv-kpi-k">DECISION</div><div class="mob-rv-kpi-v text-primary">{decisionLabel(openDetail)}</div></div>
      </div>

      <!-- contributed -->
      {#if contributedProviders(openDetail).length > 0}
        <div class="flex flex-wrap items-center gap-2 px-4 pt-3">
          <span class="text-muted-foreground font-mono text-[10px] tracking-[0.08em]">CONTRIBUTED</span>
          {#each contributedProviders(openDetail) as c (c.label)}
            <span class="border-border inline-flex items-center gap-1.5 rounded-full border px-2 py-0.5">
              <span class="size-2 rounded-full" style="background: {c.color}"></span>
              <span class="text-[12px]">{c.label}</span>
            </span>
          {/each}
        </div>
      {/if}

      <!-- candidates -->
      <div class="px-4 pt-3">
        <CandidateGrid candidates={openCandidates} pickedKey={pickedKey[open.id] ?? null} loading={detailLoading[open.id]} onpick={pick} single />
      </div>

      <!-- preview -->
      <div class="px-4 pt-3">
        <button class="mob-btn" onclick={() => open && play(open)}><Play size={13} />Preview audio</button>
      </div>

      <!-- VIEW segmented -->
      <div class="bg-surface-sunken mx-4 mt-4 flex items-center gap-1 rounded-lg p-0.5">
        {#each VIEWS as v (v.key)}
          <button
            type="button"
            onclick={() => (view = v.key)}
            class={cn(
              'flex-1 rounded-md px-2 py-1.5 text-[12.5px] font-medium transition-colors',
              view === v.key ? 'bg-primary text-primary-foreground' : 'text-muted-foreground'
            )}>{v.label}</button
          >
        {/each}
      </div>

      <!-- view content -->
      <div class="px-4 pt-4">
        {#if view === 'before'}
          <div class="border-border bg-surface-sunken/40 rounded-lg border p-3 font-mono text-[11px]">
            <span class="text-muted-foreground">FROM</span> {open.fileName}
            <div class="text-muted-foreground mt-1 break-all">
              {open.sourcePath.split('/').slice(0, -1).join('/')} · {formatFileSize(open.fileSizeBytes)}
            </div>
          </div>
          <div class="mt-3 space-y-2.5">
            {#each beforeRows as row (row.key)}
              <div>
                <div class="text-muted-foreground text-[11px] font-medium">{row.label}</div>
                <div class="mt-1 grid grid-cols-2 gap-2">
                  <div class="min-w-0">
                    <div class="text-muted-foreground/70 text-[10px] tracking-wide uppercase">was</div>
                    <div class="flex items-center gap-1.5">
                      <span class={cn('truncate text-[13px]', !row.embedded && 'text-muted-foreground/60 italic')}>{row.embedded || '(empty)'}</span>
                      {#if openIsReview && row.embedded && row.embedded !== (finalValues[row.key] ?? '')}
                        <button type="button" class="text-muted-foreground border-border grid size-4 shrink-0 place-items-center rounded border" onclick={() => setField(row.key, row.embedded)}><Plus class="size-2.5" /></button>
                      {/if}
                    </div>
                  </div>
                  <div class="min-w-0">
                    <div class="text-primary text-[10px] tracking-wide uppercase">now</div>
                    {#if openIsReview}
                      <input
                        class="border-border bg-background focus:border-primary mt-0.5 h-8 w-full rounded-md border px-2 text-[13px] outline-none"
                        type={row.key === 'year' || row.key === 'trackNumber' ? 'number' : 'text'}
                        value={finalValues[row.key] ?? ''}
                        oninput={(e) => setField(row.key, (e.target as HTMLInputElement).value)}
                      />
                    {:else}
                      <div class="truncate text-[13px]">{finalValues[row.key] || '—'}</div>
                    {/if}
                  </div>
                </div>
              </div>
            {/each}
          </div>
        {:else if view === 'timeline'}
          <TimelineView events={timeline} />
        {:else}
          <OriginMatrixView {matrix} />
        {/if}
      </div>

      <!-- actions -->
      {#if openIsReview}
        <div class="mob-rv-actions mt-5">
          <button class="mob-btn warn" disabled={busy} onclick={() => decide('reject')}><X size={12} strokeWidth={2} />Reject</button>
          <button class="mob-btn" disabled={busy} onclick={() => decide('skip')}>Skip</button>
          <button class="mob-btn primary" disabled={busy} onclick={() => decide('accept')}><Check size={12} strokeWidth={2} />Accept</button>
        </div>
      {:else}
        <div class="px-4 pt-5">
          <button class="mob-btn" disabled={busy} onclick={reenrich}>
            {#if busy}<Loader2 class="size-3.5 animate-spin" />{:else}<RefreshCw size={13} />{/if}Re-enrich
          </button>
        </div>
      {/if}
      <div class="h-8"></div>
    </div>
  </div>
{:else}
  <div class="mob">
    <MobileHeader title="Provenance & review" sub="{reviewTracks.length} pending · {doneTracks.length} done" />
    <!-- segmented filter -->
    <div class="border-border flex items-center gap-1 border-b px-3 py-2">
      {#each FILTERS as f (f.key)}
        {@const count = f.key === 'needsreview' ? reviewTracks.length : f.key === 'done' ? doneTracks.length : reviewTracks.length + doneTracks.length}
        <button
          type="button"
          onclick={() => (queueFilter = f.key)}
          class={cn(
            'flex items-center gap-1.5 rounded-md px-2.5 py-1.5 text-[12.5px] font-medium transition-colors',
            queueFilter === f.key ? 'bg-primary/15 text-primary' : 'text-muted-foreground'
          )}
        >
          {#if f.key === 'needsreview'}<AlertTriangle class="size-3.5" />{/if}
          {#if f.key === 'done'}<Check class="size-3.5" />{/if}
          <span>{f.label}</span>
          <span class={cn('rounded-full px-1.5 text-[10px] font-semibold tabular-nums', queueFilter === f.key ? 'bg-primary text-primary-foreground' : 'bg-muted text-muted-foreground')}>{count}</span>
        </button>
      {/each}
    </div>

    <div class="mob-scroll pt-2.5">
      {#if loading}
        <div class="text-muted-foreground px-6 py-16 text-center text-sm">Loading…</div>
      {:else if tracks.length === 0}
        <div class="px-6 py-16 text-center">
          <Check class="text-primary mx-auto mb-3" size={32} />
          <div class="text-sm font-medium">{queueFilter === 'needsreview' ? 'All clear' : 'Nothing here'}</div>
          <div class="text-muted-foreground mt-1 text-[13px]">
            {queueFilter === 'needsreview' ? 'No items in the review queue.' : 'No matched items to show.'}
          </div>
        </div>
      {:else}
        {#each tracks as t (t.id)}
          {@const r = reasonFor(t)}
          {@const d = decisions[t.id]}
          {@const info = rowGuess(t)}
          {@const detail = details[t.id]}
          {@const provs = contributedProviders(detail)}
          {@const ms = detail ? formatElapsed(elapsedMs(t, detail)) : null}
          <button
            class="border-border bg-card mx-4 mb-3 flex w-[calc(100%-32px)] items-center gap-3 overflow-hidden rounded-xl border border-l-[3px] border-l-amber-500 p-3 text-left"
            onclick={() => openItem(t.id)}
          >
            <Cover artist={t.artist ?? 'Unknown'} title={info.title} size={48} corner={8} caption={false} />
            <div class="min-w-0 flex-1">
              <div class="truncate text-[15px] font-semibold">{info.title}</div>
              <div class="text-muted-foreground truncate text-[12.5px]">{info.subtitle || '—'}</div>
              <div class="mt-1.5 flex flex-wrap items-center gap-1.5">
                <span class={cn('rounded px-1.5 py-0.5 text-[9px] font-bold tracking-wide uppercase', PILL_TINT[r.tint])}>{r.label}</span>
                {#each provs.slice(0, 3) as p (p.label)}
                  <span class="size-1.5 rounded-full" style="background: {p.color}" title={p.label}></span>
                {/each}
              </div>
            </div>
            <div class="flex shrink-0 flex-col items-end gap-1">
              {#if ms}<span class="text-muted-foreground font-mono text-[10.5px]">{ms}</span>{/if}
              {#if d}
                <span class="rounded-lg px-1.5 py-0.5 text-[9.5px] font-bold tracking-wider text-white uppercase" style="background: {d === 'accept' ? 'var(--primary)' : d === 'reject' ? '#c23a3a' : 'var(--muted-foreground)'};">{d}</span>
              {/if}
            </div>
          </button>
        {/each}
      {/if}
      <div class="h-8"></div>
    </div>
  </div>
{/if}

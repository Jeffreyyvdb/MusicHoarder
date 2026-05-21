<script lang="ts">
  import { onMount } from 'svelte';
  import { Check, X, Search, Loader2 } from '@lucide/svelte';
  import MobileHeader from '$lib/components/mobile/MobileHeader.svelte';
  import {
    fetchReviewTracks,
    fetchEnrichmentDetail,
    submitManualReview,
    getSongStreamUrl,
    type ApiSong,
    type EnrichmentDetail
  } from '$lib/api-client';
  import { reasonFor, candidatesFromDetail, embeddedTags } from '$lib/review-helpers';
  import { formatDuration, formatFileSize } from '$lib/formatters';
  import { playerStore } from '$lib/stores/player.svelte';

  let tracks = $state<ApiSong[]>([]);
  let loading = $state(true);
  let openId = $state<number | null>(null);
  let decisions = $state<Record<number, 'accept' | 'reject' | 'skip'>>({});
  let details = $state<Record<number, EnrichmentDetail>>({});
  let detailLoading = $state<Record<number, boolean>>({});
  let pickedKey = $state<Record<number, string>>({});
  let busy = $state(false);

  async function load() {
    loading = true;
    try {
      tracks = await fetchReviewTracks();
      // Prefetch candidate detail so each list card can show its top guess.
      for (const track of tracks) void loadDetail(track.id);
    } catch {
      tracks = [];
    } finally {
      loading = false;
    }
  }

  function topGuess(id: number): { title: string; sub: string } | null {
    const c = candidatesFromDetail(details[id])[0];
    if (!c) return null;
    const sub = [c.artist, c.album, c.year].filter(Boolean).join(' · ');
    return { title: c.title, sub };
  }

  onMount(load);

  const open = $derived(openId != null ? (tracks.find((t) => t.id === openId) ?? null) : null);
  const openCandidates = $derived(open ? candidatesFromDetail(details[open.id]) : []);

  async function loadDetail(id: number) {
    if (details[id] || detailLoading[id]) return;
    detailLoading = { ...detailLoading, [id]: true };
    try {
      const detail = await fetchEnrichmentDetail(id);
      details = { ...details, [id]: detail };
      const cands = candidatesFromDetail(detail);
      if (cands[0] && !pickedKey[id]) pickedKey = { ...pickedKey, [id]: cands[0].key };
    } catch {
      // candidates optional
    } finally {
      detailLoading = { ...detailLoading, [id]: false };
    }
  }

  function openItem(id: number) {
    openId = id;
    void loadDetail(id);
  }

  function play(t: ApiSong) {
    void playerStore.playSong({
      id: t.id,
      title: (t.title ?? t.fileName).trim() || t.fileName,
      artist: (t.artist ?? 'Unknown Artist').trim() || 'Unknown Artist',
      streamUrl: getSongStreamUrl(t.id)
    });
  }

  function folderName(sourcePath: string): string {
    const parts = sourcePath.split('/');
    parts.pop();
    return parts.pop() ?? '';
  }

  async function decide(action: 'accept' | 'reject' | 'skip') {
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
        const picked = candidatesFromDetail(details[t.id]).find((c) => c.key === pickedKey[t.id]);
        const overrides = picked
          ? {
              title: picked.fields.title,
              artist: picked.fields.artist,
              album: picked.fields.album,
              year: picked.fields.year ? parseInt(picked.fields.year, 10) : undefined
            }
          : {};
        await submitManualReview(t.id, { decision: 'approve', ...overrides });
      } else {
        await submitManualReview(t.id, { decision: 'reject' });
      }
      decisions = { ...decisions, [t.id]: action };
      openId = null;
    } catch {
      // leave open on failure
    } finally {
      busy = false;
    }
  }

  const decidedCount = $derived(Object.keys(decisions).length);
</script>

{#if open}
  {@const r = reasonFor(open)}
  <div class="mob">
    <MobileHeader back="Queue" onback={() => (openId = null)} title="Review" />
    <div class="mob-scroll">
      <div class="mob-rv-card-h" style="background: var(--surface-sunken);">
        <span class="mob-pill {r.tint}">{r.label}</span>
        <span class="mob-rv-card-file">{open.fileName}</span>
      </div>

      <div class="mob-rv-kpis">
        <div><div class="mob-rv-kpi-k">DUR</div><div class="mob-rv-kpi-v">{formatDuration(open.durationSeconds)}</div></div>
        <div><div class="mob-rv-kpi-k">SIZE</div><div class="mob-rv-kpi-v">{formatFileSize(open.fileSizeBytes)}</div></div>
        <div><div class="mob-rv-kpi-k">FORMAT</div><div class="mob-rv-kpi-v">{(open.extension ?? '').replace(/^\./, '').toUpperCase() || '—'}</div></div>
        <div><div class="mob-rv-kpi-k">FOLDER</div><div class="mob-rv-kpi-v truncate">{folderName(open.sourcePath)}</div></div>
      </div>

      <div class="px-4 pt-4 pb-2">
        <div class="text-muted-foreground mb-2.5 text-[10.5px] font-semibold tracking-wider uppercase">
          Candidate matches · {openCandidates.length}
        </div>
        {#if detailLoading[open.id] && openCandidates.length === 0}
          <div class="text-muted-foreground flex items-center gap-2 py-2 text-sm">
            <Loader2 class="size-4 animate-spin" /> Loading candidates…
          </div>
        {/if}
        {#if !detailLoading[open.id] && openCandidates.length === 0}
          <div class="bg-surface-sunken text-muted-foreground rounded-lg p-3.5 text-[13px]">
            No fingerprint matches. Enter metadata manually or skip.
          </div>
        {/if}
        {#each openCandidates as c (c.key)}
          {@const picked = pickedKey[open.id] === c.key}
          <button
            class="mob-row mb-1.5 rounded-[10px] border"
            style="border-color: {picked ? 'var(--primary)' : 'var(--border)'}; background: {picked ? 'var(--accent-soft)' : 'var(--card)'};"
            onclick={() => (pickedKey = { ...pickedKey, [open.id]: c.key })}
          >
            <div class="w-10 shrink-0 text-center">
              <div class="font-mono text-[15px] font-semibold {picked ? 'text-primary' : ''}">
                {c.score != null ? c.score.toFixed(2) : '—'}
              </div>
            </div>
            <div class="mob-row-meta">
              <div class="mob-row-t">{c.title}</div>
              <div class="mob-row-s">{c.artist} · {c.album} · {c.year}</div>
              <div class="text-muted-foreground mt-1 font-mono text-[10.5px]">{c.source}</div>
            </div>
            {#if picked}<Check size={13} class="text-primary" strokeWidth={2.5} />{/if}
          </button>
        {/each}
      </div>

      <div class="px-4 pt-2 pb-3">
        <button class="mob-btn" onclick={() => open && play(open)}><Search size={13} />Preview audio</button>
      </div>

      <div class="mob-grouped-h">Embedded tags</div>
      <div class="mob-grouped">
        {#each embeddedTags(open) as tag (tag.key)}
          <div class="mob-row">
            <span class="text-muted-foreground w-[70px] shrink-0 font-mono text-[11px]">{tag.key}</span>
            <div class="mob-row-meta">
              <div class="text-[13.5px]">
                {#if tag.value}{tag.value}{:else}<em class="text-muted-foreground/60 text-xs">(empty)</em>{/if}
              </div>
            </div>
          </div>
        {/each}
      </div>

      <div class="mob-rv-actions">
        <button class="mob-btn warn" disabled={busy} onclick={() => decide('reject')}><X size={12} strokeWidth={2} />Reject</button>
        <button class="mob-btn" disabled={busy} onclick={() => decide('skip')}>Skip</button>
        <button class="mob-btn primary" disabled={busy} onclick={() => decide('accept')}><Check size={12} strokeWidth={2} />Accept</button>
      </div>
    </div>
  </div>
{:else}
  <div class="mob">
    <MobileHeader title="Manual review" sub="{tracks.length - decidedCount} pending · {decidedCount} decided" />
    <div class="mob-scroll pt-2.5">
      {#if loading}
        <div class="text-muted-foreground px-6 py-16 text-center text-sm">Loading…</div>
      {:else if tracks.length === 0}
        <div class="px-6 py-16 text-center">
          <Check class="text-primary mx-auto mb-3" size={32} />
          <div class="text-sm font-medium">All clear</div>
          <div class="text-muted-foreground mt-1 text-[13px]">No items in the review queue.</div>
        </div>
      {:else}
        {#each tracks as t (t.id)}
          {@const r = reasonFor(t)}
          {@const d = decisions[t.id]}
          {@const guess = topGuess(t.id)}
          <button class="mob-rv-card" onclick={() => openItem(t.id)}>
            <div class="mob-rv-card-h">
              <span class="mob-pill {r.tint}">{r.label}</span>
              {#if t.matchConfidence != null}
                <span class="text-muted-foreground font-mono text-[11px]">{t.matchConfidence.toFixed(2)}</span>
              {/if}
              {#if d}
                <span
                  class="ml-auto rounded-lg px-1.5 py-0.5 text-[9.5px] font-bold tracking-wider text-white uppercase"
                  style="background: {d === 'accept' ? 'var(--primary)' : d === 'reject' ? '#c23a3a' : 'var(--muted-foreground)'};"
                >{d}</span>
              {/if}
            </div>
            <div class="mob-rv-card-body">
              <div class="mob-rv-card-eyebrow">{t.fileName}</div>
              {#if guess}
                <div class="mob-rv-card-guess">{guess.title}</div>
                <div class="mob-rv-card-guess-sub">{guess.sub}</div>
              {:else}
                <div class="mob-rv-card-guess text-muted-foreground font-medium">
                  No candidates — manual entry needed
                </div>
              {/if}
            </div>
          </button>
        {/each}
      {/if}
      <div class="h-8"></div>
    </div>
  </div>
{/if}

<script lang="ts">
  import { onMount } from 'svelte';
  import { Check, X, Search } from '@lucide/svelte';
  import MobileHeader from '$lib/components/mobile/MobileHeader.svelte';
  import {
    fetchReviewTracks,
    submitManualReview,
    getSongStreamUrl,
    type ApiSong
  } from '$lib/api-client';
  import { formatDuration, formatFileSize } from '$lib/formatters';
  import { playerStore } from '$lib/stores/player.svelte';

  let tracks = $state<ApiSong[]>([]);
  let loading = $state(true);
  let openId = $state<number | null>(null);
  let decisions = $state<Record<number, 'accept' | 'reject' | 'skip'>>({});
  let pickedOriginal = $state(false);
  let busy = $state(false);

  async function load() {
    loading = true;
    try {
      tracks = await fetchReviewTracks();
    } catch {
      tracks = [];
    } finally {
      loading = false;
    }
  }

  onMount(load);

  function reasonFor(t: ApiSong): { label: string; cls: string } {
    if (!t.fingerprint) return { label: 'No fingerprint', cls: 'err' };
    if ((t.matchWarnings?.length ?? 0) > 1) return { label: 'Multiple matches', cls: 'info' };
    if (t.matchConfidence != null && t.matchConfidence < 0.7) return { label: 'Low confidence', cls: 'warn' };
    return { label: 'Needs review', cls: 'warn' };
  }

  const open = $derived(openId != null ? (tracks.find((t) => t.id === openId) ?? null) : null);

  type Candidate = {
    src: string;
    title: string;
    artist: string;
    album: string;
    year: string;
    score: number | null;
    original: boolean;
  };

  const candidates = $derived.by<Candidate[]>(() => {
    const t = open;
    if (!t) return [];
    const out: Candidate[] = [
      {
        src: t.matchedBy ?? 'enrichment',
        title: t.title ?? t.fileName,
        artist: t.artist ?? '—',
        album: t.album ?? '—',
        year: t.year ? String(t.year) : '—',
        score: t.matchConfidence ?? null,
        original: false
      }
    ];
    if (t.originalMetadataCaptured) {
      out.push({
        src: 'embedded tags',
        title: t.originalTitle ?? t.fileName,
        artist: t.originalArtist ?? '—',
        album: t.originalAlbum ?? '—',
        year: t.originalYear ? String(t.originalYear) : '—',
        score: null,
        original: true
      });
    }
    return out;
  });

  function openItem(id: number) {
    openId = id;
    pickedOriginal = false;
  }

  function play(t: ApiSong) {
    void playerStore.playSong({
      id: t.id,
      title: (t.title ?? t.fileName).trim() || t.fileName,
      artist: (t.artist ?? 'Unknown Artist').trim() || 'Unknown Artist',
      streamUrl: getSongStreamUrl(t.id)
    });
  }

  async function decide(action: 'accept' | 'reject' | 'skip') {
    const t = open;
    if (!t || busy) return;
    decisions = { ...decisions, [t.id]: action };
    if (action === 'skip') {
      openId = null;
      return;
    }
    busy = true;
    try {
      if (action === 'accept') {
        const overrides =
          pickedOriginal && t.originalMetadataCaptured
            ? {
                artist: t.originalArtist ?? undefined,
                albumArtist: t.originalAlbumArtist ?? undefined,
                album: t.originalAlbum ?? undefined,
                title: t.originalTitle ?? undefined,
                year: t.originalYear ?? undefined,
                trackNumber: t.originalTrackNumber ?? undefined
              }
            : {};
        await submitManualReview(t.id, { decision: 'approve', ...overrides });
        tracks = tracks.filter((x) => x.id !== t.id);
      } else {
        await submitManualReview(t.id, { decision: 'reject' });
        tracks = tracks.filter((x) => x.id !== t.id);
      }
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
        <span class="mob-pill {r.cls}">{r.label}</span>
        <span class="mob-rv-card-file">{open.fileName}</span>
      </div>

      <div class="mob-rv-kpis">
        <div><div class="mob-rv-kpi-k">DUR</div><div class="mob-rv-kpi-v">{formatDuration(open.durationSeconds)}</div></div>
        <div><div class="mob-rv-kpi-k">SIZE</div><div class="mob-rv-kpi-v">{formatFileSize(open.fileSizeBytes)}</div></div>
        <div><div class="mob-rv-kpi-k">FORMAT</div><div class="mob-rv-kpi-v">{(open.extension ?? '').replace(/^\./, '').toUpperCase() || '—'}</div></div>
        <div><div class="mob-rv-kpi-k">CONF</div><div class="mob-rv-kpi-v">{open.matchConfidence != null ? open.matchConfidence.toFixed(2) : '—'}</div></div>
      </div>

      <div class="px-4 pt-4 pb-2">
        <div class="text-muted-foreground mb-2.5 text-[10.5px] font-semibold tracking-wider uppercase">
          Candidate matches · {candidates.length}
        </div>
        {#each candidates as c (c.src)}
          {@const picked = pickedOriginal === c.original}
          <button
            class="mob-row mb-1.5 rounded-[10px] border"
            style="border-color: {picked ? 'var(--primary)' : 'var(--border)'}; background: {picked ? 'var(--accent-soft)' : 'var(--card)'};"
            onclick={() => (pickedOriginal = c.original)}
          >
            <div class="w-10 shrink-0 text-center">
              <div class="font-mono text-[15px] font-semibold {picked ? 'text-primary' : ''}">
                {c.score != null ? c.score.toFixed(2) : '—'}
              </div>
            </div>
            <div class="mob-row-meta">
              <div class="mob-row-t">{c.title}</div>
              <div class="mob-row-s">{c.artist} · {c.album} · {c.year}</div>
              <div class="text-muted-foreground mt-1 font-mono text-[10.5px]">{c.src}</div>
            </div>
            {#if picked}<Check size={13} class="text-primary" strokeWidth={2.5} />{/if}
          </button>
        {/each}
      </div>

      <div class="px-4 pt-2 pb-3">
        <button class="mob-btn" onclick={() => open && play(open)}><Search size={13} />Preview / search</button>
      </div>

      <div class="mob-grouped-h">Embedded tags</div>
      <div class="mob-grouped">
        {#each [['title', open.title], ['artist', open.artist], ['album', open.album], ['year', open.year], ['track', open.trackNumber]] as [k, v] (k)}
          <div class="mob-row">
            <span class="text-muted-foreground w-[70px] shrink-0 font-mono text-[11px]">{k}</span>
            <div class="mob-row-meta">
              <div class="text-[13.5px]">
                {#if v != null && String(v).length}{v}{:else}<em class="text-muted-foreground/60 text-xs">(empty)</em>{/if}
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
    <MobileHeader title="Manual review" sub="{tracks.length} pending · {decidedCount} decided" />
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
          <button class="mob-rv-card" onclick={() => openItem(t.id)}>
            <div class="mob-rv-card-h">
              <span class="mob-pill {r.cls}">{r.label}</span>
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
              <div class="mob-rv-card-guess">{t.title ?? t.fileName}</div>
              <div class="mob-rv-card-guess-sub">
                {t.artist ?? '—'}{#if t.album} · {t.album}{/if}{#if t.year} · {t.year}{/if}
              </div>
            </div>
          </button>
        {/each}
      {/if}
      <div class="h-8"></div>
    </div>
  </div>
{/if}

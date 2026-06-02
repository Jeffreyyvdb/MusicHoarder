<script lang="ts">
  import { onMount } from 'svelte';
  import { Disc3, RefreshCw } from '@lucide/svelte';
  import {
    fetchAlbumQualityOverview,
    fetchAlbumQualityProgress,
    gradeAllAlbums,
    type AlbumQualityOverview,
    type AlbumQualityRow
  } from '$lib/api-client';
  import { verdictBadge, scoreColor, verdictGlyph } from '$lib/quality-ui';

  let overview = $state<AlbumQualityOverview | null>(null);
  let loading = $state(true);
  let grading = $state(false);
  let progressText = $state<string | null>(null);

  async function load() {
    loading = true;
    try {
      overview = await fetchAlbumQualityOverview();
    } catch {
      overview = null;
    } finally {
      loading = false;
    }
  }

  onMount(load);

  /** Deep-link to an album in the library (matches AlbumSummary.key). */
  function albumHref(row: AlbumQualityRow): string {
    const key = `${(row.artist ?? '').toLowerCase()}::${(row.album ?? '').toLowerCase()}`;
    return `/library?album=${encodeURIComponent(key)}`;
  }

  async function regradeAll() {
    if (grading) return;
    grading = true;
    progressText = 'Queuing…';
    try {
      const { enqueued } = await gradeAllAlbums();
      if (enqueued === 0) {
        progressText = 'Everything is up to date.';
        grading = false;
        return;
      }
      // Poll until the run drains, then reload the overview.
      const poll = setInterval(async () => {
        try {
          const p = await fetchAlbumQualityProgress();
          if (p.active) {
            progressText = `Grading ${p.processed ?? 0}/${p.total ?? enqueued}…`;
          } else {
            clearInterval(poll);
            grading = false;
            progressText = null;
            await load();
          }
        } catch {
          clearInterval(poll);
          grading = false;
          progressText = null;
        }
      }, 1500);
    } catch {
      progressText = 'Grading is not configured or is disabled in Settings.';
      grading = false;
    }
  }

  const lib = $derived(overview?.library);
</script>

<div class="flex min-h-0 flex-1 flex-col gap-5 p-5 sm:p-6">
  <header class="flex flex-wrap items-end justify-between gap-3">
    <div>
      <h1 class="text-xl font-semibold tracking-tight">Album matches</h1>
      <p class="text-muted-foreground mt-1 text-sm">
        Did we link each album to the right provider album? Albums the AI judged a poor match show up first.
      </p>
    </div>
    <div class="flex items-center gap-3">
      {#if progressText}
        <span class="text-muted-foreground text-xs">{progressText}</span>
      {/if}
      <button
        type="button"
        onclick={regradeAll}
        disabled={grading}
        class="bg-primary text-primary-foreground inline-flex items-center gap-2 rounded-md px-3 py-1.5 text-sm font-medium transition-opacity hover:opacity-90 disabled:opacity-60"
      >
        <RefreshCw class={`size-4 ${grading ? 'animate-spin' : ''}`} />
        Grade all albums
      </button>
    </div>
  </header>

  {#if loading}
    <div class="text-muted-foreground flex flex-1 items-center justify-center text-sm">Loading…</div>
  {:else if !overview || lib === undefined}
    <div class="text-muted-foreground flex flex-1 items-center justify-center text-sm">No album grades yet.</div>
  {:else}
    <!-- Rollup -->
    <div class="grid grid-cols-2 gap-3 sm:grid-cols-3 lg:grid-cols-6">
      {#each [{ label: 'Graded', value: lib.graded }, { label: 'Coverage', value: `${Math.round(overview.coverage * 100)}%` }, { label: 'Avg score', value: lib.averageScore ?? '—' }, { label: 'Wrong', value: lib.verdicts.wrong }, { label: 'Questionable', value: lib.verdicts.questionable }, { label: 'Excellent', value: lib.verdicts.excellent }] as card (card.label)}
        <div class="border-border rounded-lg border p-3">
          <div class="text-muted-foreground text-[11px] font-medium tracking-wide uppercase">{card.label}</div>
          <div class="mt-1 text-lg font-semibold">{card.value}</div>
        </div>
      {/each}
    </div>

    <!-- Worst offenders -->
    <div class="border-border min-h-0 flex-1 overflow-auto rounded-lg border">
      {#if overview.worstOffenders.length === 0}
        <div class="text-muted-foreground flex h-32 flex-col items-center justify-center gap-2 text-sm">
          <Disc3 class="size-8 opacity-40" />
          No graded albums yet — run “Grade all albums”.
        </div>
      {:else}
        <ul class="divide-border divide-y">
          {#each overview.worstOffenders as row (row.canonicalAlbumId)}
            <li>
              <a href={albumHref(row)} class="hover:bg-accent/50 flex items-center gap-3 px-4 py-2.5 transition-colors">
                <span
                  class={`inline-flex shrink-0 items-center gap-1 rounded-full border px-2 py-0.5 text-[11px] font-semibold ${verdictBadge(row.verdict)}`}
                >
                  {verdictGlyph(row.verdict)} {row.verdict}
                </span>
                <span class={`w-8 shrink-0 text-right font-mono text-sm ${scoreColor(row.score)}`}>{row.score}</span>
                <span class="min-w-0 flex-1">
                  <span class="block truncate text-sm font-medium">{row.album ?? '—'}</span>
                  <span class="text-muted-foreground block truncate text-[12px]">
                    {row.artist ?? '—'} · {row.ownedTrackCount}/{row.canonicalTrackCount} owned
                    {#if row.summary}— {row.summary}{/if}
                  </span>
                </span>
              </a>
            </li>
          {/each}
        </ul>
      {/if}
    </div>
  {/if}
</div>

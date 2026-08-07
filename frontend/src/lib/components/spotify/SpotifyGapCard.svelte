<script lang="ts">
  import {
    fetchSpotifyComparisonSummary,
    type SpotifyComparisonSummary
  } from '$lib/api-client';

  // How much of your liked-songs library you actually hold. A background sweep has been computing
  // and persisting these four numbers all along, and nothing displayed them — this is the whole
  // "what am I missing" question the app exists to answer.
  let summary = $state<SpotifyComparisonSummary | null>(null);
  let failed = $state(false);

  $effect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const next = await fetchSpotifyComparisonSummary();
        if (!cancelled) summary = next;
      } catch {
        if (!cancelled) failed = true;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  // The endpoint returns four zeros when the sweep has never run, so the timestamp — not the
  // counts — is what says whether these numbers mean anything.
  const measured = $derived(summary != null && summary.updatedAtUtc != null);

  const ownedPct = $derived(
    summary && summary.total > 0 ? Math.round((summary.inLibrary / summary.total) * 100) : 0
  );

  function fmtWhen(iso: string): string {
    const then = new Date(iso).getTime();
    const mins = Math.max(0, Math.round((Date.now() - then) / 60_000));
    if (mins < 1) return 'just now';
    if (mins < 60) return `${mins}m ago`;
    const hours = Math.round(mins / 60);
    if (hours < 24) return `${hours}h ago`;
    return `${Math.round(hours / 24)}d ago`;
  }
</script>

{#if !failed}
  <div class="border-border border-b px-4 py-4 md:px-6">
    {#if summary == null}
      <div class="text-muted-foreground text-xs">Checking what you already have…</div>
    {:else if !measured}
      <div class="text-muted-foreground text-xs">
        Not checked yet — a background sweep compares your liked songs against the library and
        usually runs within a couple of hours of connecting Spotify.
      </div>
    {:else}
      <div class="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
        <h2 class="text-[13px] font-semibold">
          You have {summary.inLibrary.toLocaleString()} of {summary.total.toLocaleString()} liked songs
        </h2>
        <span class="text-muted-foreground text-[11px]">
          Checked {fmtWhen(summary.updatedAtUtc!)}
        </span>
      </div>

      <div class="bg-border mt-2.5 h-1.5 overflow-hidden rounded-full">
        <div class="bg-primary h-full rounded-full" style="width: {ownedPct}%;"></div>
      </div>

      <dl class="mt-2.5 flex flex-wrap gap-x-6 gap-y-1 text-[11.5px]">
        <div class="flex items-baseline gap-1.5">
          <dt class="text-muted-foreground">In library</dt>
          <dd class="font-semibold tabular-nums">{summary.inLibrary.toLocaleString()}</dd>
        </div>
        <div class="flex items-baseline gap-1.5">
          <!-- Matched, but below the confidence threshold — a maybe, not a yes. -->
          <dt class="text-muted-foreground">Possible</dt>
          <dd class="font-semibold tabular-nums">{summary.possibleMatch.toLocaleString()}</dd>
        </div>
        <div class="flex items-baseline gap-1.5">
          <dt class="text-muted-foreground">Missing</dt>
          <dd class="font-semibold tabular-nums">{summary.notInLibrary.toLocaleString()}</dd>
        </div>
      </dl>
    {/if}
  </div>
{/if}

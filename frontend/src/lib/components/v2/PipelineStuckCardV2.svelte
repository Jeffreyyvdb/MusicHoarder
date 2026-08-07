<script lang="ts">
  import { fetchStuckCounts, type StuckCounts } from '$lib/api-client';

  // Tracks the pipeline has set aside. The endpoint returns two integers and nothing else — no
  // ids, no list — so this is a counter, not a drill-down. Resisting the urge to link it
  // somewhere is the point: there is no page that shows *which* tracks these are.
  let counts = $state<StuckCounts | null>(null);

  $effect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const next = await fetchStuckCounts();
        if (!cancelled) counts = next;
      } catch {
        // Leave the section hidden rather than showing a broken card.
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  // Nothing set aside is the normal state — don't spend a section on two zeroes.
  const show = $derived(counts != null && (counts.quarantined > 0 || counts.lyricsHeld > 0));
</script>

{#if show && counts}
  <section aria-label="Set aside">
    <div class="mb-5 flex flex-wrap items-baseline gap-x-2 gap-y-1">
      <h2 class="text-[13px] font-semibold">Set aside</h2>
      <span class="text-muted-foreground text-[12px]">
        Tracks the pipeline stopped working on.
      </span>
    </div>

    <div class="border-border divide-border divide-y rounded-lg border">
      {#if counts.quarantined > 0}
        <div class="flex items-start justify-between gap-4 p-4">
          <div class="min-w-0">
            <h3 class="text-[13px] font-semibold">Gave up building</h3>
            <p class="text-muted-foreground mt-1 text-[12px]">
              Matched, but the build failed enough times to exhaust its retries. These will not be
              picked up again on their own — re-run the build for the album to try afresh.
            </p>
          </div>
          <span class="text-[15px] font-semibold tabular-nums">
            {counts.quarantined.toLocaleString()}
          </span>
        </div>
      {/if}
      {#if counts.lyricsHeld > 0}
        <div class="flex items-start justify-between gap-4 p-4">
          <div class="min-w-0">
            <h3 class="text-[13px] font-semibold">Waiting on lyrics</h3>
            <p class="text-muted-foreground mt-1 text-[12px]">
              Ready to build, but held while lyrics are fetched. This clears itself once the lyrics
              land or the wait times out — nothing to do.
            </p>
          </div>
          <span class="text-muted-foreground text-[15px] font-semibold tabular-nums">
            {counts.lyricsHeld.toLocaleString()}
          </span>
        </div>
      {/if}
    </div>
  </section>
{/if}

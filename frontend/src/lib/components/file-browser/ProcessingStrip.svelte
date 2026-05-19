<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import { fetchOverview, type ApiOverview } from '$lib/api-client';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { cn } from '$lib/utils';

  const POLL_MS = 4000;

  type Card = {
    id: string;
    title: string;
    artist: string;
    stage: 'fingerprinting' | 'lookup' | 'artwork';
    progress: number;
  };

  let overview = $state<ApiOverview | null>(null);
  let pollHandle: ReturnType<typeof setInterval> | null = null;
  let cancelled = false;

  async function refresh() {
    try {
      const next = await fetchOverview();
      if (!cancelled) overview = next;
    } catch {
      // swallow; the strip just keeps the last good snapshot
    }
  }

  onMount(() => {
    void refresh();
    pollHandle = setInterval(refresh, POLL_MS);
  });

  onDestroy(() => {
    cancelled = true;
    if (pollHandle) clearInterval(pollHandle);
  });

  const cards = $derived.by<Card[]>(() => {
    const job = overview?.job;
    if (!job || job.status !== 'running') return [];
    const discovered = job.tracksDiscovered ?? 0;
    const fingerprinted = job.tracksFingerprinted ?? 0;
    const enriched = job.tracksEnriched ?? 0;
    const eligible = job.tracksBuildEligible ?? 0;
    const copied = job.tracksCopied ?? 0;
    const denom = Math.max(1, discovered);

    const out: Card[] = [];
    if (fingerprinted < discovered) {
      out.push({
        id: 'fp',
        title: 'Fingerprinting',
        artist: `${(discovered - fingerprinted).toLocaleString()} files in flight`,
        stage: 'fingerprinting',
        progress: Math.min(1, fingerprinted / denom)
      });
    }
    if (enriched < fingerprinted) {
      out.push({
        id: 'lookup',
        title: 'Metadata lookup',
        artist: `${(fingerprinted - enriched).toLocaleString()} awaiting match`,
        stage: 'lookup',
        progress: Math.min(1, enriched / denom)
      });
    }
    if (copied < eligible) {
      out.push({
        id: 'art',
        title: 'Writing to destination',
        artist: `${(eligible - copied).toLocaleString()} files to copy`,
        stage: 'artwork',
        progress: Math.min(1, copied / denom)
      });
    }
    return out.slice(0, 3);
  });

  const inFlight = $derived(cards.length);
</script>

{#if cards.length > 0}
  <div
    class="border-border bg-surface-sunken/60 mb-6 rounded-lg border p-3.5"
  >
    <div class="text-foreground mb-3 flex items-center gap-2 text-xs font-semibold">
      <span class="bg-primary mh-strip-pulse size-1.5 rounded-full"></span>
      <span>Processing now</span>
      <span class="text-muted-foreground ml-auto font-mono font-normal">
        {inFlight} in flight
      </span>
    </div>
    <div
      class="grid grid-cols-1 gap-2.5 sm:grid-cols-2 xl:grid-cols-3"
    >
      {#each cards as card (card.id)}
        <div
          class="border-border bg-background flex items-start gap-3 rounded-lg border p-2.5"
        >
          <Cover artist={card.artist} title={card.title} size={64} corner={4} caption={false} />
          <div class="flex min-w-0 flex-1 flex-col gap-0.5">
            <div class="truncate text-[12.5px] font-medium">{card.title}</div>
            <div class="text-muted-foreground truncate text-[11px]">{card.artist}</div>
            <div class="mt-1 flex items-center gap-2">
              <span
                class={cn(
                  'rounded px-1.5 py-0.5 text-[9.5px] font-semibold tracking-wider uppercase',
                  card.stage === 'fingerprinting' && 'bg-primary/15 text-primary',
                  card.stage === 'lookup' && 'bg-violet-500/15 text-violet-600 dark:text-violet-400',
                  card.stage === 'artwork' && 'bg-amber-500/15 text-amber-700 dark:text-amber-400'
                )}
              >
                {card.stage}
              </span>
              <span class="text-muted-foreground ml-auto font-mono text-[10.5px]">
                {Math.round(card.progress * 100)}%
              </span>
            </div>
            <div class="bg-border mt-1.5 h-[3px] overflow-hidden rounded-full">
              <div
                class="bg-primary h-full transition-[width] duration-500"
                style="width: {card.progress * 100}%;"
              ></div>
            </div>
          </div>
        </div>
      {/each}
    </div>
  </div>
{/if}

<style>
  :global(.mh-strip-pulse) {
    box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    animation: mh-strip-pulse 2s infinite;
  }
  @keyframes mh-strip-pulse {
    0% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    }
    70% {
      box-shadow: 0 0 0 6px oklch(0.5 0.17 145 / 0);
    }
    100% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0);
    }
  }
</style>

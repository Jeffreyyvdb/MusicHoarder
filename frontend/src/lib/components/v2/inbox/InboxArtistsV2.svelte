<script lang="ts">
  import { untrack } from 'svelte';
  import { Check, Loader2, RefreshCw, Users, Merge, Split } from '@lucide/svelte';
  import {
    fetchArtistDuplicates,
    mergeArtists,
    splitArtistCredit,
    dismissArtistDuplicates,
    type ArtistDuplicateReport,
    type ArtistDuplicateCluster
  } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import { cn } from '$lib/utils';

  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();

  let report = $state<ArtistDuplicateReport | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let acting = $state(false);
  let actionError = $state<string | null>(null);
  // Per-cluster canonical pick, keyed by the cluster's suggested canonical (stable per load).
  let canonicalPick = $state<Record<string, string>>({});

  const total = $derived((report?.clusters.length ?? 0) + (report?.combinedCredits.length ?? 0));

  // untrack the callback identity — see InboxTagReviewV2 for the loop this avoids.
  $effect(() => {
    const n = loading ? null : total;
    untrack(() => oncount?.(n));
  });

  async function load() {
    try {
      loading = true;
      error = null;
      report = await fetchArtistDuplicates();
      canonicalPick = {};
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load artist duplicates';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  function pickedCanonical(cluster: ArtistDuplicateCluster): string {
    return canonicalPick[cluster.suggestedCanonical] ?? cluster.suggestedCanonical;
  }

  async function run(action: () => Promise<unknown>) {
    if (acting) return;
    try {
      acting = true;
      actionError = null;
      await action();
      await load();
    } catch (err) {
      actionError = err instanceof Error ? err.message : 'Action failed';
    } finally {
      acting = false;
    }
  }

  function merge(cluster: ArtistDuplicateCluster) {
    const canonical = pickedCanonical(cluster);
    const variants = cluster.variants.map((v) => v.name).filter((n) => n !== canonical);
    return run(() => mergeArtists(canonical, variants));
  }

  function dismiss(cluster: ArtistDuplicateCluster) {
    return run(() => dismissArtistDuplicates(cluster.variants.map((v) => v.name)));
  }
</script>

{#if loading}
  <div class="flex flex-1 items-center justify-center p-8">
    <div class="text-muted-foreground flex items-center gap-2 text-sm">
      <Loader2 class="size-5 animate-spin" /> Scanning artists…
    </div>
  </div>
{:else if error}
  <div class="flex flex-1 items-center justify-center p-8">
    <div class="max-w-md text-center">
      <p class="text-destructive mb-3 text-sm">{error}</p>
      <Button onclick={load}>Retry</Button>
    </div>
  </div>
{:else if total === 0}
  <div class="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
    <span class="bg-primary/10 text-primary grid size-12 place-items-center rounded-full">
      <Check class="size-6" />
    </span>
    <div class="text-[15px] font-semibold">No artist duplicates found</div>
    <p class="text-muted-foreground max-w-sm text-[12.5px]">
      Variant spellings of one artist ("JAY-Z" / "JAYZ") and combined credits registered as a single
      artist ("A &amp; B") show up here with a one-click fix.
    </p>
  </div>
{:else}
  <div class="min-h-0 flex-1 overflow-y-auto px-4 py-4 pb-[calc(1rem_+_var(--mh-content-pad))] sm:px-6">
    <div class="mb-3 flex items-center justify-between gap-2">
      <span class="text-muted-foreground text-[11px]">
        {report!.clusters.length} spelling cluster{report!.clusters.length === 1 ? '' : 's'}
        · {report!.combinedCredits.length} combined credit{report!.combinedCredits.length === 1 ? '' : 's'}
      </span>
      <button
        type="button"
        onclick={load}
        title="Refresh"
        class="text-muted-foreground hover:bg-accent hover:text-foreground grid size-7 place-items-center rounded-md transition-colors"
      >
        <RefreshCw class="size-3.5" />
      </button>
    </div>

    {#if actionError}
      <p class="text-destructive mb-3 text-[12px]">{actionError}</p>
    {/if}

    <div class="space-y-3">
      {#each report!.clusters as cluster (cluster.suggestedCanonical)}
        <div class="border-border bg-card rounded-lg border p-4">
          <div class="mb-2 flex flex-wrap items-center justify-between gap-2">
            <div class="flex min-w-0 items-center gap-2">
              <Users class="text-muted-foreground size-4 shrink-0" />
              <span class="truncate text-[14px] font-semibold">{cluster.suggestedCanonical}</span>
              <span class="text-muted-foreground text-[11px]">
                {cluster.variants.length} spellings · {cluster.variants.reduce((s, v) => s + v.songCount, 0)} songs
              </span>
            </div>
            <div class="flex shrink-0 items-center gap-2">
              <Button size="sm" class="h-7 px-2.5 text-[12px]" disabled={acting} onclick={() => merge(cluster)}>
                <Merge class="mr-1 size-3.5" /> Merge into “{pickedCanonical(cluster)}”
              </Button>
              <Button variant="outline" size="sm" class="h-7 px-2.5 text-[12px]" disabled={acting} onclick={() => dismiss(cluster)}>
                Not the same
              </Button>
            </div>
          </div>
          {#if cluster.evidence.length > 0}
            <div class="mb-2 flex flex-wrap gap-1">
              {#each cluster.evidence as why, whyIdx (whyIdx)}
                <span class="bg-accent text-muted-foreground rounded-sm px-1.5 py-px text-[10px]">{why}</span>
              {/each}
            </div>
          {/if}
          <div class="divide-border divide-y">
            {#each cluster.variants as variant (variant.name)}
              <label class="flex cursor-pointer items-center gap-3 py-1.5">
                <input
                  type="radio"
                  name={`canonical-${cluster.suggestedCanonical}`}
                  checked={pickedCanonical(cluster) === variant.name}
                  onchange={() => (canonicalPick = { ...canonicalPick, [cluster.suggestedCanonical]: variant.name })}
                  class="accent-primary size-3.5"
                />
                <span class={cn('min-w-0 flex-1 truncate text-[13px]', pickedCanonical(cluster) === variant.name && 'font-medium')}>
                  {variant.name}
                </span>
                {#if variant.musicBrainzIds.length > 0}
                  <span class="bg-primary/10 text-primary rounded-sm px-1.5 py-px text-[10px]">MBID</span>
                {/if}
                <span class="text-muted-foreground shrink-0 text-[11.5px] tabular-nums">
                  {variant.songCount} song{variant.songCount === 1 ? '' : 's'}
                </span>
              </label>
            {/each}
          </div>
          <p class="text-muted-foreground mt-2 text-[11px]">
            Merging rewrites the artist tags on every affected song and re-tags built files in place.
          </p>
        </div>
      {/each}

      {#if report!.combinedCredits.length > 0}
        <div class="text-muted-foreground mt-5 mb-1.5 text-[12px] font-medium">
          Combined credits registered as one artist
        </div>
        {#each report!.combinedCredits as credit (credit.credit)}
          <div class="border-border bg-card flex flex-wrap items-center justify-between gap-2 rounded-lg border p-4">
            <div class="min-w-0">
              <div class="truncate text-[13.5px] font-medium">{credit.credit}</div>
              <div class="text-muted-foreground text-[11.5px]">
                Splits into {credit.parts.join(' · ')} — {credit.songCount} song{credit.songCount === 1 ? '' : 's'}
              </div>
            </div>
            <Button
              variant="outline"
              size="sm"
              class="h-7 shrink-0 px-2.5 text-[12px]"
              disabled={acting}
              onclick={() => run(() => splitArtistCredit(credit.credit))}
            >
              <Split class="mr-1 size-3.5" /> Split credit
            </Button>
          </div>
        {/each}
      {/if}
    </div>
  </div>
{/if}

<script lang="ts">
  import { untrack } from 'svelte';
  import { Check, Loader2, RefreshCw, Merge, Split } from '@lucide/svelte';
  import {
    fetchArtistDuplicates,
    mergeArtists,
    splitArtistCredit,
    dismissArtistDuplicates,
    type ArtistDuplicateReport,
    type ArtistDuplicateCluster,
    type CombinedCreditCandidate
  } from '$lib/api-client';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Badge } from '$lib/components/ui/badge';
  import { Button } from '$lib/components/ui/button';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { toast } from 'svelte-sonner';
  import { cn } from '$lib/utils';
  import InboxDedupHistoryV2 from './InboxDedupHistoryV2.svelte';
  import InboxQueueStates from './InboxQueueStates.svelte';
  import { snapshotDedupKeys, toastWithUndo } from './dedup-undo';
  import { radioGroup, radioTabIndex } from '$lib/components/review/radio-group';

  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();

  let report = $state<ArtistDuplicateReport | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  // Which card is acting, and the error it got: shown in that card, next to the button pressed,
  // rather than at the top of a long scroll where a phone would never see it.
  let actingKey = $state<string | null>(null);
  let actionError = $state<{ key: string; message: string } | null>(null);
  let historyVersion = $state(0);
  // Per-cluster canonical pick, keyed by the cluster's suggested canonical (stable per load).
  let canonicalPick = $state<Record<string, string>>({});

  const total = $derived((report?.clusters.length ?? 0) + (report?.combinedCredits.length ?? 0));

  // untrack the callback identity — see InboxTagReviewV2 for the loop this avoids.
  $effect(() => {
    const n = loading ? null : total;
    untrack(() => oncount?.(n));
  });

  async function load(quiet = false) {
    try {
      if (!quiet) loading = true;
      error = null;
      report = await fetchArtistDuplicates();
      canonicalPick = {};
    } catch (err) {
      if (quiet) toast.error(err instanceof Error ? err.message : 'Failed to reload artists');
      else error = err instanceof Error ? err.message : 'Failed to load artist duplicates';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  function reloadAll() {
    historyVersion += 1;
    void load(true);
  }

  function pickedCanonical(cluster: ArtistDuplicateCluster): string {
    return canonicalPick[cluster.suggestedCanonical] ?? cluster.suggestedCanonical;
  }

  // One decision at a time. The queue reloads in place (no skeleton flash) and the history below
  // picks up the new batch.
  async function run(key: string, action: () => Promise<void>) {
    if (actingKey) return;
    try {
      actingKey = key;
      actionError = null;
      await action();
      reloadAll();
    } catch (err) {
      actionError = { key, message: err instanceof Error ? err.message : 'Action failed' };
    } finally {
      actingKey = null;
    }
  }

  function merge(cluster: ArtistDuplicateCluster) {
    const canonical = pickedCanonical(cluster);
    const variants = cluster.variants.map((v) => v.name).filter((n) => n !== canonical);
    return run(cluster.suggestedCanonical, async () => {
      const before = await snapshotDedupKeys();
      await mergeArtists(canonical, variants);
      await toastWithUndo(`Merged into “${canonical}”`, 'artist-merge', before, reloadAll);
    });
  }

  function dismiss(cluster: ArtistDuplicateCluster) {
    return run(cluster.suggestedCanonical, async () => {
      await dismissArtistDuplicates(cluster.variants.map((v) => v.name));
      toast.success('Marked as different artists');
    });
  }

  function split(credit: string) {
    return run(`credit:${credit}`, async () => {
      const before = await snapshotDedupKeys();
      await splitArtistCredit(credit);
      await toastWithUndo(`Split “${credit}”`, 'artist-credit-split', before, reloadAll);
    });
  }

  const partList = new Intl.ListFormat('en', { type: 'conjunction' });

  // What Split does, said on the row: the artists it lists separately, and — where the credit is
  // the album artist — that those tracks are filed under the lead instead of a folder of their own.
  function creditSummary(credit: CombinedCreditCandidate): string {
    const tracks = `${credit.songCount} track${credit.songCount === 1 ? '' : 's'}`;
    const moved = credit.albumArtistSongCount ?? 0;
    const filing =
      moved === 0
        ? ''
        : `, files ${moved === credit.songCount ? '' : `${moved} `}under ${credit.parts[0]}`;
    return `Splits into ${partList.format(credit.parts)}${filing} — ${tracks}`;
  }

  function clusterHeader(cluster: ArtistDuplicateCluster): string {
    const tracks = cluster.variants.reduce((s, v) => s + v.songCount, 0);
    return `${cluster.suggestedCanonical} · ${cluster.variants.length} spellings · ${tracks} track${tracks === 1 ? '' : 's'}`;
  }

  const meta = $derived(
    report && !loading
      ? `${report.clusters.length} spelling cluster${report.clusters.length === 1 ? '' : 's'} · ${
          report.combinedCredits.length
        } combined credit${report.combinedCredits.length === 1 ? '' : 's'}`
      : undefined
  );
</script>

{#snippet refreshAction()}
  <Button
    variant="ghost"
    size="icon"
    aria-label="Refresh artist duplicates"
    title="Refresh"
    onclick={() => void load()}
  >
    <RefreshCw />
  </Button>
{/snippet}

{#snippet cardError(key: string)}
  {#if actionError?.key === key}
    <span role="alert" class="text-destructive-text mt-1 block">{actionError.message}</span>
  {/if}
{/snippet}

<!-- A single-level list of decision cards at every width (no push): each card holds its own
     choice and its actions, so there is nothing for a detail page to add. -->
<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1" viewportClass="overscroll-contain">
    <PageToolbarV2 title="Artist names" {meta} grouped actions={refreshAction} />

    <div class="mx-auto flex w-full max-w-3xl flex-col gap-7 pt-2 pb-8 md:px-6 md:pt-6">
      {#if loading}
        <div
          role="status"
          class="text-body text-muted-foreground flex items-center justify-center gap-2 py-16 md:text-sm"
        >
          <Loader2 class="size-5 animate-spin" /> Scanning artists…
        </div>
      {:else if error}
        <InboxQueueStates state="error" message={error} onretry={load} />
      {:else if total === 0}
        <InboxQueueStates state="empty" icon={Check} title="No artist duplicates found">
          Variant spellings of one artist ("JAY-Z" / "JAYZ") and combined credits registered as a
          single artist ("A &amp; B", "Hef met Jayh") show up here with a one-step fix.
        </InboxQueueStates>
      {:else if report}
        {#each report.clusters as cluster (cluster.suggestedCanonical)}
          {@const canonical = pickedCanonical(cluster)}
          {@const busy = actingKey === cluster.suggestedCanonical}
          <GroupedList.Section header={clusterHeader(cluster)}>
            <!-- Pick the spelling to keep: a single-choice list, the choice a trailing check — a
                 radio group (one Tab stop, arrows move and pick, "2 of 3, checked"), as the
                 native radios it replaced were. The last spelling keeps its hairline: the Merge
                 row follows it in the same card. -->
            {@const chosenIndex = cluster.variants.findIndex((v) => v.name === canonical)}
            <div
              role="radiogroup"
              aria-label="Spelling to keep for {cluster.suggestedCanonical}"
              use:radioGroup
              class="[&>[data-slot=grouped-list-row]:last-child]:after:block"
            >
              {#each cluster.variants as variant, vi (variant.name)}
                {@const chosen = canonical === variant.name}
                <GroupedList.Row
                  onclick={() =>
                    (canonicalPick = {
                      ...canonicalPick,
                      [cluster.suggestedCanonical]: variant.name
                    })}
                  role="radio"
                  aria-checked={chosen}
                  tabindex={radioTabIndex(vi, chosenIndex)}
                  disabled={actingKey != null}
                >
                  <span class={cn('text-body md:text-sm', chosen && 'font-semibold')}>
                    {variant.name}
                  </span>
                  <span class="text-footnote text-muted-foreground tabular-nums md:text-xs">
                    {variant.songCount} track{variant.songCount === 1 ? '' : 's'}
                  </span>
                  {#snippet trailing()}
                    <span class="flex items-center gap-2">
                      {#if variant.musicBrainzIds.length > 0}
                        <!-- The abbreviation is for the eye; assistive tech gets the words, and the
                           footer spells it out. Grey, not tinted: it is a fact about the spelling,
                           not something to tap. -->
                        <Badge variant="secondary">
                          <span aria-hidden="true">MBID</span><span class="sr-only"
                            >Has a MusicBrainz ID</span
                          >
                        </Badge>
                      {/if}
                      <Check
                        class={cn('text-primary size-5 md:size-4', !chosen && 'invisible')}
                        strokeWidth={2.5}
                        aria-hidden="true"
                      />
                    </span>
                  {/snippet}
                </GroupedList.Row>
              {/each}
            </div>
            <GroupedList.Row disabled={actingKey != null} onclick={() => merge(cluster)}>
              <span class="text-body text-primary flex min-w-0 items-center gap-2 md:text-sm">
                {#if busy}<Loader2 class="size-4 shrink-0 animate-spin" />{:else}<Merge
                    class="size-4 shrink-0"
                    aria-hidden="true"
                  />{/if}
                <span class="min-w-0 break-words">Merge into “{canonical}”</span>
              </span>
            </GroupedList.Row>
            <GroupedList.Row
              label="Not the same"
              disabled={actingKey != null}
              onclick={() => dismiss(cluster)}
            />
            {#snippet footer()}
              {#if cluster.evidence.length > 0}
                <p>{cluster.evidence.join(' · ')}</p>
              {/if}
              <p class={cn(cluster.evidence.length > 0 && 'mt-1')}>
                Merging rewrites the artist tags on every affected track and re-tags built files in
                place.
                {#if cluster.variants.some((v) => v.musicBrainzIds.length > 0)}
                  MBID marks a spelling that has a MusicBrainz ID.
                {/if}
              </p>
              {@render cardError(cluster.suggestedCanonical)}
            {/snippet}
          </GroupedList.Section>
        {/each}

        {#if report.combinedCredits.length > 0}
          <GroupedList.Section
            header="Combined credits registered as one artist"
            footer="Splitting lists each artist on the track separately. Where the credit is also the album artist, the tracks are filed under the first artist instead. Built files are re-tagged, and moved when their folder changes."
          >
            {#each report.combinedCredits as credit (credit.credit)}
              {@const key = `credit:${credit.credit}`}
              <GroupedList.Row label={credit.credit} sublabel={creditSummary(credit)}>
                {@render cardError(key)}
                {#snippet trailing()}
                  <Button
                    variant="outline"
                    size="sm"
                    class="h-11 gap-1.5 rounded-full px-4 text-[15px] md:h-7 md:rounded-lg md:px-2.5 md:text-[12px]"
                    disabled={actingKey != null}
                    onclick={() => split(credit.credit)}
                  >
                    {#if actingKey === key}<Loader2
                        class="size-4 animate-spin md:size-3.5"
                      />{:else}<Split class="size-4 md:size-3.5" aria-hidden="true" />{/if}
                    Split credit
                  </Button>
                {/snippet}
              </GroupedList.Row>
            {/each}
          </GroupedList.Section>
        {/if}
      {/if}

      {#if !loading}
        <InboxDedupHistoryV2 refresh={historyVersion} onreverted={() => void load(true)} />
      {/if}
    </div>
  </ScrollArea>
</div>

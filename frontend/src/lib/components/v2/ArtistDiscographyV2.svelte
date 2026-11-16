<script lang="ts">
  import { untrack } from 'svelte';
  import { Check, CircleCheck, Clock, Download, LoaderCircle, RotateCw } from '@lucide/svelte';
  import * as AlertDialog from '#lib/components/ui/alert-dialog/index.ts';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { Spinner } from '#lib/components/ui/spinner/index.ts';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import FilterChip from '#lib/components/v2/FilterChip.svelte';
  import type { ArtistRelease } from '#lib/api-client.ts';
  import {
    acquirableReleases,
    availableFilters,
    canAcquire,
    catalogArtistCaption,
    DISCOGRAPHY_FILTERS,
    discographySummary,
    filterReleases,
    releaseCaption,
    releaseState,
    releaseStatusLabel,
    type DiscographyFilter
  } from '#lib/artist-discography.ts';
  import type { ArtistPageState } from '#lib/stores/artist-page.svelte.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    state: ArtistPageState;
    /** The album page of a release you own, when the library shows it. */
    hrefFor: (release: ArtistRelease) => string | null;
    compact: boolean;
  };
  const { state: page, hrefFor, compact }: Props = $props();

  const data = $derived(page.data);
  const releases = $derived(data?.releases ?? []);
  const filters = $derived(availableFilters(releases));

  let filter = $state<DiscographyFilter>('all');
  let missingOnly = $state(false);
  // A filter that the next artist has nothing for falls back to All rather than an empty shelf.
  $effect(() => {
    if (!filters.includes(untrack(() => filter))) filter = 'all';
  });

  const shown = $derived(filterReleases(releases, filter, missingOnly));
  // Two rows' worth of a desktop grid, or a first stretch of a phone shelf, then "Show all": a
  // prolific artist has hundreds of releases, and every card is a cover and a button to mount.
  const GRID_PREVIEW = 12;
  let showAll = $state(false);
  const gridShown = $derived(showAll ? shown : shown.slice(0, GRID_PREVIEW));
  const toGet = $derived(acquirableReleases(shown));
  const canDownload = $derived(Boolean(data?.downloadsEnabled));
  const missingTracksHint = $derived(
    toGet.reduce(
      (n, r) => n + (r.totalTracks != null ? Math.max(0, r.totalTracks - r.ownedTracks) : 0),
      0
    )
  );

  let confirmAll = $state(false);
  const filterLabel = $derived(DISCOGRAPHY_FILTERS.find((f) => f.id === filter)?.label ?? 'All');
</script>

<section aria-labelledby="artist-discography-heading" class="flex flex-col gap-2">
  <div class="flex flex-wrap items-end justify-between gap-x-3 gap-y-1 px-4 md:px-0">
    <div class="min-w-0">
      <h2 id="artist-discography-heading" class="text-title-2 md:text-lg">Discography</h2>
      {#if releases.length > 0}
        <p class="text-muted-foreground text-footnote md:text-xs">{discographySummary(releases)}</p>
      {/if}
    </div>
    {#if canDownload && toGet.length > 1}
      <Button
        variant="tinted"
        size={compact ? 'default' : 'sm'}
        class="rounded-full px-3"
        onclick={() => (confirmAll = true)}
      >
        <Download class="size-4" />
        Get all missing
      </Button>
    {/if}
  </div>

  {#if page.loading && !data}
    <div
      class="flex gap-3 overflow-hidden px-4 pt-2 md:px-0"
      aria-busy="true"
      aria-label="Loading the discography"
    >
      {#each { length: 5 } as _, i (i)}
        <div class="flex w-[140px] shrink-0 flex-col gap-2 md:w-[160px]">
          <div class="bg-muted aspect-square animate-pulse rounded-md"></div>
          <div class="bg-muted h-3 w-3/4 animate-pulse rounded"></div>
          <div class="bg-muted h-3 w-1/2 animate-pulse rounded"></div>
        </div>
      {/each}
    </div>
  {:else if page.error && !data}
    <div class="flex items-center gap-3 px-4 md:px-0">
      <p class="text-destructive-text text-subheadline md:text-sm">{page.error}</p>
      <Button
        variant="gray"
        size="sm"
        class="rounded-full"
        onclick={() => page.artist && void page.load(page.artist)}
      >
        <RotateCw class="size-4" /> Retry
      </Button>
    </div>
  {:else if data && data.catalog.status === 'unconfirmed'}
    <!-- The name alone did not prove which catalog artist this is: ask, rather than guess. -->
    <div class="bg-card mx-4 rounded-xl p-4 md:mx-0" data-elevated="">
      <p class="text-headline md:text-sm md:font-semibold">Which {data.artist} is this?</p>
      <p class="text-muted-foreground text-subheadline mt-0.5 md:text-xs">
        No catalog artist by this name lists anything you own from them. Pick the right one to see
        their releases.
      </p>
      <ul class="mt-3 flex flex-col">
        {#each data.candidates as candidate (candidate.id)}
          <li>
            <button
              type="button"
              class="hover:bg-accent focus-visible:ring-ring flex min-h-14 w-full items-center gap-3 rounded-lg px-2 text-left focus-visible:ring-2 focus-visible:outline-hidden"
              onclick={() => void page.choose(candidate.id)}
            >
              <Cover
                artist={candidate.name ?? data.artist}
                title={candidate.name ?? data.artist}
                coverUrl={candidate.pictureUrl}
                size={40}
                corner={20}
                caption={false}
              />
              <span class="min-w-0 flex-1">
                <span class="text-body block truncate md:text-sm">{candidate.name}</span>
                <span class="text-muted-foreground text-footnote block md:text-xs">
                  {catalogArtistCaption(candidate)}
                </span>
              </span>
              <span class="text-primary text-subheadline md:text-sm">This one</span>
            </button>
          </li>
        {/each}
      </ul>
    </div>
  {:else if data && data.catalog.status === 'not-found'}
    <p class="text-muted-foreground text-subheadline px-4 md:px-0 md:text-sm">
      No catalog artist goes by this name, so there is no discography to show. Under another name?
      <a href="/following" class="text-primary hover:underline">Search the catalog</a>.
    </p>
  {:else if data?.releasesUnavailable}
    <div class="flex items-center gap-3 px-4 md:px-0">
      <p class="text-muted-foreground text-subheadline md:text-sm">
        The catalog could not be reached just now.
      </p>
      <Button
        variant="gray"
        size="sm"
        class="rounded-full"
        onclick={() => page.artist && void page.load(page.artist)}
      >
        <RotateCw class="size-4" /> Retry
      </Button>
    </div>
  {:else if data && releases.length > 0}
    <!-- One band: sideways on a phone (data-scroll-x keeps the edge swipe out of it), wrapping on md+.
         The vertical padding is the room each chip's 44pt touch area needs. -->
    <div
      class="flex [scrollbar-width:none] items-center gap-2 overflow-x-auto overflow-y-hidden px-4 py-1.5 md:flex-wrap md:overflow-visible md:px-0"
      data-scroll-x=""
    >
      {#each DISCOGRAPHY_FILTERS.filter((f) => filters.includes(f.id)) as f (f.id)}
        <FilterChip pressed={filter === f.id} onclick={() => (filter = f.id)}>{f.label}</FilterChip>
      {/each}
      <FilterChip pressed={missingOnly} onclick={() => (missingOnly = !missingOnly)}
        >Missing only</FilterChip
      >
    </div>

    {#if shown.length === 0}
      <p class="text-muted-foreground text-subheadline px-4 py-6 md:px-0 md:text-sm">
        You have every {filterLabel === 'All' ? 'release' : filterLabel.toLowerCase()} here.
      </p>
    {:else if compact}
      <ScrollArea
        orientation="horizontal"
        data-scroll-x=""
        class="[&>[data-slot=scroll-area-viewport]]:snap-x [&>[data-slot=scroll-area-viewport]]:scroll-px-4"
      >
        <ul class="flex gap-3 px-4 pt-1 pb-3">
          {#each gridShown as release (release.key)}
            <li class="w-[148px] shrink-0 snap-start">{@render card(release)}</li>
          {/each}
          {#if shown.length > gridShown.length}
            <li class="flex w-[148px] shrink-0 snap-start">
              <button
                type="button"
                class="bg-secondary hover:bg-secondary-hover text-subheadline aspect-square w-full rounded-md font-semibold"
                onclick={() => (showAll = true)}
              >
                Show all {shown.length}
              </button>
            </li>
          {/if}
        </ul>
      </ScrollArea>
    {:else}
      <ul class="grid grid-cols-[repeat(auto-fill,minmax(156px,1fr))] gap-x-4 gap-y-5 pt-1">
        {#each gridShown as release (release.key)}
          <li>{@render card(release)}</li>
        {/each}
      </ul>
      {#if shown.length > GRID_PREVIEW}
        <button
          type="button"
          class="text-muted-foreground hover:text-foreground self-start text-sm font-semibold"
          onclick={() => (showAll = !showAll)}
        >
          {showAll ? 'Show less' : `Show all ${shown.length}`}
        </button>
      {/if}
    {/if}
  {:else if data}
    <p class="text-muted-foreground text-subheadline px-4 md:px-0 md:text-sm">
      The catalog lists no releases yet.
    </p>
  {/if}
</section>

{#snippet card(release: ArtistRelease)}
  {@const state = releaseState(release)}
  {@const href = state === 'complete' || state === 'partial' ? hrefFor(release) : null}
  {@const status = releaseStatusLabel(release)}
  {@const busy = page.acquiring.has(release.key)}
  <div class="group relative flex flex-col gap-1.5">
    <div class="relative">
      {#if href}
        <a
          {href}
          class="focus-visible:ring-ring block rounded-md focus-visible:ring-2 focus-visible:outline-hidden"
          aria-label={`Open ${release.title}`}
        >
          {@render art(release, state)}
        </a>
      {:else}
        {@render art(release, state)}
      {/if}

      {#if canDownload && (canAcquire(release) || busy)}
        <!-- The one action a missing release has. At rest on every card: there is no hover on a phone. -->
        <button
          type="button"
          class="bg-primary text-primary-foreground focus-visible:ring-ring absolute right-2 bottom-2 grid size-11 place-items-center rounded-full shadow-[0_4px_14px_rgb(0_0_0/0.3)] transition-transform duration-150 focus-visible:ring-2 focus-visible:outline-hidden active:scale-95 disabled:opacity-80 md:size-10 md:hover:scale-105"
          aria-label={state === 'partial'
            ? `Get the rest of ${release.title}`
            : `Get ${release.title}`}
          title={state === 'partial' ? 'Get the missing tracks' : 'Get this release'}
          disabled={busy}
          onclick={() => void page.acquire([release])}
        >
          {#if busy}<Spinner class="size-5" />{:else}<Download class="size-5" />{/if}
        </button>
      {/if}
    </div>

    <div class="min-w-0">
      {#if href}
        <a {href} class="text-subheadline block truncate font-semibold hover:underline md:text-sm"
          >{release.title}</a
        >
      {:else}
        <p class="text-subheadline truncate font-semibold md:text-sm">{release.title}</p>
      {/if}
      <p class="text-muted-foreground text-footnote truncate md:text-xs">
        {releaseCaption(release)}{release.explicit ? ' · E' : ''}
      </p>
      {#if status}
        <p
          class={cn(
            'text-footnote mt-0.5 flex items-center gap-1 truncate md:text-xs',
            state === 'complete' ? 'text-muted-foreground' : 'text-foreground'
          )}
        >
          {#if state === 'complete'}
            <CircleCheck class="text-primary size-3.5 shrink-0" aria-hidden="true" />
          {:else if state === 'queued'}
            <LoaderCircle
              class="text-primary size-3.5 shrink-0 animate-spin motion-reduce:animate-none"
              aria-hidden="true"
            />
          {:else if state === 'upcoming'}
            <Clock class="size-3.5 shrink-0" aria-hidden="true" />
          {:else if state === 'partial'}
            <Check class="size-3.5 shrink-0" aria-hidden="true" />
          {/if}
          <span class="truncate">{status}</span>
        </p>
      {/if}
    </div>
  </div>
{/snippet}

{#snippet art(release: ArtistRelease, state: string)}
  <Cover
    artist={page.data?.catalog.name ?? page.artist ?? ''}
    title={release.title}
    coverUrl={release.coverUrl}
    size={180}
    corner={8}
    caption={false}
    class={cn(
      'aspect-square !h-auto !w-full shadow-[0_2px_10px_rgb(0_0_0/0.15)]',
      state === 'missing' && 'opacity-90'
    )}
  />
{/snippet}

<AlertDialog.Root bind:open={confirmAll}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>
        Get {toGet.length}
        {filter === 'all' ? 'releases' : filterLabel.toLowerCase()} by {data?.catalog.name ??
          page.artist}?
      </AlertDialog.Title>
      <AlertDialog.Description>
        The missing tracks of every {filter === 'all'
          ? 'release'
          : filterLabel.toLowerCase().replace(/s$/, '')}
        shown are added to the wishlist and downloaded{missingTracksHint > 0
          ? ` — at least ${missingTracksHint} tracks`
          : ''}. Tracks you already have, or that are already on their way, are skipped. One request
        has a track limit; run it again for the rest.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action onclick={() => void page.acquire(toGet)}
        >Get {toGet.length} releases</AlertDialog.Action
      >
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<script lang="ts">
  import { page } from '$app/state';
  import type { SpotifyApiPlaylist, SpotifyApiTrack } from '$lib/api-client';
  import { fetchSpotifyPlaylistTracks } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import { SearchField } from '$lib/components/ui/search-field';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Clock, AlertCircle, ListMusic } from '@lucide/svelte';
  import { albumTint } from '$lib/album-tint';
  import { computeInitials, formatTotalDuration } from '$lib/formatters';
  import SpotifyTrackRow from './SpotifyTrackRow.svelte';
  import PaginationControls from './PaginationControls.svelte';
  import TrackListSkeleton from './TrackListSkeleton.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { heroTitle } from '$lib/components/discover/hero-title';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';

  // A Spotify playlist, as a page pushed from the Playlists grid (/spotify?tab=playlists&
  // playlist=<id>): its own nav bar with Back to the grid, the cover centred on a phone.
  type Props = { playlist: SpotifyApiPlaylist; backHref: string };
  const { playlist, backHref }: Props = $props();

  const limit = 50;

  // The playlist names its page (Back labels, the browser-tab title), as Discover's does.
  // titleOf is read first so the effect runs again once the navigation is recorded.
  $effect(() => {
    const title = playlist.name;
    if (title && tabMemory.titleOf(page.url) !== title) tabMemory.setTitle(page.url, title);
  });

  // The filter sits in the desktop bar; on a phone it goes under the hero, over the list it
  // filters, rather than above the cover.
  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);
  // On a phone the bar's inline title waits until the hero's title has scrolled under it (see
  // heroTitle); md+ keeps the desktop toolbar's title.
  let heroVisible = $state(true);

  let tracks = $state<SpotifyApiTrack[]>([]);
  let total = $state(0);
  let offset = $state(0);
  let isLoading = $state(true);
  let error = $state<string | null>(null);
  let searchQuery = $state('');
  let expandedMatchIds = $state(new Set<string>());

  async function loadTracks(newOffset: number) {
    isLoading = true;
    error = null;
    try {
      const result = await fetchSpotifyPlaylistTracks(playlist.spotifyId, newOffset, limit);
      tracks = result.items;
      total = result.total;
      offset = result.offset;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load tracks';
    } finally {
      isLoading = false;
    }
  }

  $effect(() => {
    void loadTracks(0);
  });

  const filteredTracks = $derived(
    searchQuery
      ? tracks.filter(
          (t) =>
            t.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
            t.artist.toLowerCase().includes(searchQuery.toLowerCase()) ||
            t.album.toLowerCase().includes(searchQuery.toLowerCase())
        )
      : tracks
  );

  function toggleExpand(id: string) {
    const next = new Set(expandedMatchIds);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    expandedMatchIds = next;
  }

  const tint = $derived(albumTint(playlist.ownerName ?? 'Spotify', playlist.name));
  const initials = $derived(computeInitials(playlist.name));

  // Total duration is summed from the *loaded page* — Spotify's playlist endpoint
  // doesn't return a total in the list response, so the figure reflects what's
  // visible and grows as the user paginates.
  const visibleDurationSeconds = $derived(
    Math.floor(tracks.reduce((acc, t) => acc + (t.durationMs ?? 0), 0) / 1000)
  );

  const meta = $derived(
    [
      playlist.ownerName,
      `${playlist.trackCount} song${playlist.trackCount === 1 ? '' : 's'}`,
      visibleDurationSeconds > 0
        ? `${formatTotalDuration(visibleDurationSeconds)}${tracks.length < playlist.trackCount ? '+' : ''}`
        : null
    ]
      .filter(Boolean)
      .join(' · ')
  );
</script>

{#snippet filterField()}
  <SearchField bind:value={searchQuery} label="Filter this page" />
{/snippet}

<div class="flex min-h-0 flex-1 flex-col">
  <div
    class="hero-scroller min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)"
    data-hero-visible={compact && heroVisible ? '' : undefined}
  >
    <PageToolbarV2
      title={playlist.name}
      largeTitle={false}
      back={{ label: 'Spotify', href: backHref }}
      search={compact ? undefined : filterField}
    />

    <!-- Hero: the cover centred on a phone, beside the title on desktop. -->
    <div
      class="flex flex-col items-center px-4 pt-3 pb-5 text-center md:flex-row md:items-end md:gap-7 md:px-7 md:pt-7 md:text-left"
    >
      <div
        class="relative grid size-44 shrink-0 place-items-center overflow-hidden rounded-md shadow-[0_12px_32px_rgb(0_0_0/0.22)] md:size-40"
        style="background: linear-gradient(135deg, {tint.from} 0%, {tint.to} 100%);"
      >
        {#if playlist.imageUrl}
          <img
            src={playlist.imageUrl}
            alt=""
            crossorigin="anonymous"
            draggable="false"
            class="absolute inset-0 size-full object-cover"
          />
        {:else}
          <div class="text-4xl font-bold tracking-[-0.04em] text-white">{initials}</div>
          <div
            class="absolute right-[8%] bottom-[7%] left-[8%] truncate text-center text-[11px] font-medium text-white"
          >
            {playlist.ownerName ?? 'Spotify'}
          </div>
        {/if}
      </div>
      <div class="mt-4 min-w-0 md:mt-0 md:pb-1">
        <h2
          use:heroTitle={(v) => (heroVisible = v)}
          class="text-title-2 text-balance break-words md:text-[26px] md:leading-8"
        >
          {playlist.name}
        </h2>
        <p class="text-subheadline text-muted-foreground mt-1 md:text-sm">{meta}</p>
        {#if playlist.description}
          <p
            class="text-footnote text-muted-foreground mx-auto mt-2 max-w-md text-pretty md:mx-0 md:max-w-2xl md:text-[13px]"
          >
            {playlist.description}
          </p>
        {/if}
        <!-- There is no playback for a Spotify playlist yet. This used to be a disabled Play button
             explained only by a hover title, so a tap did nothing; say it in words instead. -->
        <p class="text-footnote text-muted-foreground mt-3 md:text-xs">
          Playing Spotify playlists here is coming soon.
        </p>
      </div>
    </div>

    {#if compact}
      <div class="px-4 pb-2">{@render filterField()}</div>
    {/if}

    {#if error}
      <div class="flex flex-col items-center justify-center px-6 py-12 text-center">
        <AlertCircle class="text-destructive-text mb-3 size-10" aria-hidden="true" />
        <p class="text-body text-muted-foreground md:text-sm">{error}</p>
        <Button
          variant="outline"
          class="mt-4 h-11 rounded-full px-5 md:h-8 md:rounded-lg md:px-3"
          onclick={() => loadTracks(offset)}
        >
          Retry
        </Button>
      </div>
    {:else if isLoading}
      <TrackListSkeleton />
    {:else}
      <div
        class="text-muted-foreground border-separator hidden items-center gap-3 border-b px-6 py-2 text-xs md:mx-4 md:flex md:px-3"
      >
        <span class="w-8 text-right">#</span>
        <span class="size-10"></span>
        <span class="flex-1"
          >Title · {searchQuery ? `${filteredTracks.length} of ` : ''}{total}</span
        >
        <span class="hidden max-w-[200px] md:block">Album</span>
        <span class="w-12 text-right"><Clock class="inline size-3.5" aria-label="Duration" /></span>
        <span class="w-[120px] shrink-0 text-right">Library</span>
      </div>
      <div class="py-1 md:px-4 md:py-2">
        {#each filteredTracks as track, i (`${track.spotifyId}-${i}`)}
          <SpotifyTrackRow
            {track}
            index={offset + i}
            showDateAdded={false}
            expanded={expandedMatchIds.has(track.spotifyId)}
            onToggleExpand={() => toggleExpand(track.spotifyId)}
          />
        {/each}
      </div>
      {#if filteredTracks.length === 0}
        <div class="flex flex-col items-center justify-center py-12 text-center">
          <ListMusic class="text-muted-foreground mb-3 size-10" aria-hidden="true" />
          <p class="text-body text-muted-foreground md:text-sm">No tracks found</p>
        </div>
      {/if}
      <PaginationControls {offset} {limit} {total} onPageChange={loadTracks} {isLoading} />
    {/if}
  </div>
</div>

<style>
  /* The bar's inline title fades in once the hero title has gone under it (Apple Music). */
  .hero-scroller :global([data-mh-navbar] h1) {
    transition: opacity 150ms cubic-bezier(0.23, 1, 0.32, 1);
  }
  .hero-scroller[data-hero-visible] :global([data-mh-navbar] h1) {
    opacity: 0;
  }
</style>

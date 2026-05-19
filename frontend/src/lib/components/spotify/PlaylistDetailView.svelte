<script lang="ts">
  import type { SpotifyApiPlaylist, SpotifyApiTrack } from '$lib/api-client';
  import { fetchSpotifyPlaylistTracks } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { ArrowLeft, Search, Clock, AlertCircle, ListMusic } from '@lucide/svelte';
  import SpotifyTrackRow from './SpotifyTrackRow.svelte';
  import PaginationControls from './PaginationControls.svelte';
  import TrackListSkeleton from './TrackListSkeleton.svelte';

  type Props = { playlist: SpotifyApiPlaylist; onBack: () => void };
  const { playlist, onBack }: Props = $props();

  const limit = 50;

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
</script>

<div class="flex min-h-0 flex-1 flex-col overflow-hidden">
  <div class="border-border flex items-center gap-4 border-b px-4 py-4 md:px-6">
    <Button variant="ghost" size="sm" onclick={onBack} class="shrink-0">
      <ArrowLeft class="mr-1.5 size-4" />
      Back
    </Button>

    <div class="flex min-w-0 items-center gap-3">
      <div class="bg-secondary size-12 shrink-0 overflow-hidden rounded-lg">
        {#if playlist.imageUrl}
          <img
            src={playlist.imageUrl}
            alt={playlist.name}
            class="size-full object-cover"
            crossorigin="anonymous"
          />
        {:else}
          <div class="flex size-full items-center justify-center">
            <ListMusic class="text-muted-foreground size-5" />
          </div>
        {/if}
      </div>
      <div class="min-w-0">
        <h2 class="truncate font-semibold">{playlist.name}</h2>
        <p class="text-muted-foreground text-xs">
          {playlist.trackCount} tracks{playlist.ownerName ? ` · ${playlist.ownerName}` : ''}
        </p>
      </div>
    </div>
  </div>

  <div class="border-border flex items-center gap-3 border-b px-4 py-3 md:px-6">
    <div class="relative max-w-md flex-1">
      <Search class="text-muted-foreground absolute top-1/2 left-3 size-4 -translate-y-1/2" />
      <Input
        placeholder="Filter tracks..."
        bind:value={searchQuery}
        class="bg-secondary border-0 pl-9"
      />
    </div>
    <span class="text-muted-foreground shrink-0 text-sm">{total} tracks</span>
  </div>

  {#if error}
    <div class="flex flex-col items-center justify-center py-12 text-center">
      <AlertCircle class="text-destructive mb-3 size-10" />
      <p class="text-muted-foreground">{error}</p>
      <Button variant="outline" size="sm" class="mt-4" onclick={() => loadTracks(offset)}>
        Retry
      </Button>
    </div>
  {:else if isLoading}
    <TrackListSkeleton />
  {:else}
    <ScrollArea class="min-h-0 flex-1">
      <div
        class="text-muted-foreground border-border/50 hidden items-center gap-3 border-b px-6 py-2 text-xs md:flex"
      >
        <span class="w-8 text-right">#</span>
        <span class="size-10"></span>
        <span class="flex-1">Title</span>
        <span class="hidden max-w-[200px] md:block">Album</span>
        <span class="w-12 text-right"><Clock class="inline size-3.5" /></span>
        <span class="w-[120px] shrink-0 text-right">Library</span>
      </div>
      <div class="flex flex-col gap-2 p-2 md:px-4">
        {#each filteredTracks as track, i (`${track.spotifyId}-${i}`)}
          <SpotifyTrackRow
            {track}
            index={offset + i}
            showDateAdded={false}
            expanded={expandedMatchIds.has(track.spotifyId)}
            onToggleExpand={() => toggleExpand(track.spotifyId)}
          />
        {/each}
        {#if filteredTracks.length === 0}
          <div class="flex flex-col items-center justify-center py-12 text-center">
            <AlertCircle class="text-muted-foreground mb-3 size-10" />
            <p class="text-muted-foreground">No tracks found</p>
          </div>
        {/if}
      </div>
      <PaginationControls {offset} {limit} {total} onPageChange={loadTracks} {isLoading} />
    </ScrollArea>
  {/if}
</div>

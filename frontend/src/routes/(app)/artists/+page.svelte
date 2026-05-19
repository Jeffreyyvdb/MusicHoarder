<script lang="ts">
  import { Search, Disc, Music, ChevronRight } from '@lucide/svelte';
  import { Input } from '$lib/components/ui/input';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { mockArtists } from '$lib/mock-data';

  let searchQuery = $state('');

  const filteredArtists = $derived(
    mockArtists.filter((artist) =>
      artist.name.toLowerCase().includes(searchQuery.toLowerCase())
    )
  );
</script>

<div class="flex min-h-0 flex-1 flex-col overflow-hidden">
  <div class="border-border bg-card/30 border-b px-4 py-6 md:px-6">
    <h1 class="text-2xl font-bold">Artists</h1>
    <p class="text-muted-foreground mt-1 text-sm">
      Compare your library with full artist discographies
    </p>
  </div>

  <div class="border-border border-b px-4 py-3 md:px-6">
    <div class="relative max-w-md">
      <Search
        class="text-muted-foreground absolute top-1/2 left-3 size-4 -translate-y-1/2"
      />
      <Input
        placeholder="Search artists..."
        bind:value={searchQuery}
        class="bg-secondary border-0 pl-9"
      />
    </div>
  </div>

  <ScrollArea class="min-h-0 flex-1">
    <div class="grid grid-cols-1 gap-4 p-4 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 md:p-6">
      {#each filteredArtists as artist (artist.id)}
        {@const completionPercent = Math.round(
          (artist.tracksInLibrary / artist.totalTracks) * 100
        )}
        <a
          href={`/artists/${artist.id}`}
          class="group bg-card border-border hover:border-primary/50 relative overflow-hidden rounded-xl border transition-all hover:shadow-lg"
        >
          <div class="bg-secondary aspect-square overflow-hidden">
            {#if artist.image}
              <img
                src={artist.image}
                alt={artist.name}
                class="size-full object-cover transition-transform group-hover:scale-105"
                crossorigin="anonymous"
              />
            {:else}
              <div class="flex size-full items-center justify-center">
                <Music class="text-muted-foreground size-16" />
              </div>
            {/if}
          </div>

          <div class="p-4">
            <div class="flex items-center justify-between">
              <h3 class="truncate font-semibold">{artist.name}</h3>
              <ChevronRight
                class="text-muted-foreground size-4 opacity-0 transition-opacity group-hover:opacity-100"
              />
            </div>
            <p class="text-muted-foreground truncate text-sm">{artist.genres.join(', ')}</p>

            <div class="text-muted-foreground mt-3 flex items-center gap-4 text-xs">
              <div class="flex items-center gap-1">
                <Disc class="size-3" />
                <span>{artist.albumsInLibrary}/{artist.totalAlbums} albums</span>
              </div>
              <div class="flex items-center gap-1">
                <Music class="size-3" />
                <span>{artist.tracksInLibrary}/{artist.totalTracks} tracks</span>
              </div>
            </div>

            <div class="mt-3">
              <div class="mb-1 flex items-center justify-between text-xs">
                <span class="text-muted-foreground">Library completion</span>
                <span class="text-primary font-medium">{completionPercent}%</span>
              </div>
              <div class="bg-secondary h-1.5 overflow-hidden rounded-full">
                <div
                  class="bg-primary h-full rounded-full transition-all"
                  style="width: {completionPercent}%"
                ></div>
              </div>
            </div>
          </div>
        </a>
      {/each}
    </div>
  </ScrollArea>
</div>

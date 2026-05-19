<script lang="ts">
  import { page } from '$app/state';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as Tabs from '$lib/components/ui/tabs';
  import AlbumCard from '$lib/components/artists/AlbumCard.svelte';
  import { mockArtists, mockBeatlesDiscography } from '$lib/mock-data';
  import {
    ArrowLeft,
    Music,
    Disc,
    CheckCircle2,
    Circle,
    Clock
  } from '@lucide/svelte';

  const artistId = $derived(page.params.id);

  // For demo, use Beatles discography for any artist.
  const discography = mockBeatlesDiscography;

  const artist = $derived(mockArtists.find((a) => a.id === artistId));

  let downloadingTracks = $state(new Set<string>());

  function handleDownloadTrack(trackId: string) {
    downloadingTracks = new Set([...downloadingTracks, trackId]);
    setTimeout(() => {
      const next = new Set(downloadingTracks);
      next.delete(trackId);
      downloadingTracks = next;
    }, 2000);
  }

  function handleDownloadAlbum(albumId: string) {
    const album = discography.find((a) => a.id === albumId);
    if (!album) return;
    const missing = album.tracks.filter((t) => !t.inLibrary);
    for (const t of missing) handleDownloadTrack(t.id);
  }

  const totalTracks = $derived(discography.reduce((s, a) => s + a.totalTracks, 0));
  const ownedTracks = $derived(discography.reduce((s, a) => s + a.tracksOwned, 0));
  const completionPercent = $derived(Math.round((ownedTracks / totalTracks) * 100));
  const albumsWithAll = $derived(discography.filter((a) => a.tracksOwned === a.totalTracks).length);
  const albumsPartial = $derived(
    discography.filter((a) => a.tracksOwned > 0 && a.tracksOwned < a.totalTracks).length
  );
  const albumsMissing = $derived(discography.filter((a) => a.tracksOwned === 0).length);

  const completeAlbums = $derived(discography.filter((a) => a.tracksOwned === a.totalTracks));
  const partialAlbums = $derived(
    discography.filter((a) => a.tracksOwned > 0 && a.tracksOwned < a.totalTracks)
  );
  const missingAlbums = $derived(discography.filter((a) => a.tracksOwned === 0));
</script>

{#if !artist}
  <div class="flex flex-1 items-center justify-center">
    <p class="text-muted-foreground">Artist not found</p>
  </div>
{:else}
  <ScrollArea class="min-h-0 flex-1">
    <div class="p-4 md:p-6">
      <a
        href="/artists"
        class="text-muted-foreground hover:text-foreground mb-4 inline-flex items-center gap-1 text-sm"
      >
        <ArrowLeft class="size-4" />
        Back to Artists
      </a>

      <div class="mb-8 flex flex-col gap-6 sm:flex-row sm:items-end">
        <div class="bg-secondary size-32 shrink-0 overflow-hidden rounded-xl sm:size-48">
          {#if artist.image}
            <img
              src={artist.image}
              alt={artist.name}
              class="size-full object-cover"
              crossorigin="anonymous"
            />
          {:else}
            <div class="flex size-full items-center justify-center">
              <Music class="text-muted-foreground size-16" />
            </div>
          {/if}
        </div>

        <div class="flex-1">
          <p class="text-muted-foreground mb-1 text-sm">Artist</p>
          <h1 class="text-3xl font-bold sm:text-4xl">{artist.name}</h1>
          <p class="text-muted-foreground mt-1">{artist.genres.join(', ')}</p>

          <div class="mt-4 flex flex-wrap gap-4">
            <div class="bg-secondary/50 rounded-lg px-4 py-2">
              <p class="text-primary text-2xl font-bold">{completionPercent}%</p>
              <p class="text-muted-foreground text-xs">Library Complete</p>
            </div>
            <div class="bg-secondary/50 rounded-lg px-4 py-2">
              <p class="text-2xl font-bold">{ownedTracks}</p>
              <p class="text-muted-foreground text-xs">of {totalTracks} tracks</p>
            </div>
            <div class="bg-secondary/50 rounded-lg px-4 py-2">
              <p class="text-2xl font-bold">{discography.length}</p>
              <p class="text-muted-foreground text-xs">Albums</p>
            </div>
          </div>
        </div>
      </div>

      <Tabs.Root value="all">
        <Tabs.List class="mb-4">
          <Tabs.Trigger value="all" class="gap-1.5">
            <Disc class="size-3.5" />
            All ({discography.length})
          </Tabs.Trigger>
          <Tabs.Trigger value="complete" class="gap-1.5">
            <CheckCircle2 class="size-3.5" />
            Complete ({albumsWithAll})
          </Tabs.Trigger>
          <Tabs.Trigger value="partial" class="gap-1.5">
            <Clock class="size-3.5" />
            Partial ({albumsPartial})
          </Tabs.Trigger>
          <Tabs.Trigger value="missing" class="gap-1.5">
            <Circle class="size-3.5" />
            Missing ({albumsMissing})
          </Tabs.Trigger>
        </Tabs.List>

        <Tabs.Content value="all" class="space-y-4">
          {#each discography as album (album.id)}
            <AlbumCard
              {album}
              {downloadingTracks}
              onDownloadTrack={handleDownloadTrack}
              onDownloadAlbum={handleDownloadAlbum}
            />
          {/each}
        </Tabs.Content>

        <Tabs.Content value="complete" class="space-y-4">
          {#each completeAlbums as album (album.id)}
            <AlbumCard
              {album}
              {downloadingTracks}
              onDownloadTrack={handleDownloadTrack}
              onDownloadAlbum={handleDownloadAlbum}
            />
          {/each}
          {#if albumsWithAll === 0}
            <div class="border-border rounded-xl border border-dashed p-8 text-center">
              <p class="text-muted-foreground">No complete albums yet</p>
            </div>
          {/if}
        </Tabs.Content>

        <Tabs.Content value="partial" class="space-y-4">
          {#each partialAlbums as album (album.id)}
            <AlbumCard
              {album}
              {downloadingTracks}
              onDownloadTrack={handleDownloadTrack}
              onDownloadAlbum={handleDownloadAlbum}
            />
          {/each}
          {#if albumsPartial === 0}
            <div class="border-border rounded-xl border border-dashed p-8 text-center">
              <p class="text-muted-foreground">No partial albums</p>
            </div>
          {/if}
        </Tabs.Content>

        <Tabs.Content value="missing" class="space-y-4">
          {#each missingAlbums as album (album.id)}
            <AlbumCard
              {album}
              {downloadingTracks}
              onDownloadTrack={handleDownloadTrack}
              onDownloadAlbum={handleDownloadAlbum}
            />
          {/each}
          {#if albumsMissing === 0}
            <div class="border-border rounded-xl border border-dashed p-8 text-center">
              <p class="text-muted-foreground">You have all albums!</p>
            </div>
          {/if}
        </Tabs.Content>
      </Tabs.Root>
    </div>
  </ScrollArea>
{/if}

<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { ArrowLeft, Music2, Pause, Play } from '@lucide/svelte';
  import { coverUrlForSong, type AlbumSummary, type ApiSong } from '$lib/api-client';
  import { getSharedSongStreamUrl } from '$lib/api-client';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { Button } from '$lib/components/ui/button';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import { formatDuration } from '$lib/formatters';
  import { playerStore, type PlayerSong } from '$lib/stores/player.svelte';
  import { sharedLibraryStore } from '$lib/stores/shared-library.svelte';

  $effect(() => sharedLibraryStore.ensureLoaded());

  const albums = $derived(sharedLibraryStore.albums);

  // Album detail is a query param, not a route — the same pattern LibraryV2 uses on /library,
  // so back/forward and reloads keep the opened album.
  const openAlbumKey = $derived(page.url.searchParams.get('album'));
  const openAlbum = $derived(
    openAlbumKey ? (albums.find((a) => a.key === openAlbumKey) ?? null) : null
  );

  function openAlbumView(album: AlbumSummary) {
    const url = new URL(page.url);
    url.searchParams.set('album', album.key);
    void goto(url, { keepFocus: true, noScroll: false });
  }

  function closeAlbumView() {
    const url = new URL(page.url);
    url.searchParams.delete('album');
    void goto(url, { keepFocus: true });
  }

  // Don't reuse toPlayerSong: it hardcodes the owner-only stream endpoint. Shared playback
  // streams through the grant-scoped route; covers were already stamped by the store.
  function toSharedPlayerSong(song: ApiSong, fallbackArtist: string): PlayerSong {
    return {
      id: song.id,
      title: (song.title ?? song.fileName).trim() || song.fileName,
      artist: (song.artist ?? fallbackArtist).trim() || fallbackArtist,
      streamUrl: getSharedSongStreamUrl(song.id),
      coverUrl: coverUrlForSong(song),
      album: song.album ?? null
    };
  }

  function playAlbum(album: AlbumSummary, startAt = 0) {
    const queue = album.songs.map((s) => toSharedPlayerSong(s, album.artist));
    if (queue.length === 0) return;
    void playerStore.playSong(queue[startAt] ?? queue[0], queue, startAt);
  }

  const nowPlayingId = $derived(playerStore.currentSong?.id ?? null);
</script>

{#if sharedLibraryStore.isLoading && albums.length === 0}
  <div class="grid grid-cols-2 gap-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5">
    {#each Array(10) as _, i (i)}
      <div class="space-y-2">
        <Skeleton class="aspect-square w-full rounded-lg" />
        <Skeleton class="h-4 w-3/4" />
        <Skeleton class="h-3 w-1/2" />
      </div>
    {/each}
  </div>
{:else if sharedLibraryStore.error}
  <div class="border-border bg-card mx-auto max-w-md rounded-lg border p-8 text-center">
    <p class="text-sm font-medium">Couldn't load your shared music</p>
    <p class="text-muted-foreground mt-1 text-sm">{sharedLibraryStore.error}</p>
    <Button class="mt-4" variant="outline" onclick={() => sharedLibraryStore.load()}>Try again</Button>
  </div>
{:else if albums.length === 0}
  <div class="mx-auto flex max-w-md flex-col items-center gap-3 py-24 text-center">
    <div class="bg-muted text-muted-foreground flex size-12 items-center justify-center rounded-full">
      <Music2 class="size-6" />
    </div>
    <p class="text-sm font-medium">Nothing shared with you yet</p>
    <p class="text-muted-foreground text-sm">
      When the library's owner shares an album or artist with you, it shows up here — ready to
      play.
    </p>
  </div>
{:else if openAlbum}
  <!-- Album detail -->
  <div class="space-y-6">
    <Button variant="ghost" size="sm" class="-ml-2" onclick={closeAlbumView}>
      <ArrowLeft class="size-4" />
      All albums
    </Button>

    <div class="flex flex-col gap-6 sm:flex-row sm:items-end">
      <Cover
        artist={openAlbum.artist}
        title={openAlbum.title}
        coverUrl={openAlbum.coverUrl}
        size={208}
        caption={false}
        class="shrink-0"
      />
      <div class="min-w-0 space-y-2">
        <h1 class="truncate text-2xl font-semibold">{openAlbum.title}</h1>
        <p class="text-muted-foreground truncate text-sm">
          {openAlbum.artist}{openAlbum.year ? ` · ${openAlbum.year}` : ''} · {openAlbum.trackCount}
          {openAlbum.trackCount === 1 ? 'track' : 'tracks'}
        </p>
        <Button size="sm" onclick={() => playAlbum(openAlbum)}>
          <Play class="size-4" />
          Play album
        </Button>
      </div>
    </div>

    <ol class="border-border divide-border bg-card divide-y rounded-lg border">
      {#each openAlbum.songs as song, index (song.id)}
        {@const isCurrent = nowPlayingId === song.id}
        <li>
          <button
            type="button"
            class="hover:bg-accent/50 flex w-full items-center gap-3 px-4 py-2.5 text-left transition-colors"
            onclick={() => playAlbum(openAlbum, index)}
          >
            <span class="text-muted-foreground w-6 shrink-0 text-right text-xs tabular-nums">
              {#if isCurrent && playerStore.isPlaying}
                <Pause class="text-primary ml-auto size-3.5" />
              {:else if isCurrent}
                <Play class="text-primary ml-auto size-3.5" />
              {:else}
                {song.trackNumber ?? index + 1}
              {/if}
            </span>
            <span class="min-w-0 flex-1">
              <span class={`block truncate text-sm ${isCurrent ? 'text-primary font-medium' : ''}`}>
                {song.title ?? song.fileName}
              </span>
              {#if song.artist && song.artist !== openAlbum.artist}
                <span class="text-muted-foreground block truncate text-xs">{song.artist}</span>
              {/if}
            </span>
            <span class="text-muted-foreground shrink-0 text-xs tabular-nums">
              {formatDuration(song.durationSeconds)}
            </span>
          </button>
        </li>
      {/each}
    </ol>
  </div>
{:else}
  <!-- Album grid -->
  <div class="space-y-4">
    <p class="text-muted-foreground text-sm">
      {albums.length}
      {albums.length === 1 ? 'album' : 'albums'} shared with you
    </p>
    <div class="grid grid-cols-2 gap-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5">
      {#each albums as album (album.key)}
        <button
          type="button"
          class="group space-y-2 text-left"
          onclick={() => openAlbumView(album)}
        >
          <div class="relative">
            <Cover
              artist={album.artist}
              title={album.title}
              coverUrl={album.coverUrl}
              size={200}
              caption={false}
              interactive
              class="w-full"
            />
            <span
              class="bg-primary text-primary-foreground absolute right-2 bottom-2 flex size-9 items-center justify-center rounded-full opacity-0 shadow-md transition-opacity group-hover:opacity-100"
              onclick={(e) => {
                e.stopPropagation();
                playAlbum(album);
              }}
              role="button"
              tabindex={-1}
              onkeydown={(e) => e.stopPropagation()}
            >
              <Play class="size-4" />
            </span>
          </div>
          <div class="min-w-0">
            <p class="truncate text-sm font-medium">{album.title}</p>
            <p class="text-muted-foreground truncate text-xs">{album.artist}</p>
          </div>
        </button>
      {/each}
    </div>
  </div>
{/if}

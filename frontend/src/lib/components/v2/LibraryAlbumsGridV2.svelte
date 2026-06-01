<script lang="ts">
  import { Disc3, Play } from '@lucide/svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { toPlayerSong, type AlbumSummary } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';

  type Props = {
    albums: AlbumSummary[];
    /** href builder for an album card (keeps deep-linkable `?album=` URLs). */
    hrefFor: (album: AlbumSummary) => string;
    /** Whether the underlying songs are still loading (controls the empty/skeleton copy). */
    isLoading?: boolean;
  };
  const { albums, hrefFor, isLoading = false }: Props = $props();

  function playFirst(album: AlbumSummary, e: MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    if (album.songs.length === 0) return;
    const queue = album.songs.map((s) => toPlayerSong(s, album.artist));
    void playerStore.playSong(queue[0], queue, 0);
  }
</script>

{#if albums.length === 0}
  <div class="text-muted-foreground flex flex-col items-center justify-center gap-3 py-16 text-center">
    <Disc3 class="size-10 opacity-40" />
    <p class="text-sm">{isLoading ? 'Loading albums…' : 'No albums match.'}</p>
  </div>
{:else}
  <div
    class="grid grid-cols-2 gap-x-3 gap-y-6 sm:grid-cols-3 sm:gap-x-5 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
  >
    {#each albums as album (album.key)}
      <a
        href={hrefFor(album)}
        class="group focus-visible:ring-ring outline-hidden flex flex-col gap-2 rounded-lg p-1 transition-transform hover:-translate-y-0.5 focus-visible:ring-2 focus-visible:ring-offset-2"
        aria-label={`Open album ${album.title} by ${album.artist}`}
      >
        <div class="relative">
          <Cover
            artist={album.artist}
            title={album.title}
            coverUrl={album.coverUrl}
            size={176}
            interactive
            class="!h-auto !w-full aspect-square"
          />
          <button
            type="button"
            aria-label={`Play ${album.title}`}
            onclick={(e) => playFirst(album, e)}
            class="bg-primary text-primary-foreground absolute right-2 bottom-2 grid size-9 translate-y-1 place-items-center rounded-full opacity-0 shadow-md transition-all duration-150 group-hover:translate-y-0 group-hover:opacity-100"
          >
            <Play class="size-4" />
          </button>
        </div>
        <div class="min-w-0 px-0.5">
          <p class="truncate text-[12.5px] font-medium">{album.title}</p>
          <p class="text-muted-foreground truncate text-[11.5px]">
            {album.artist}{album.year ? ` · ${album.year}` : ''}
          </p>
        </div>
      </a>
    {/each}
  </div>
{/if}

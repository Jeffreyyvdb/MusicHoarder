<script lang="ts">
  import { Disc3, Music } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import type { ApiSong } from '$lib/api-client';
  import { cn } from '$lib/utils';

  type Props = {
    songs: ApiSong[];
    isLoading: boolean;
    searchQuery: string;
  };
  const { songs, isLoading, searchQuery }: Props = $props();

  type AlbumSummary = {
    key: string;
    title: string;
    artist: string;
    year: number | null;
    trackCount: number;
    initials: string;
    coverUrl: string | null;
  };

  const UNKNOWN_ALBUM = 'Unknown Album';
  const UNKNOWN_ARTIST = 'Unknown Artist';

  function computeInitials(title: string): string {
    const letters = title
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((word) => word[0]?.toUpperCase() ?? '')
      .join('');
    return letters || title.slice(0, 2).toUpperCase();
  }

  function groupByAlbum(list: ApiSong[]): AlbumSummary[] {
    const map = new Map<string, AlbumSummary>();
    for (const song of list) {
      const title = (song.album ?? UNKNOWN_ALBUM).trim() || UNKNOWN_ALBUM;
      const artist =
        (song.albumArtist ?? song.artist ?? UNKNOWN_ARTIST).trim() || UNKNOWN_ARTIST;
      const key = `${artist.toLowerCase()}::${title.toLowerCase()}`;
      const existing = map.get(key);
      if (existing) {
        existing.trackCount += 1;
        if (song.year && (!existing.year || song.year < existing.year)) existing.year = song.year;
      } else {
        map.set(key, {
          key,
          title,
          artist,
          year: song.year ?? null,
          trackCount: 1,
          initials: computeInitials(title),
          coverUrl: song.albumArt ?? null
        });
      }
    }
    return Array.from(map.values()).sort((a, b) => {
      const artistCmp = a.artist.localeCompare(b.artist);
      if (artistCmp !== 0) return artistCmp;
      return a.title.localeCompare(b.title);
    });
  }

  const albums = $derived(groupByAlbum(songs));
  const filtered = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return albums;
    return albums.filter(
      (a) => a.title.toLowerCase().includes(q) || a.artist.toLowerCase().includes(q)
    );
  });

  function hideOnError(e: Event) {
    (e.currentTarget as HTMLImageElement).style.display = 'none';
  }
</script>

{#if isLoading && albums.length === 0}
  <div class="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
    Loading albums...
  </div>
{:else if filtered.length === 0}
  <div
    class="text-muted-foreground flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center"
  >
    <Disc3 class="size-10 opacity-40" />
    <p class="text-sm">
      {searchQuery ? 'No albums match your search.' : 'No albums yet.'}
    </p>
  </div>
{:else}
  <ScrollArea class="min-h-0 flex-1">
    <div class="p-4 md:p-6">
      <div class="mb-4 flex items-end justify-between gap-4">
        <div>
          <h2 class="text-xl font-semibold">All albums</h2>
          <p class="text-muted-foreground text-sm">
            {filtered.length} album{filtered.length === 1 ? '' : 's'}
            {searchQuery ? ` matching "${searchQuery}"` : ''}
          </p>
        </div>
      </div>
      <div
        class="grid grid-cols-2 gap-4 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
      >
        {#each filtered as album (album.key)}
          <a
            href={`/app?album=${encodeURIComponent(album.key)}`}
            class="group focus-visible:ring-ring outline-hidden flex flex-col gap-2 rounded-lg focus-visible:ring-2 focus-visible:ring-offset-2"
            aria-label={`Open album ${album.title} by ${album.artist}`}
          >
            <div
              class="border-border from-secondary to-muted group-hover:border-primary/40 relative aspect-square overflow-hidden rounded-lg border bg-gradient-to-br shadow-sm transition-all group-hover:shadow-md"
            >
              {#if album.coverUrl}
                <img
                  src={album.coverUrl}
                  alt=""
                  loading="lazy"
                  class="size-full object-cover transition-transform group-hover:scale-[1.02]"
                  onerror={hideOnError}
                />
              {/if}
              <div
                class={cn(
                  'pointer-events-none absolute inset-0 flex items-center justify-center',
                  album.coverUrl && 'opacity-0'
                )}
              >
                <span class="text-muted-foreground/60 text-3xl font-semibold tracking-wide">
                  {album.initials}
                </span>
              </div>
              <div
                class="bg-background/80 text-muted-foreground absolute bottom-2 left-2 flex items-center gap-1 rounded-full px-2 py-0.5 text-[10px] backdrop-blur-sm"
              >
                <Music class="size-3" />
                {album.trackCount}
              </div>
            </div>
            <div class="min-w-0">
              <p class="truncate text-sm font-medium">{album.title}</p>
              <p class="text-muted-foreground truncate text-xs">
                {album.artist}{album.year ? ` · ${album.year}` : ''}
              </p>
            </div>
          </a>
        {/each}
      </div>
    </div>
  </ScrollArea>
{/if}

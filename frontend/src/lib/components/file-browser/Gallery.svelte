<script lang="ts">
  import { onMount, onDestroy } from 'svelte';
  import { Disc3, Music, Play } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import ProcessingStrip from '$lib/components/file-browser/ProcessingStrip.svelte';
  import { buildAlbumsFromSongs, getSongStreamUrl, type ApiSong } from '$lib/api-client';
  import { SECTION_LABELS, type SectionId } from '$lib/album-sections';
  import { formatFileSize, formatDuration } from '$lib/formatters';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  type Props = {
    songs: ApiSong[];
    section: SectionId;
    searchQuery: string;
    isLoading: boolean;
  };
  const { songs, section, searchQuery, isLoading }: Props = $props();

  type Layout = 'grid' | 'list' | 'col';

  function getStoredLayout(): Layout {
    if (typeof window === 'undefined') return 'grid';
    const v = localStorage.getItem('musichoarder-library-view');
    if (v === 'list' || v === 'col' || v === 'grid') return v;
    return 'grid';
  }

  let layout = $state<Layout>('grid');

  onMount(() => {
    layout = getStoredLayout();
    const handler = (e: Event) => {
      const next = (e as CustomEvent).detail as Layout | undefined;
      if (next === 'list' || next === 'grid') layout = next;
    };
    window.addEventListener('mh:layout-change', handler);
    return () => window.removeEventListener('mh:layout-change', handler);
  });
  onDestroy(() => {});

  const albums = $derived(buildAlbumsFromSongs(songs));
  const filtered = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return albums;
    return albums.filter(
      (a) =>
        a.title.toLowerCase().includes(q) ||
        a.artist.toLowerCase().includes(q) ||
        (a.genre?.toLowerCase().includes(q) ?? false)
    );
  });

  const meta = $derived(SECTION_LABELS[section]);
  const showProcessing = $derived(section === 'lib' || section === 'queue');
  const isQueue = $derived(section === 'queue');

  function playFirst(albumKey: string, e: MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    const album = filtered.find((a) => a.key === albumKey);
    if (!album || album.songs.length === 0) return;
    const target = album.songs[0];
    void playerStore.playSong({
      id: target.id,
      title: (target.title ?? target.fileName).trim() || target.fileName,
      artist: (target.artist ?? album.artist).trim() || album.artist,
      streamUrl: getSongStreamUrl(target.id)
    });
  }

  function albumHref(key: string) {
    return `/app?album=${encodeURIComponent(key)}`;
  }
</script>

{#if isLoading && albums.length === 0}
  <div class="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
    Loading albums…
  </div>
{:else}
  <ScrollArea class="min-h-0 flex-1">
    <div class="p-4 pb-20 md:p-6">
      <div class="mb-4 flex items-end justify-between gap-4">
        <div class="min-w-0">
          <h2 class="truncate text-2xl font-semibold tracking-[-0.02em]">{meta.title}</h2>
          <p class="text-muted-foreground mt-1 text-xs">
            {meta.subtitle(filtered.length)}
            {#if searchQuery.trim()}
              <span class="ml-1">· matching "{searchQuery.trim()}"</span>
            {/if}
          </p>
        </div>
        <div class="text-muted-foreground hidden text-xs sm:block">
          Sort by <span class="text-foreground/80 ml-1 cursor-pointer">Recently added ▾</span>
        </div>
      </div>

      {#if showProcessing}
        <ProcessingStrip />
      {/if}

      {#if isQueue}
        <div class="text-muted-foreground px-2 py-12 text-center">
          <div class="text-foreground text-base font-medium">Queue tails live above</div>
          <div class="mt-1 text-xs">
            Items appear in the library once they finish writing to destination.
          </div>
        </div>
      {:else if filtered.length === 0}
        <div
          class="text-muted-foreground flex flex-col items-center justify-center gap-3 py-16 text-center"
        >
          <Disc3 class="size-10 opacity-40" />
          <p class="text-sm">
            {searchQuery.trim()
              ? 'No albums match your search.'
              : 'Nothing in this section yet.'}
          </p>
        </div>
      {:else}
        <div class="text-muted-foreground mt-1 mb-3 text-sm font-medium">
          {layout === 'grid' ? 'All albums' : 'Details'}
        </div>

        {#if layout === 'grid'}
          <div
            class="grid grid-cols-2 gap-x-5 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
          >
            {#each filtered as album (album.key)}
              <a
                href={albumHref(album.key)}
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
                    class="!w-full !h-auto aspect-square"
                  />
                  <button
                    type="button"
                    aria-label={`Play ${album.title}`}
                    onclick={(e) => playFirst(album.key, e)}
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
        {:else}
          <!-- list view -->
          <div class="border-border bg-card overflow-hidden rounded-lg border">
            <div
              class={cn(
                'bg-surface-sunken border-border text-muted-foreground grid items-center gap-4 border-b px-3.5 py-2 text-[10px] font-semibold tracking-wider uppercase',
                'grid-cols-[44px_2.2fr_1.4fr_1fr_0.8fr_0.8fr_0.9fr]'
              )}
            >
              <span></span>
              <span>Album</span>
              <span>Artist</span>
              <span class="hidden md:inline">Genre</span>
              <span class="hidden md:inline">Year</span>
              <span>Tracks</span>
              <span class="text-right">Size</span>
            </div>
            {#each filtered as album (album.key)}
              <a
                href={albumHref(album.key)}
                class={cn(
                  'border-border hover:bg-accent/50 grid items-center gap-4 border-b px-3.5 py-2 text-xs last:border-b-0',
                  'grid-cols-[44px_2.2fr_1.4fr_1fr_0.8fr_0.8fr_0.9fr]'
                )}
              >
                <Cover
                  artist={album.artist}
                  title={album.title}
                  coverUrl={album.coverUrl}
                  size={32}
                  corner={3}
                  caption={false}
                />
                <span class="truncate text-[12.5px] font-medium">{album.title}</span>
                <span class="text-muted-foreground truncate">{album.artist}</span>
                <span class="text-muted-foreground hidden truncate md:inline">{album.genre ?? '—'}</span>
                <span class="text-muted-foreground hidden font-mono md:inline">{album.year ?? '—'}</span>
                <span class="text-muted-foreground font-mono">
                  <Music class="-mt-0.5 mr-1 inline size-3" />{album.trackCount}
                </span>
                <span class="text-muted-foreground text-right font-mono">
                  {formatFileSize(album.byteSize)}
                </span>
              </a>
            {/each}
          </div>
        {/if}

        {#if filtered.length > 0 && layout === 'grid'}
          <div class="text-muted-foreground mt-6 text-center text-[11px]">
            {filtered.length.toLocaleString()} album{filtered.length === 1 ? '' : 's'}
            {#if filtered.reduce((sum, a) => sum + a.durationSeconds, 0) > 0}
              · total {formatDuration(filtered.reduce((sum, a) => sum + a.durationSeconds, 0))}
            {/if}
          </div>
        {/if}
      {/if}
    </div>
  </ScrollArea>
{/if}

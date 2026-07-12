<script lang="ts">
  import { ChevronRight, Clock, Compass, Disc3, Heart, Play, Sparkles, Users } from '@lucide/svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import {
    buildAlbumsFromSongs,
    buildArtistGroups,
    coverUrlForSong,
    sortAlbumsByRecency,
    toPlayerSong,
    type AlbumSummary,
    type ApiSong,
    type GroupSummary
  } from '$lib/api-client';
  import { isBuiltSong } from '$lib/album-sections';
  import { playerStore } from '$lib/stores/player.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';

  // ── data (shared songs store, same live-refresh contract as LibraryV2) ──────
  const songs = $derived(songsStore.songs);
  const isLoading = $derived(songsStore.isLoading);

  $effect(() => {
    void songsStore.loadSongs();
    songsStore.startLive();
    return () => songsStore.stopLive();
  });

  const builtSongs = $derived(songs.filter(isBuiltSong));
  const allAlbums = $derived(buildAlbumsFromSongs(builtSongs));
  const artistGroups = $derived(buildArtistGroups(builtSongs, { primaryOnly: true }));

  // ── per-visit random order that stays stable across live refetches ──────────
  // A real shuffle inside $derived would reorder the shelves every time the
  // songs store silently refreshes; hashing item keys against a per-mount seed
  // keeps the "random" sections random per visit but stable within it.
  const seed = Math.floor(Math.random() * 0xffff_ffff).toString(36);
  function seededOrder<T>(items: T[], keyOf: (item: T) => string): T[] {
    const hash = (s: string): number => {
      let h = 5381;
      for (let i = 0; i < s.length; i++) h = ((h << 5) + h + s.charCodeAt(i)) >>> 0;
      return h;
    };
    return [...items].sort((a, b) => hash(keyOf(a) + seed) - hash(keyOf(b) + seed));
  }

  const SHELF_SIZE = 12;

  // ── sections ────────────────────────────────────────────────────────────────
  const favoriteTracks = $derived(
    builtSongs
      .filter((s) => Boolean(s.likedAtUtc))
      .sort((a, b) => Date.parse(b.likedAtUtc ?? '') - Date.parse(a.likedAtUtc ?? ''))
      .slice(0, 10)
  );

  const recentAlbums = $derived(sortAlbumsByRecency(allAlbums).slice(0, SHELF_SIZE));

  function albumLastPlayed(a: AlbumSummary): number {
    let max = 0;
    for (const s of a.songs) {
      const t = s.lastPlayedAtUtc ? Date.parse(s.lastPlayedAtUtc) : 0;
      if (t > max) max = t;
    }
    return max;
  }
  const lastPlayedAlbums = $derived(
    allAlbums
      .filter((a) => albumLastPlayed(a) > 0)
      .sort((a, b) => albumLastPlayed(b) - albumLastPlayed(a))
      .slice(0, SHELF_SIZE)
  );

  // Discover: albums you've never pressed play on — a random rummage through the
  // unlistened corners of the hoard.
  const discoverAlbums = $derived(
    seededOrder(
      allAlbums.filter((a) => a.songs.every((s) => !s.playCount)),
      (a) => a.key
    ).slice(0, SHELF_SIZE)
  );

  const randomArtists = $derived(seededOrder(artistGroups, (g) => g.key).slice(0, SHELF_SIZE));
  const randomAlbums = $derived(seededOrder(allAlbums, (a) => a.key).slice(0, SHELF_SIZE));

  // ── playback ────────────────────────────────────────────────────────────────
  function fallbackArtist(s: ApiSong): string {
    return (s.albumArtist ?? s.artist ?? '').trim() || 'Unknown Artist';
  }
  function playFavorite(target: ApiSong) {
    const queue = favoriteTracks.map((s) => toPlayerSong(s, fallbackArtist(s)));
    const index = favoriteTracks.findIndex((s) => s.id === target.id);
    void playerStore.playSong(toPlayerSong(target, fallbackArtist(target)), queue, index);
  }
  function playAlbum(album: AlbumSummary, e: MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    if (album.songs.length === 0) return;
    const queue = album.songs.map((s) => toPlayerSong(s, album.artist));
    void playerStore.playSong(queue[0], queue, 0);
  }

  function albumHref(a: AlbumSummary): string {
    return `/library?album=${encodeURIComponent(a.key)}`;
  }
  function artistHref(g: GroupSummary): string {
    return `/library?artist=${encodeURIComponent(g.key)}`;
  }

  const greeting = $derived.by(() => {
    const h = new Date().getHours();
    if (h < 6) return 'Night owl session';
    if (h < 12) return 'Good morning';
    if (h < 18) return 'Good afternoon';
    return 'Good evening';
  });
</script>

{#snippet sectionHeader(title: string, href: string, Icon: typeof Heart)}
  <a
    href={href}
    class="group/head flex items-center gap-2 px-1"
    aria-label={`Open ${title}`}
  >
    <Icon class="text-muted-foreground size-4" />
    <h2 class="text-[15px] font-semibold tracking-[-0.01em]">{title}</h2>
    <ChevronRight
      class="text-muted-foreground size-4 opacity-0 transition-all group-hover/head:translate-x-0.5 group-hover/head:opacity-100"
    />
  </a>
{/snippet}

{#snippet albumShelf(albums: AlbumSummary[])}
  <div class="-mx-1 flex snap-x gap-4 overflow-x-auto px-1 pt-3 pb-2">
    {#each albums as album (album.key)}
      <a
        href={albumHref(album)}
        class="group focus-visible:ring-ring outline-hidden flex w-[136px] shrink-0 snap-start flex-col gap-2 sm:w-[160px]"
        aria-label={`Open album ${album.title} by ${album.artist}`}
      >
        <div class="relative">
          <Cover
            artist={album.artist}
            title={album.title}
            coverUrl={album.coverUrl}
            size={160}
            corner={8}
            caption={false}
            interactive
            class="!h-auto !w-full aspect-square shadow-[0_2px_10px_rgba(0,0,0,0.12)] dark:shadow-[0_4px_14px_rgba(0,0,0,0.5)]"
          />
          <button
            type="button"
            aria-label={`Play ${album.title}`}
            onclick={(e) => playAlbum(album, e)}
            class="bg-primary text-primary-foreground absolute right-2 bottom-2 grid size-9 translate-y-1 place-items-center rounded-full opacity-0 shadow-md transition-all duration-150 group-hover:translate-y-0 group-hover:opacity-100 focus-visible:translate-y-0 focus-visible:opacity-100"
          >
            <Play class="size-4" fill="currentColor" />
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
{/snippet}

<ScrollArea class="min-h-0 flex-1">
  <div class="mx-auto flex max-w-[1400px] flex-col gap-8 px-4 py-6 pb-[var(--mh-content-pad)] sm:px-7">
    <!-- Greeting band -->
    <div>
      <h1 class="text-2xl font-bold tracking-tight sm:text-3xl">{greeting}</h1>
      <p class="text-muted-foreground mt-1 text-sm">
        {builtSongs.length.toLocaleString()} tracks · {allAlbums.length.toLocaleString()} albums ·
        {artistGroups.length.toLocaleString()} artists
      </p>
    </div>

    {#if isLoading && songs.length === 0}
      <div class="text-muted-foreground flex items-center justify-center py-24 text-sm">
        Loading your library…
      </div>
    {:else if builtSongs.length === 0}
      <div class="text-muted-foreground flex flex-col items-center justify-center gap-3 py-24 text-center">
        <Disc3 class="size-10 opacity-40" />
        <p class="text-sm">Nothing in the library yet — run the pipeline to build it.</p>
      </div>
    {:else}
      <!-- Favourite tracks -->
      {#if favoriteTracks.length > 0}
        <section>
          {@render sectionHeader('Favourite tracks', '/liked', Heart)}
          <div class="grid grid-cols-1 gap-2 pt-3 sm:grid-cols-2 xl:grid-cols-5">
            {#each favoriteTracks as song (song.id)}
              {@const isLoaded = playerStore.currentSong?.id === song.id}
              {@const isCurrentlyPlaying = isLoaded && playerStore.isPlaying}
              <button
                type="button"
                onclick={() => playFavorite(song)}
                class="group border-border/60 bg-card/50 hover:bg-accent/60 flex items-center gap-3 rounded-lg border p-2 text-left transition-colors active:scale-[0.99]"
                aria-label={`Play ${(song.title ?? song.fileName).trim() || song.fileName}`}
              >
                <div class="relative shrink-0">
                  <Cover
                    artist={fallbackArtist(song)}
                    title={song.album ?? song.title ?? song.fileName}
                    coverUrl={coverUrlForSong(song)}
                    size={44}
                    corner={6}
                    caption={false}
                  />
                  <span
                    class="bg-background/70 absolute inset-0 grid place-items-center rounded-[6px] opacity-0 backdrop-blur-[1px] transition-opacity group-hover:opacity-100"
                  >
                    <Play class="text-foreground size-4" fill="currentColor" />
                  </span>
                </div>
                <div class="min-w-0 flex-1">
                  <p
                    class="truncate text-[12.5px] font-medium {isCurrentlyPlaying ? 'text-primary' : ''}"
                  >
                    {(song.title ?? song.fileName).trim() || song.fileName}
                  </p>
                  <p class="text-muted-foreground truncate text-[11.5px]">{fallbackArtist(song)}</p>
                </div>
                <Heart class="text-primary size-3.5 shrink-0" fill="currentColor" />
              </button>
            {/each}
          </div>
        </section>
      {/if}

      <!-- Recently added albums -->
      {#if recentAlbums.length > 0}
        <section>
          {@render sectionHeader('Recently added', '/library', Sparkles)}
          {@render albumShelf(recentAlbums)}
        </section>
      {/if}

      <!-- Last played albums -->
      {#if lastPlayedAlbums.length > 0}
        <section>
          {@render sectionHeader('Last played', '/library', Clock)}
          {@render albumShelf(lastPlayedAlbums)}
        </section>
      {/if}

      <!-- Discover: albums never played -->
      {#if discoverAlbums.length > 0}
        <section>
          {@render sectionHeader('Discover — never played', '/library', Compass)}
          {@render albumShelf(discoverAlbums)}
        </section>
      {/if}

      <!-- Random artists -->
      {#if randomArtists.length > 0}
        <section>
          {@render sectionHeader('Artists to revisit', '/artists', Users)}
          <div class="-mx-1 flex snap-x gap-4 overflow-x-auto px-1 pt-3 pb-2">
            {#each randomArtists as group (group.key)}
              <a
                href={artistHref(group)}
                class="group focus-visible:ring-ring outline-hidden flex w-[120px] shrink-0 snap-start flex-col items-center gap-2 sm:w-[136px]"
                aria-label={`Browse ${group.label}`}
              >
                <Cover
                  artist={group.coverArtist}
                  title={group.coverTitle}
                  coverUrl={group.coverUrl}
                  size={136}
                  caption={false}
                  interactive
                  class="!h-auto !w-full aspect-square !rounded-full shadow-[0_2px_10px_rgba(0,0,0,0.15)]"
                />
                <div class="min-w-0 text-center">
                  <p class="truncate text-[12.5px] font-medium">{group.label}</p>
                  <p class="text-muted-foreground truncate text-[11px]">
                    {group.albumCount} album{group.albumCount === 1 ? '' : 's'}
                  </p>
                </div>
              </a>
            {/each}
          </div>
        </section>
      {/if}

      <!-- Random albums -->
      {#if randomAlbums.length > 0}
        <section>
          {@render sectionHeader('From the shelves', '/library', Disc3)}
          {@render albumShelf(randomAlbums)}
        </section>
      {/if}
    {/if}
  </div>
</ScrollArea>

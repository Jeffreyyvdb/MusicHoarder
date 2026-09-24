<script lang="ts">
  import { ChevronRight, Disc3, Heart, ListMusic, Mic2, Music2 } from '@lucide/svelte';
  import { page } from '$app/state';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import LibraryAlbumsGridV2 from '$lib/components/v2/LibraryAlbumsGridV2.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import TrackRowMenu, { activateTrack } from '$lib/components/v2/TrackRowMenu.svelte';
  import TrackRowText from '$lib/components/v2/TrackRowText.svelte';
  import { longpress, type LongPressPoint } from '$lib/actions/long-press';
  import {
    buildArtistGroups,
    coverUrlForSong,
    isSpotifyLiked,
    sortAlbumsByRecency,
    toPlayerSong,
    type AlbumSummary,
    type ApiSong,
    type GroupSummary
  } from '$lib/api-client';
  import { isBuiltSong } from '$lib/album-sections';
  import { isAdmin } from '$lib/auth/capabilities';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { navGroupsFor } from '$lib/nav';
  import { playerStore } from '$lib/stores/player.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { isTrackListSong, titleOf } from '$lib/track-list-view.svelte';
  import { cn } from '$lib/utils';

  // The Listen tab's root on a phone, and a member's Overview tab — Apple Music's Library page:
  // the greeting as the large title, a short list into Albums / Artists / Tracks / Favourites,
  // then the shelves.

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);
  const user = $derived(page.data.user);
  const admin = $derived(isAdmin(user));

  // ── data (shared songs store, same live-refresh contract as LibraryV2) ──────
  const songs = $derived(songsStore.songs);
  const isLoading = $derived(songsStore.isLoading);

  $effect(() => {
    void songsStore.loadSongs();
    songsStore.startLive();
    return () => songsStore.stopLive();
  });

  const builtSongs = $derived(songs.filter(isBuiltSong));
  // The library grid's own cards, so a folder-split album isn't two near-identical shelf tiles.
  // Their `songs` are the store's rows, which is what lets the shelves below read per-track play
  // counts and likes and still see an optimistic heart tap.
  const allAlbums = $derived(songsStore.albums);
  const artistGroups = $derived(buildArtistGroups(builtSongs, { primaryOnly: true }));
  // The Tracks list's own base, so the Library rows count exactly what they open.
  const trackListSongs = $derived(songs.filter(isTrackListSong));

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
  /** Rows per column in the Favourite tracks list. */
  const FAVOURITE_ROWS = 4;

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

  // New to you: albums that album completion filled in and you haven't played yet. This is the
  // payoff of the feature — you liked one track, here's the rest of the record, waiting.
  const newToYouAlbums = $derived(
    seededOrder(
      allAlbums.filter(
        (a) =>
          a.songs.some((s) => s.acquisitionIntent === 'AlbumFill' && !s.likedAtUtc) &&
          a.songs.every((s) => !s.playCount)
      ),
      (a) => a.key
    ).slice(0, SHELF_SIZE)
  );

  const randomArtists = $derived(seededOrder(artistGroups, (g) => g.key).slice(0, SHELF_SIZE));
  const randomAlbums = $derived(seededOrder(allAlbums, (a) => a.key).slice(0, SHELF_SIZE));

  // ── Library rows ────────────────────────────────────────────────────────────
  // Only for an audience with several tabs (admin, demo): a member's tab bar already IS Overview /
  // Albums / Artists / Tracks. Phones only — the desktop sidebar lists the same pages.
  const multiGroup = $derived(navGroupsFor(user).length > 1);
  const libraryRows = $derived([
    { href: '/library', label: 'Albums', icon: Disc3, count: allAlbums.length },
    { href: '/artists', label: 'Artists', icon: Mic2, count: artistGroups.length },
    { href: '/tracks', label: 'Tracks', icon: ListMusic, count: trackListSongs.length },
    {
      href: '/tracks?f=mh-liked',
      label: 'Favourites',
      icon: Heart,
      count: trackListSongs.filter((s) => Boolean(s.likedAtUtc)).length
    },
    ...(admin
      ? [
          {
            href: '/tracks?f=spotify-liked',
            label: 'Spotify liked',
            icon: Music2,
            count: trackListSongs.filter(isSpotifyLiked).length
          }
        ]
      : [])
  ]);

  // ── playback ────────────────────────────────────────────────────────────────
  function fallbackArtist(s: ApiSong): string {
    return (s.albumArtist ?? s.artist ?? '').trim() || 'Unknown Artist';
  }
  /** The favourites from `target`; never pauses (the phone's tap rule, a menu's Play). */
  function playFavorite(target: ApiSong) {
    void playerStore.startQueue(
      favoriteTracks.map((s) => toPlayerSong(s, fallbackArtist(s))),
      favoriteTracks.findIndex((s) => s.id === target.id)
    );
  }
  /**
   * A phone follows the tap rule (play the favourites from here, or open the loaded song); a
   * desktop click plays, as it always has (a second click on the playing song pauses it).
   */
  function tapFavorite(song: ApiSong) {
    if (compact) {
      activateTrack(song.id, () => playFavorite(song));
      return;
    }
    const queue = favoriteTracks.map((s) => toPlayerSong(s, fallbackArtist(s)));
    const index = favoriteTracks.findIndex((s) => s.id === song.id);
    void playerStore.playSong(toPlayerSong(song, fallbackArtist(song)), queue, index);
  }

  type RowMenu = { openAt: (point: LongPressPoint) => void };
  const menus: Record<number, RowMenu | undefined> = {};

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

<!-- A shelf header: the title, and a tint "See all" text button (44pt tall) where the shelf has
     somewhere to go. Visible at rest — there is no hover on a phone. -->
{#snippet sectionHeader(title: string, href: string | null, id: string)}
  <div class="flex items-center justify-between gap-3 px-4 md:px-7">
    <h2 {id} class="text-title-2 md:text-lg md:font-semibold">{title}</h2>
    {#if href}
      <a
        {href}
        aria-label="See all {title.toLowerCase()}"
        class="text-primary text-body relative -mr-2 flex h-11 items-center gap-0.5 px-2 hover:underline md:h-8 md:text-sm"
      >
        See all
        <ChevronRight class="size-4 md:hidden" aria-hidden="true" />
      </a>
    {/if}
  </div>
{/snippet}

<div class="flex min-h-0 flex-1 flex-col">
  <!-- The nav bar is the viewport's first child so the greeting scrolls away and the bar collapses.
       The viewport already reserves the tab-bar / mini-player clearance (app.css), so there is no
       bottom padding here. -->
  <ScrollArea class="min-h-0 flex-1">
    <!-- The tracks figure is the Tracks list's own count, so it agrees with the Tracks row below.
         metaFrom lg: at md the meta starved the greeting down to "Good ev…". -->
    <PageToolbarV2
      title={greeting}
      meta="{trackListSongs.length.toLocaleString()} tracks · {allAlbums.length.toLocaleString()} albums · {artistGroups.length.toLocaleString()} artists"
      metaFrom="lg"
    />

    <div class="mx-auto flex w-full max-w-[1400px] flex-col gap-7 pt-2 pb-6 md:gap-8 md:pt-4">
      {#if isLoading && songs.length === 0}
        <div
          class="text-muted-foreground text-subheadline flex items-center justify-center py-24 md:text-sm"
        >
          Loading your library…
        </div>
      {:else if builtSongs.length === 0}
        <div
          class="text-muted-foreground flex flex-col items-center justify-center gap-3 px-8 py-24 text-center"
        >
          <Disc3 class="text-muted-foreground-dim size-10" aria-hidden="true" />
          <p class="text-subheadline md:text-sm">
            {admin
              ? 'Nothing in the library yet — run the pipeline to build it.'
              : 'Nothing has been shared with you yet.'}
          </p>
        </div>
      {:else}
        {#if compact && multiGroup}
          <!-- Library: a plain list with inset separators (Apple Music's Library rows). -->
          <nav aria-label="Library">
            <ul>
              {#each libraryRows as row, i (row.href)}
                <li>
                  <a
                    href={row.href}
                    class={cn(
                      'focus-visible:ring-ring active:bg-accent relative flex min-h-11 items-center gap-4 px-4 transition-colors duration-100 outline-none focus-visible:ring-2 focus-visible:ring-inset',
                      i < libraryRows.length - 1 &&
                        "after:bg-separator after:absolute after:right-0 after:bottom-0 after:left-[54px] after:h-(--hairline) after:content-['']"
                    )}
                  >
                    <row.icon class="text-primary size-[22px] shrink-0" aria-hidden="true" />
                    <span class="text-body min-w-0 flex-1 truncate">{row.label}</span>
                    <span class="text-body text-muted-foreground tabular-nums"
                      >{row.count.toLocaleString()}</span
                    >
                    <ChevronRight
                      class="text-muted-foreground-dim -mr-1 size-4 shrink-0"
                      strokeWidth={2.5}
                      aria-hidden="true"
                    />
                  </a>
                </li>
              {/each}
            </ul>
          </nav>
        {/if}

        <!-- Favourite tracks: ONE list, laid out as Apple Music's "Top songs" — columns of four rows
             that page sideways on a phone (all ten stay one swipe away without pushing the shelves
             off the screen) and sit side by side on a desktop. Sized from the data: four or fewer
             favourites are one full-width column of exactly that many rows, with nothing to page
             to and no blank rows reserved under it. -->
        {#if favoriteTracks.length > 0}
          {@const paged = favoriteTracks.length > FAVOURITE_ROWS}
          <section aria-labelledby="overview-favourites">
            {@render sectionHeader('Favourite tracks', '/tracks?f=mh-liked', 'overview-favourites')}
            <div
              data-scroll-x={paged ? '' : undefined}
              style:grid-template-rows="repeat({Math.min(FAVOURITE_ROWS, favoriteTracks.length)},
              auto)"
              class={cn(
                'grid grid-flow-col gap-x-4 px-4 pt-1 md:px-7',
                paged
                  ? 'no-scrollbar snap-x snap-mandatory scroll-px-4 auto-cols-[calc(100%-3rem)] overflow-x-auto md:scroll-px-7 md:auto-cols-[minmax(18rem,1fr)]'
                  : 'auto-cols-[100%] md:auto-cols-[minmax(18rem,36rem)]'
              )}
            >
              {#each favoriteTracks as song, i (song.id)}
                {@const isLoaded = playerStore.currentSong?.id === song.id}
                <div
                  use:longpress={{ onlongpress: (p) => menus[song.id]?.openAt(p) }}
                  oncontextmenu={(e) => {
                    e.preventDefault();
                    menus[song.id]?.openAt({ x: e.clientX, y: e.clientY });
                  }}
                  role="group"
                  aria-label={titleOf(song)}
                  class={cn(
                    'group has-[[data-row-main]:active]:bg-accent md:hover:bg-accent relative flex min-h-14 snap-start items-center transition-colors duration-100 md:rounded-lg',
                    i % FAVOURITE_ROWS !== FAVOURITE_ROWS - 1 &&
                      i < favoriteTracks.length - 1 &&
                      "after:bg-separator after:absolute after:right-0 after:bottom-0 after:left-[60px] after:h-(--hairline) after:content-['']"
                  )}
                >
                  <button
                    type="button"
                    data-row-main=""
                    onclick={() => tapFavorite(song)}
                    aria-current={isLoaded ? 'true' : undefined}
                    class="focus-visible:ring-ring flex min-h-14 min-w-0 flex-1 items-center gap-3 text-left outline-none focus-visible:ring-2 md:pl-1"
                  >
                    <Cover
                      artist={fallbackArtist(song)}
                      title={song.album ?? song.title ?? song.fileName}
                      coverUrl={coverUrlForSong(song)}
                      size={48}
                      corner={6}
                      caption={false}
                      dprCap={3}
                      class="shrink-0"
                    />
                    <!-- The Tracks row's text column, minus the heart: every row here is a
                         favourite, so a heart on each said nothing. -->
                    <TrackRowText
                      {song}
                      loaded={isLoaded}
                      heart={false}
                      opensPlayer={isLoaded && isMobile.current}
                      secondary={fallbackArtist(song)}
                    />
                  </button>
                  <TrackRowMenu
                    bind:this={() => menus[song.id], (m) => (menus[song.id] = m)}
                    {song}
                    onplay={() => playFavorite(song)}
                    class="md:opacity-0 md:group-hover:opacity-100 md:focus-visible:opacity-100 md:aria-expanded:opacity-100 pointer-coarse:opacity-100"
                  />
                </div>
              {/each}
            </div>
          </section>
        {/if}

        {#if recentAlbums.length > 0}
          <section aria-labelledby="overview-recent">
            {@render sectionHeader('Recently added', '/library', 'overview-recent')}
            <LibraryAlbumsGridV2 albums={recentAlbums} hrefFor={albumHref} layout="shelf" menu />
          </section>
        {/if}

        {#if lastPlayedAlbums.length > 0}
          <section aria-labelledby="overview-played">
            {@render sectionHeader('Last played', '/library', 'overview-played')}
            <LibraryAlbumsGridV2
              albums={lastPlayedAlbums}
              hrefFor={albumHref}
              layout="shelf"
              menu
            />
          </section>
        {/if}

        <!-- New to you: what album completion brought in and you haven't heard yet -->
        {#if newToYouAlbums.length > 0}
          <section aria-labelledby="overview-new">
            {@render sectionHeader('New to you', '/library', 'overview-new')}
            <LibraryAlbumsGridV2 albums={newToYouAlbums} hrefFor={albumHref} layout="shelf" menu />
          </section>
        {/if}

        {#if discoverAlbums.length > 0}
          <section aria-labelledby="overview-discover">
            {@render sectionHeader('Discover — never played', '/library', 'overview-discover')}
            <LibraryAlbumsGridV2 albums={discoverAlbums} hrefFor={albumHref} layout="shelf" menu />
          </section>
        {/if}

        {#if randomArtists.length > 0}
          <section aria-labelledby="overview-artists">
            {@render sectionHeader('Artists to revisit', '/artists', 'overview-artists')}
            <ScrollArea
              orientation="horizontal"
              data-scroll-x=""
              class="[&>[data-slot=scroll-area-viewport]]:snap-x [&>[data-slot=scroll-area-viewport]]:scroll-px-4 md:[&>[data-slot=scroll-area-viewport]]:scroll-px-7"
            >
              <div class="flex gap-4 px-4 pt-2 pb-3 md:px-7">
                {#each randomArtists as group (group.key)}
                  <a
                    href={artistHref(group)}
                    class="group focus-visible:ring-ring flex w-[120px] shrink-0 snap-start flex-col items-center gap-2 rounded-lg outline-hidden focus-visible:ring-2 md:w-[136px]"
                    aria-label={`Browse ${group.label}`}
                  >
                    <Cover
                      artist={group.coverArtist}
                      title={group.coverTitle}
                      coverUrl={group.coverUrl}
                      size={136}
                      caption={false}
                      interactive
                      class="aspect-square !h-auto !w-full !rounded-full shadow-[0_2px_10px_rgba(0,0,0,0.15)]"
                    />
                    <div class="w-full min-w-0 text-center">
                      <p
                        class="text-subheadline truncate font-semibold md:text-[12.5px] md:font-medium"
                      >
                        {group.label}
                      </p>
                      <p class="text-footnote text-muted-foreground truncate md:text-[11px]">
                        {group.albumCount} album{group.albumCount === 1 ? '' : 's'}
                      </p>
                    </div>
                  </a>
                {/each}
              </div>
            </ScrollArea>
          </section>
        {/if}

        {#if randomAlbums.length > 0}
          <section aria-labelledby="overview-shelves">
            {@render sectionHeader('From the shelves', '/library', 'overview-shelves')}
            <LibraryAlbumsGridV2 albums={randomAlbums} hrefFor={albumHref} layout="shelf" menu />
          </section>
        {/if}
      {/if}
    </div>
  </ScrollArea>
</div>

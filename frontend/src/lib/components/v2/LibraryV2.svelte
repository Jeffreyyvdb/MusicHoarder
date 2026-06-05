<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { Search, X } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import AlbumPage from '$lib/components/file-browser/AlbumPage.svelte';
  import TrackList from '$lib/components/file-browser/TrackList.svelte';
  import LibraryAlbumsGridV2 from '$lib/components/v2/LibraryAlbumsGridV2.svelte';
  import LibraryArtistsGridV2 from '$lib/components/v2/LibraryArtistsGridV2.svelte';
  import {
    buildAlbumsFromSongs,
    buildArtistGroups,
    fetchAlbumCanonicalStatuses,
    type AlbumStatusInfo,
    type AlbumSummary,
    type ApiSong,
    type GroupSummary
  } from '$lib/api-client';
  import { isBuiltSong } from '$lib/album-sections';
  import { parseBrowseFilter, applyBrowseFilter, browseFilterLabel } from '$lib/browse-filter';
  import { breadcrumbStore } from '$lib/stores/breadcrumbs.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';

  // The song-detail panel is now the global SongDetailHost (mounted in the app
  // shell), so Library no longer hosts its own resizable side-pane / bottom
  // Sheet — track selection just drives the shared store. The desktop/mobile
  // form-factor split lives in SongDetailHost.

  type LibraryTab = 'albums' | 'artists' | 'tracks';
  type Props = {
    /** Which sub-view this route hosts. The sub-nav navigates between routes. */
    tab: LibraryTab;
  };
  const { tab }: Props = $props();

  // ── data layer (shared songs store, also feeds the global detail panel) ─────
  const songs = $derived(songsStore.songs);
  const isLoading = $derived(songsStore.isLoading);

  $effect(() => {
    void songsStore.loadSongs();
    songsStore.startLive();
    return () => songsStore.stopLive();
  });

  // ── URL state ───────────────────────────────────────────────────────────────
  const albumKey = $derived(page.url.searchParams.get('album'));
  const trackParam = $derived(page.url.searchParams.get('track'));
  const songParam = $derived(page.url.searchParams.get('song'));
  const browse = $derived(parseBrowseFilter(page.url.searchParams));

  // Local search box (matches the prototype's header search, not a URL param so
  // the v1 routes stay untouched).
  let query = $state('');

  // ── derivations (only clean/built songs make up the library) ────────────────
  const builtSongs = $derived(songs.filter(isBuiltSong));
  const browseScoped = $derived(applyBrowseFilter(builtSongs, browse));

  const allAlbums = $derived(buildAlbumsFromSongs(builtSongs));
  const scopedAlbums = $derived(buildAlbumsFromSongs(browseScoped));

  // Provider-link status per album (linked / localOnly / pending) for the grid corner badges.
  // One batch lookup, refreshed when the album set changes.
  let albumStatuses = $state<Map<string, AlbumStatusInfo>>(new Map());
  $effect(() => {
    const pairs = allAlbums.map((a) => ({ artist: a.artist, album: a.title }));
    if (pairs.length === 0) {
      albumStatuses = new Map();
      return;
    }
    let cancelled = false;
    void fetchAlbumCanonicalStatuses(pairs)
      .then((map) => {
        if (!cancelled) albumStatuses = map;
      })
      .catch(() => {
        // Badges are best-effort; leave them off on error.
      });
    return () => {
      cancelled = true;
    };
  });

  function albumMatchesQuery(a: AlbumSummary, q: string): boolean {
    return a.title.toLowerCase().includes(q) || a.artist.toLowerCase().includes(q);
  }

  const filteredAlbums = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return scopedAlbums;
    return scopedAlbums.filter((a) => albumMatchesQuery(a, q));
  });

  const artistGroups = $derived(buildArtistGroups(builtSongs));
  const filteredArtists = $derived.by(() => {
    const q = query.trim().toLowerCase();
    if (!q) return artistGroups;
    return artistGroups.filter((g) => g.label.toLowerCase().includes(q));
  });

  // Tracks tab: scope by browse filter + local search; the TrackList does its own
  // sort/format filtering, so we only narrow by query here.
  const tracksScoped = $derived(browseScoped);

  const totalTracks = $derived(builtSongs.length);
  const artistCount = $derived(artistGroups.length);

  // ── album drilldown (reuses AlbumPage + TrackPanel) ─────────────────────────
  const openAlbum = $derived.by(() => {
    if (!albumKey) return null;
    return (
      filteredAlbums.find((a) => a.key === albumKey) ??
      allAlbums.find((a) => a.key === albumKey) ??
      null
    );
  });

  $effect(() => {
    if (openAlbum) {
      breadcrumbStore.setAlbum({ artist: openAlbum.artist, title: openAlbum.title });
    } else {
      breadcrumbStore.clear();
    }
    return () => breadcrumbStore.clear();
  });

  // Deep-link via ?song= — resolve the owning album, set the drilldown context
  // and surface it as ?track= (which the sync effect below opens in the panel).
  let appliedSongDeepLink: number | null = null;
  $effect(() => {
    if (isLoading) return;
    if (!songParam) return;
    const songId = Number.parseInt(songParam, 10);
    if (!Number.isFinite(songId) || appliedSongDeepLink === songId) return;
    const owningAlbum = allAlbums.find((a) => a.songs.some((s) => s.id === songId));
    if (!owningAlbum) return;
    appliedSongDeepLink = songId;
    const url = new URL(page.url);
    url.searchParams.delete('song');
    url.searchParams.set('album', owningAlbum.key);
    url.searchParams.set('track', String(songId));
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  });

  // Highlighted row in the list/album views follows the URL `?track=` param.
  const tracksSelectedId = $derived(trackParam ? Number.parseInt(trackParam, 10) : null);

  // `?track=` is the shareable deep-link for an open panel; mirror it into the
  // global detail store — open on set, close on clear. The guard keeps an
  // unrelated reactive change (e.g. openAlbum resolving) from re-firing it.
  let syncedTrackParam: string | null = null;
  $effect(() => {
    const tp = trackParam;
    if (tp === syncedTrackParam) return;
    syncedTrackParam = tp;
    if (tp) {
      const id = Number.parseInt(tp, 10);
      if (Number.isFinite(id)) songDetail.open(id, openAlbum?.key);
    } else {
      songDetail.close();
    }
  });

  // Closing the panel from the host (X / Cmd+I) leaves a stale `?track=`; strip
  // it so the URL and the row highlight stay in sync with the closed panel.
  $effect(() => {
    if (songDetail.isOpen || !trackParam) return;
    const url = new URL(page.url);
    url.searchParams.delete('track');
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  });

  function selectTrack(song: ApiSong) {
    const url = new URL(page.url);
    if (tracksSelectedId === song.id) url.searchParams.delete('track');
    else url.searchParams.set('track', String(song.id));
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  }

  // ── hrefs (keep deep-linkable, reuse the v1 ?album= / ?artist= contract) ─────
  function albumHref(a: AlbumSummary): string {
    return `/library?album=${encodeURIComponent(a.key)}`;
  }
  function artistHref(g: GroupSummary): string {
    return `/library?artist=${encodeURIComponent(g.key)}`;
  }

  const enrichedPct = $derived.by(() => {
    if (totalTracks === 0) return null;
    const matched = builtSongs.length;
    return (matched / Math.max(1, songs.length)) * 100;
  });

  function clearArtistFilter() {
    void goto('/library', { noScroll: true });
  }
</script>

{#if openAlbum && tab === 'albums'}
  <!-- Album drilldown. Track selection drives the global SongDetailHost (mounted
       in the app shell), which pushes this page on desktop and is a bottom Sheet
       on mobile — so AlbumPage just renders full-width here. -->
  <AlbumPage album={openAlbum} {isLoading} />
{:else}
  <!-- Header -->
  <header
    class="border-border flex shrink-0 flex-col gap-3 border-b px-4 py-4 sm:flex-row sm:items-end sm:justify-between sm:gap-4 sm:px-7 sm:py-5"
  >
    <div class="min-w-0">
      <div class="text-muted-foreground font-mono text-[10px] tracking-[0.12em] uppercase">
        {totalTracks.toLocaleString()} tracks · {artistCount.toLocaleString()} artists{enrichedPct !=
        null
          ? ` · ${enrichedPct.toFixed(1)}% enriched`
          : ''}
      </div>
      <h1 class="mt-1 text-2xl font-semibold tracking-tight">Library</h1>
      <p class="text-muted-foreground mt-1 hidden max-w-2xl text-xs sm:block">
        The clean output. Click any album to drill in — every track has its own enrichment timeline
        showing which provider supplied each field.
      </p>
    </div>
    <div class="flex shrink-0 items-center gap-2">
      <div class="relative w-full sm:w-[clamp(180px,28vw,280px)]">
        <Search class="text-muted-foreground absolute top-1/2 left-2.5 size-3.5 -translate-y-1/2" />
        <input
          type="search"
          placeholder="Search artists, albums, tracks…"
          bind:value={query}
          class="border-border bg-card focus-visible:ring-ring h-8 w-full rounded-md border pr-2.5 pl-8 text-[12.5px] outline-none focus-visible:ring-2"
        />
      </div>
    </div>
  </header>

  {#if tab === 'tracks'}
    <!-- All tracks: the virtualized TrackList. Selecting a row drives the global
         SongDetailHost (app-shell-mounted), which pushes this view on desktop and
         is a bottom Sheet on mobile. The min-h-0 flex chain keeps TrackList's
         scroll viewport bounded so virtualization works. -->
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      <TrackList
        songs={tracksScoped}
        searchQuery={query}
        {isLoading}
        selectedId={tracksSelectedId}
        onSelect={selectTrack}
        hideHeading
      />
    </div>
  {:else}
    <ScrollArea class="min-h-0 flex-1">
      <div class="flex flex-col gap-4 px-4 py-4 sm:px-7 sm:py-6">
        {#if browse?.artist && (tab === 'albums' || tab === 'artists')}
          <div class="flex items-center gap-2">
            <span
              class="bg-primary/10 text-primary inline-flex items-center gap-1.5 rounded-full px-2.5 py-1 text-[12px]"
            >
              filtering by <b class="font-semibold">{browseFilterLabel(browse)}</b>
              <button
                type="button"
                onclick={clearArtistFilter}
                aria-label="Clear artist filter"
                class="hover:text-primary/70 inline-flex transition-colors"
              >
                <X class="size-3" />
              </button>
            </span>
          </div>
        {/if}

        {#if tab === 'albums'}
          <LibraryAlbumsGridV2 albums={filteredAlbums} hrefFor={albumHref} {isLoading} statuses={albumStatuses} />
          {#if filteredAlbums.length > 0}
            <div class="text-muted-foreground text-center text-[11px]">
              {filteredAlbums.length.toLocaleString()} album{filteredAlbums.length === 1 ? '' : 's'}
            </div>
          {/if}
        {:else if tab === 'artists'}
          <LibraryArtistsGridV2 groups={filteredArtists} hrefFor={artistHref} {isLoading} />
        {/if}
      </div>
    </ScrollArea>
  {/if}
{/if}

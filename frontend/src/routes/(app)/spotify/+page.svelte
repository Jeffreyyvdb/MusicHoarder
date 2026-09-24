<script lang="ts">
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import { replaceUrl } from '$lib/navigation/replace-url';
  import { Button } from '$lib/components/ui/button';
  import { SearchField } from '$lib/components/ui/search-field';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import {
    fetchSpotifyStatus,
    fetchSpotifyConnectUrl,
    disconnectSpotify,
    fetchSpotifyLikedSongs,
    fetchSpotifyPlaylists,
    fetchSpotifyCredentials,
    addWishlistSource,
    type SpotifyStatusResponse,
    type SpotifyApiTrack,
    type SpotifyApiPlaylist,
    type SpotifyCredentialsResponse
  } from '$lib/api-client';
  import {
    Music2,
    Heart,
    ListMusic,
    Clock,
    AlertCircle,
    CircleCheck,
    LogOut,
    ExternalLink,
    Loader2,
    Settings,
    KeyRound,
    Gift,
    X
  } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import SpotifyTrackRow from '$lib/components/spotify/SpotifyTrackRow.svelte';
  import PlaylistCard from '$lib/components/spotify/PlaylistCard.svelte';
  import PaginationControls from '$lib/components/spotify/PaginationControls.svelte';
  import TrackListSkeleton from '$lib/components/spotify/TrackListSkeleton.svelte';
  import PlaylistGridSkeleton from '$lib/components/spotify/PlaylistGridSkeleton.svelte';
  import PlaylistDetailView from '$lib/components/spotify/PlaylistDetailView.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';

  let status = $state<SpotifyStatusResponse | null>(null);
  let credentials = $state<SpotifyCredentialsResponse | null>(null);
  let isLoadingStatus = $state(true);
  let isConnecting = $state(false);
  let isDisconnecting = $state(false);
  let confirmDisconnect = $state(false);

  // A phone scrolls one growing list of likes (more load as the end nears); a desktop keeps the
  // numbered pages, where a table of 50 with a pager is the familiar shape.
  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  // The two Spotify views: a segmented control in the nav bar. The view is in the URL (?tab=), so
  // Back from a playlist returns to the Playlists grid rather than to Liked songs. Switching
  // views replaces the URL — it is a filter of the page, not a page of its own.
  type SpotifyTab = 'liked' | 'playlists';
  const SPOTIFY_TABS = [
    { id: 'liked', label: 'Liked songs' },
    { id: 'playlists', label: 'Playlists' }
  ];
  const spotifyTab = $derived<SpotifyTab>(
    page.url.searchParams.get('tab') === 'playlists' ? 'playlists' : 'liked'
  );
  function selectTab(id: string) {
    void replaceUrl(id === 'playlists' ? '/spotify?tab=playlists' : '/spotify');
  }
  const PLAYLISTS_HREF = '/spotify?tab=playlists';

  let error = $state<string | null>(null);
  let oauthBanner = $state<{ type: 'success' | 'error'; message: string } | null>(null);

  let likedSongs = $state<SpotifyApiTrack[]>([]);
  let likedTotal = $state<number | null>(null);
  // `likedSongs` covers [likedOffset, likedEnd): one page on a desktop, every page loaded so far
  // on a phone.
  let likedOffset = $state(0);
  let likedEnd = $state(0);
  let isLoadingLiked = $state(false);
  let isLoadingMore = $state(false);
  let likedError = $state<string | null>(null);
  let loadMoreError = $state<string | null>(null);
  let likedSearchQuery = $state('');
  const hasMoreLiked = $derived(likedTotal != null && likedEnd < likedTotal);

  let playlists = $state<SpotifyApiPlaylist[]>([]);
  let playlistsLoaded = $state(false);
  let isLoadingPlaylists = $state(false);
  let playlistsError = $state<string | null>(null);
  let playlistSearchQuery = $state('');

  // An open playlist is a page pushed from the grid: /spotify?tab=playlists&playlist=<id>.
  const playlistParam = $derived(page.url.searchParams.get('playlist'));
  const selectedPlaylist = $derived(
    playlistParam ? (playlists.find((p) => p.spotifyId === playlistParam) ?? null) : null
  );
  // A link to a playlist this account doesn't have (or no longer has): back to the grid.
  $effect(() => {
    if (playlistParam && playlistsLoaded && !selectedPlaylist) {
      untrack(() => void replaceUrl(PLAYLISTS_HREF, { noScroll: false }));
    }
  });

  let expandedLikedMatchIds = $state(new Set<string>());

  // The page's one scroller (the nav bar's): the playlist grid's scroll memory and the end-of-list
  // observer both use it.
  let scroller = $state<HTMLElement | null>(null);

  // ── Wishlist actions ──────────────────────────────────────────────────────
  // Off by default: collecting a source used to mean every future like was fetched too, which is how
  // a library fills with thousands of tracks nobody went looking for. Adding a source now takes a
  // one-time snapshot unless you ask for more. Existing sources keep whatever they were set to — flip
  // them on the Wishlist page.
  let wishlistAutoSync = $state(false);
  let addingLiked = $state(false);
  let addingPlaylistId = $state<string | null>(null);

  async function addLikedToWishlist() {
    addingLiked = true;
    try {
      await addWishlistSource('LikedSongs', { autoSync: wishlistAutoSync });
      toast.success('Adding your liked songs to the wishlist — tracks will appear shortly.', {
        description: wishlistAutoSync
          ? 'Songs you like from now on are fetched too.'
          : 'A one-time snapshot. To fetch songs you like later too, tick “Follow new likes” in More first.'
      });
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to add to wishlist');
    } finally {
      addingLiked = false;
    }
  }

  async function addPlaylistToWishlist(playlist: SpotifyApiPlaylist) {
    addingPlaylistId = playlist.spotifyId;
    try {
      await addWishlistSource('Playlist', {
        playlistId: playlist.spotifyId,
        autoSync: wishlistAutoSync
      });
      toast.success(`Adding “${playlist.name}” to your wishlist — tracks will appear shortly.`);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to add playlist to wishlist');
    } finally {
      addingPlaylistId = null;
    }
  }

  const likedLimit = 50;

  async function loadStatus() {
    isLoadingStatus = true;
    error = null;
    try {
      const [statusResult, credsResult] = await Promise.all([
        fetchSpotifyStatus().catch(
          () =>
            ({
              connected: false,
              hasCredentials: false,
              tokenExpired: false
            }) as SpotifyStatusResponse
        ),
        fetchSpotifyCredentials().catch(
          () => ({ clientId: null, hasClientSecret: false }) as SpotifyCredentialsResponse
        )
      ]);
      status = statusResult;
      credentials = credsResult;
    } catch {
      status = { connected: false, hasCredentials: false, tokenExpired: false };
      credentials = { clientId: null, hasClientSecret: false };
    } finally {
      isLoadingStatus = false;
    }
  }

  $effect(() => {
    void loadStatus();
  });

  // Read OAuth callback query params, set banner, clean the URL, then reload status.
  $effect(() => {
    const connected = page.url.searchParams.get('spotify_connected');
    const oauthErr = page.url.searchParams.get('spotify_error');
    if (connected !== '1' && oauthErr == null) return;

    if (connected === '1') {
      oauthBanner = { type: 'success', message: 'Spotify connected successfully.' };
    } else if (oauthErr != null) {
      oauthBanner = { type: 'error', message: oauthErr };
    }
    void replaceUrl('/spotify', { keepFocus: false });
    void loadStatus();
  });

  // Only the latest request may land: a phone's scroll and a width change can overlap them.
  let likedRequest = 0;

  /** Replace the list with the page at `offset` (a desktop page turn, a first load, a retry). */
  async function loadLikedSongs(offset: number) {
    const req = ++likedRequest;
    isLoadingLiked = true;
    isLoadingMore = false;
    likedError = null;
    loadMoreError = null;
    try {
      const result = await fetchSpotifyLikedSongs(offset, likedLimit);
      if (req !== likedRequest) return;
      likedSongs = result.items;
      likedTotal = result.total;
      likedOffset = result.offset;
      likedEnd = result.items.length > 0 ? result.offset + result.items.length : result.total;
    } catch (err) {
      if (req !== likedRequest) return;
      likedError = err instanceof Error ? err.message : 'Failed to load liked songs';
    } finally {
      if (req === likedRequest) isLoadingLiked = false;
    }
  }

  /** A phone's next page, appended to what is on screen. */
  async function loadMoreLiked() {
    if (isLoadingLiked || isLoadingMore || !hasMoreLiked) return;
    const req = ++likedRequest;
    isLoadingMore = true;
    loadMoreError = null;
    try {
      const result = await fetchSpotifyLikedSongs(likedEnd, likedLimit);
      if (req !== likedRequest) return;
      likedSongs = [...likedSongs, ...result.items];
      likedTotal = result.total;
      // An empty page ends the list rather than asking for the same offset forever.
      likedEnd = result.items.length > 0 ? result.offset + result.items.length : result.total;
    } catch (err) {
      if (req !== likedRequest) return;
      loadMoreError = err instanceof Error ? err.message : 'Failed to load more liked songs';
    } finally {
      if (req === likedRequest) isLoadingMore = false;
    }
  }

  // The end of a phone's list: when it comes within a screen of the viewport, the next page loads.
  // Not while searching — the search covers what is loaded, and paging through all of a large
  // library behind the user's back to feed it would hammer Spotify — and not after a failure,
  // which waits for Try again.
  let likedSentinel = $state<HTMLElement | null>(null);
  let sentinelNear = $state(false);
  $effect(() => {
    const el = likedSentinel;
    const root = scroller;
    if (!el || !root) return;
    const io = new IntersectionObserver(
      (entries) => (sentinelNear = entries.some((e) => e.isIntersecting)),
      { root, rootMargin: '0px 0px 600px 0px' }
    );
    io.observe(el);
    return () => {
      io.disconnect();
      sentinelNear = false;
    };
  });
  $effect(() => {
    if (
      compact &&
      sentinelNear &&
      hasMoreLiked &&
      !isLoadingLiked &&
      !isLoadingMore &&
      !loadMoreError &&
      !likedSearchQuery
    ) {
      untrack(() => void loadMoreLiked());
    }
  });

  async function loadPlaylists() {
    isLoadingPlaylists = true;
    playlistsError = null;
    try {
      const result = await fetchSpotifyPlaylists();
      // Spotify can return the same playlist twice (e.g. owned + followed).
      // Dedupe by spotifyId so the keyed each block doesn't crash.
      const seen = new Set<string>();
      playlists = result.items.filter((p) => {
        if (seen.has(p.spotifyId)) return false;
        seen.add(p.spotifyId);
        return true;
      });
    } catch (err) {
      playlistsError = err instanceof Error ? err.message : 'Failed to load playlists';
    } finally {
      isLoadingPlaylists = false;
      playlistsLoaded = true;
    }
  }

  // Load data when Spotify connects. The likes start over from the top when the width crosses
  // md: a phone's grown list and a desktop's one page are different shapes of the same data.
  $effect(() => {
    if (!status?.connected) return;
    void compact;
    untrack(() => void loadLikedSongs(0));
  });
  $effect(() => {
    if (status?.connected) untrack(() => void loadPlaylists());
  });

  async function handleConnect() {
    isConnecting = true;
    error = null;
    try {
      const result = await fetchSpotifyConnectUrl();
      window.location.href = result.authorizationUrl;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to start Spotify connection';
      isConnecting = false;
    }
  }

  async function handleDisconnect() {
    isDisconnecting = true;
    try {
      await disconnectSpotify();
      if (status) {
        status = { ...status, connected: false, connectedAt: null, tokenExpired: false };
      }
      likedSongs = [];
      playlists = [];
      expandedLikedMatchIds = new Set();
      if (playlistParam) void replaceUrl('/spotify');
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to disconnect';
    } finally {
      isDisconnecting = false;
    }
  }

  function toggleExpandedLiked(id: string) {
    const next = new Set(expandedLikedMatchIds);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    expandedLikedMatchIds = next;
  }

  const filteredLikedSongs = $derived(
    likedSearchQuery
      ? likedSongs.filter(
          (t) =>
            t.title.toLowerCase().includes(likedSearchQuery.toLowerCase()) ||
            t.artist.toLowerCase().includes(likedSearchQuery.toLowerCase()) ||
            t.album.toLowerCase().includes(likedSearchQuery.toLowerCase())
        )
      : likedSongs
  );

  const filteredPlaylists = $derived(
    playlistSearchQuery
      ? playlists.filter((p) => p.name.toLowerCase().includes(playlistSearchQuery.toLowerCase()))
      : playlists
  );

  const hasCredentials = $derived(credentials?.hasClientSecret && credentials?.clientId);

  // The one subtitle: what this view holds. When the connection was made is Settings › Sources'
  // business, not this header's. A search covers only the loaded songs, so its count says so.
  const meta = $derived.by(() => {
    if (spotifyTab === 'liked') {
      if (likedTotal == null) return undefined;
      if (likedSearchQuery) {
        const n = filteredLikedSongs.length;
        const scope = compact ? `${likedSongs.length.toLocaleString()} loaded` : 'this page';
        return `${n.toLocaleString()} ${n === 1 ? 'match' : 'matches'} in ${scope}`;
      }
      return `${likedTotal.toLocaleString()} liked songs`;
    }
    if (!playlistsLoaded) return undefined;
    const filtered = playlistSearchQuery ? `${filteredPlaylists.length} of ` : '';
    return `${filtered}${playlists.length} playlists`;
  });

  // The grid unmounts while a playlist is open; put it back where it was on return.
  let gridScrollTop = 0;
  $effect(() => {
    const el = scroller;
    if (el && spotifyTab === 'playlists' && gridScrollTop > 0)
      requestAnimationFrame(() => (el.scrollTop = gridScrollTop));
  });
</script>

{#snippet oauthNotice()}
  {#if oauthBanner}
    <div
      role={oauthBanner.type === 'error' ? 'alert' : 'status'}
      class="text-subheadline flex items-start gap-2 rounded-xl py-1 pr-1 pl-4 text-left md:text-sm {oauthBanner.type ===
      'success'
        ? 'bg-primary/10 text-foreground'
        : 'bg-destructive/10 text-destructive-text'}"
    >
      {#if oauthBanner.type === 'success'}
        <CircleCheck class="text-primary mt-2.5 size-4 shrink-0" aria-hidden="true" />
      {:else}
        <AlertCircle class="mt-2.5 size-4 shrink-0" aria-hidden="true" />
      {/if}
      <p class="flex-1 py-2">{oauthBanner.message}</p>
      <Button
        variant="ghost"
        size="icon"
        class="size-11 shrink-0 rounded-full md:size-8"
        aria-label="Dismiss"
        onclick={() => (oauthBanner = null)}
      >
        <X />
      </Button>
    </div>
  {/if}
{/snippet}

<!-- The option that qualifies the add, then the account action. Adding (the liked songs, or a
     playlist from its card) takes a one-time snapshot unless this is ticked; then the source keeps
     being fetched — new likes, or songs added to the playlist later. The menu stays open on a
     toggle, so the change is seen to take. -->
{#snippet moreItems()}
  <DropdownMenu.CheckboxItem bind:checked={wishlistAutoSync} closeOnSelect={false}>
    {spotifyTab === 'liked' ? 'Follow new likes' : 'Follow new songs'}
  </DropdownMenu.CheckboxItem>
  <DropdownMenu.Separator />
  <DropdownMenu.Item
    variant="destructive"
    disabled={isDisconnecting}
    onSelect={() => (confirmDisconnect = true)}
  >
    <LogOut /> Disconnect Spotify…
  </DropdownMenu.Item>
{/snippet}

<!-- The Liked songs view's one prominent action. On a phone it is an icon in the bar's capsule
     (named in full for VoiceOver); a desktop bar has room for words. -->
{#snippet likedActions()}
  {#if spotifyTab === 'liked'}
    {#if compact}
      <Button
        size="icon"
        onclick={addLikedToWishlist}
        disabled={addingLiked}
        aria-label="Add liked songs to the wishlist"
      >
        {#if addingLiked}<Loader2 class="animate-spin" />{:else}<Gift />{/if}
      </Button>
    {:else}
      <Button
        size="sm"
        class="h-8 gap-1.5 rounded-full px-3"
        onclick={addLikedToWishlist}
        disabled={addingLiked}
        title="Add liked songs to the wishlist"
      >
        {#if addingLiked}<Loader2 class="animate-spin" />{:else}<Gift />{/if}
        Add to wishlist
      </Button>
    {/if}
  {/if}
{/snippet}

<!-- The end of a phone's liked list: the observer's target, then what is happening there — the
     next page loading, a failure with Try again, or (while searching, when nothing loads on its
     own) how much the search covered and a way to widen it. A button rather than scroll alone, so
     VoiceOver and Switch Control can ask for more too. -->
{#snippet likedListEnd()}
  <div bind:this={likedSentinel} aria-hidden="true" class="h-px"></div>
  {#if isLoadingMore}
    <div role="status" class="flex justify-center py-5">
      <Loader2 class="text-muted-foreground size-5 animate-spin" aria-hidden="true" />
      <span class="sr-only">Loading more liked songs…</span>
    </div>
  {:else if loadMoreError}
    <div class="flex flex-col items-center gap-2 px-6 py-5 text-center">
      <p role="alert" class="text-subheadline text-muted-foreground">{loadMoreError}</p>
      <Button variant="gray" class="h-11 rounded-full px-5" onclick={loadMoreLiked}>
        Try again
      </Button>
    </div>
  {:else if hasMoreLiked}
    <div class="flex flex-col items-center gap-2 px-6 pt-3 pb-5 text-center">
      {#if likedSearchQuery}
        <p class="text-footnote text-muted-foreground">
          Searched the first {likedSongs.length.toLocaleString()} of {(
            likedTotal ?? 0
          ).toLocaleString()} liked songs.
        </p>
      {/if}
      <Button variant="gray" class="h-11 rounded-full px-5" onclick={loadMoreLiked}>
        Load more
      </Button>
    </div>
  {/if}
{/snippet}

<!-- The Spotify page belongs to the Add group. The Liked songs / Playlists views are a
     segmented control in its own nav bar. -->
{#if isLoadingStatus || !status?.connected}
  <div class="flex min-h-0 flex-1 flex-col">
    <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)">
      <PageToolbarV2 title="Spotify" meta={isLoadingStatus ? undefined : 'Not connected'} />
      {#if isLoadingStatus}
        <div role="status" class="flex justify-center py-20">
          <Loader2 class="text-muted-foreground size-8 animate-spin" aria-hidden="true" />
          <span class="sr-only">Checking the Spotify connection…</span>
        </div>
      {:else}
        <div class="mx-auto flex max-w-md flex-col items-center px-6 pt-8 pb-10 text-center">
          <!-- Spotify's green as a FILL with a black glyph (8.1:1) — never as text on a tint. -->
          <div class="mb-6 grid size-20 place-items-center rounded-full bg-[#1DB954] text-black">
            <Music2 class="size-10" aria-hidden="true" />
          </div>
          <h2 class="text-title-2 mb-2 md:text-2xl md:font-bold">Connect Spotify</h2>
          <p class="text-body text-muted-foreground mb-8 md:text-sm">
            Link your Spotify account to browse your playlists and liked songs.
          </p>

          {#if oauthBanner}
            <div class="mb-6 w-full">{@render oauthNotice()}</div>
          {/if}

          {#if error}
            <div
              role="alert"
              class="bg-destructive/10 text-destructive-text text-subheadline mb-6 w-full rounded-xl px-4 py-3 md:text-sm"
            >
              {error}
            </div>
          {/if}

          {#if !hasCredentials}
            <div class="w-full">
              <GroupedList.Section
                class="mx-0"
                contentClass="bg-muted"
                footer="You need to configure your Spotify Client ID and Client Secret before connecting."
              >
                <GroupedList.Row
                  icon={KeyRound}
                  label="Spotify API credentials required"
                  sublabel="Add them in Settings"
                />
                <GroupedList.Row href="/settings" chevron>
                  <span class="text-body text-primary flex items-center gap-2 md:text-sm">
                    <Settings class="size-4" aria-hidden="true" /> Go to Settings
                  </span>
                </GroupedList.Row>
              </GroupedList.Section>
            </div>
          {:else}
            <Button
              size="pill"
              class="bg-[#1DB954] px-8 text-black hover:bg-[#1DB954]/90 md:h-10 md:text-sm"
              onclick={handleConnect}
              disabled={isConnecting}
            >
              {#if isConnecting}
                <Loader2 class="animate-spin" />
              {:else}
                <ExternalLink />
              {/if}
              Connect with Spotify
            </Button>
          {/if}
        </div>
      {/if}
    </div>
  </div>
{:else if playlistParam}
  {#if selectedPlaylist}
    <PlaylistDetailView playlist={selectedPlaylist} backHref={PLAYLISTS_HREF} />
  {:else}
    <!-- A pushed playlist whose list is still loading (a deep link or a reload). -->
    <div class="flex min-h-0 flex-1 flex-col">
      <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)">
        <PageToolbarV2
          title="Playlist"
          largeTitle={false}
          back={{ label: 'Spotify', href: PLAYLISTS_HREF }}
        />
        <TrackListSkeleton />
      </div>
    </div>
  {/if}
{:else}
  <div class="flex min-h-0 flex-1 flex-col">
    <div
      bind:this={scroller}
      class="min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)"
    >
      <PageToolbarV2
        title="Spotify"
        {meta}
        metaFrom="lg"
        tabs={SPOTIFY_TABS}
        activeTab={spotifyTab}
        onselectTab={selectTab}
        actions={spotifyTab === 'liked' ? likedActions : undefined}
        more={moreItems}
      >
        {#snippet search()}
          {#if spotifyTab === 'liked'}
            <!-- Spotify has no search over saved tracks, so this searches what is loaded: a
                 phone's growing list (the list's end says how much that is), or a desktop's page. -->
            <SearchField
              bind:value={likedSearchQuery}
              label={compact ? 'Search liked songs' : 'Filter this page'}
            />
          {:else}
            <SearchField bind:value={playlistSearchQuery} label="Search playlists" />
          {/if}
        {/snippet}
      </PageToolbarV2>

      {#if oauthBanner}
        <div class="px-4 pt-2 pb-2 md:px-7">{@render oauthNotice()}</div>
      {/if}

      {#if spotifyTab === 'liked'}
        {#if likedError}
          <div class="flex flex-col items-center justify-center px-6 py-12 text-center">
            <AlertCircle class="text-destructive-text mb-3 size-10" aria-hidden="true" />
            <p class="text-body text-muted-foreground md:text-sm">{likedError}</p>
            <Button
              variant="outline"
              class="mt-4 h-11 rounded-full px-5 md:h-8 md:rounded-lg md:px-3"
              onclick={() => loadLikedSongs(likedOffset)}
            >
              Retry
            </Button>
          </div>
        {:else if isLoadingLiked}
          <TrackListSkeleton />
        {:else}
          <div
            class="text-muted-foreground border-separator hidden items-center gap-3 border-b px-3 py-2 text-xs md:mx-4 md:flex"
          >
            <span class="w-8 text-right">#</span>
            <span class="size-10"></span>
            <span class="flex-1">Title</span>
            <span class="hidden max-w-[200px] md:block">Album</span>
            <span class="hidden w-24 text-right lg:block">Date added</span>
            <span class="w-12 text-right"
              ><Clock class="inline size-3.5" aria-label="Duration" /></span
            >
            <span class="w-[120px] shrink-0 text-right">Library</span>
          </div>
          <div class="py-1 md:px-4 md:py-2">
            {#each filteredLikedSongs as track, i (`${track.spotifyId}-${i}`)}
              <SpotifyTrackRow
                {track}
                index={likedOffset + i}
                showDateAdded
                expanded={expandedLikedMatchIds.has(track.spotifyId)}
                onToggleExpand={() => toggleExpandedLiked(track.spotifyId)}
              />
            {/each}
          </div>
          {#if filteredLikedSongs.length === 0}
            <!-- On a phone the list's end follows, saying how far the search reached. -->
            {#if !likedSearchQuery}
              <EmptyState
                icon={Heart}
                title="No liked songs yet"
                hint="Songs you like on Spotify show up here."
              />
            {:else}
              <EmptyState
                icon={Heart}
                title={compact ? 'No matches' : 'No matches on this page'}
                hint={compact
                  ? `No loaded song matches “${likedSearchQuery}”.`
                  : `The search covers this page only — try another page for “${likedSearchQuery}”.`}
                action={{ label: 'Clear search', onclick: () => (likedSearchQuery = '') }}
              />
            {/if}
          {/if}
          {#if compact}
            {@render likedListEnd()}
          {:else}
            <!-- The pager hides itself when everything fits on one page (or there is nothing at
                 all). It stays under a search miss: the search covers this page only, so the
                 other pages are exactly where to look next. -->
            <PaginationControls
              offset={likedOffset}
              limit={likedLimit}
              total={likedTotal ?? 0}
              onPageChange={loadLikedSongs}
              isLoading={isLoadingLiked}
            />
          {/if}
        {/if}
      {:else if playlistsError}
        <div class="flex flex-col items-center justify-center px-6 py-12 text-center">
          <AlertCircle class="text-destructive-text mb-3 size-10" aria-hidden="true" />
          <p class="text-body text-muted-foreground md:text-sm">{playlistsError}</p>
          <Button
            variant="outline"
            class="mt-4 h-11 rounded-full px-5 md:h-8 md:rounded-lg md:px-3"
            onclick={loadPlaylists}
          >
            Retry
          </Button>
        </div>
      {:else if isLoadingPlaylists}
        <PlaylistGridSkeleton />
      {:else if filteredPlaylists.length === 0}
        {#if playlistSearchQuery}
          <EmptyState
            icon={ListMusic}
            title="No matching playlists"
            hint={`Nothing matches “${playlistSearchQuery}”.`}
            action={{ label: 'Clear search', onclick: () => (playlistSearchQuery = '') }}
          />
        {:else}
          <EmptyState
            icon={ListMusic}
            title="No playlists yet"
            hint="Playlists you make or follow on Spotify show up here."
          />
        {/if}
      {:else}
        <div
          class="grid grid-cols-2 gap-x-4 gap-y-5 px-4 pt-2 pb-4 sm:grid-cols-3 md:grid-cols-4 md:px-7 md:pt-4 lg:grid-cols-5"
        >
          {#each filteredPlaylists as playlist (playlist.spotifyId)}
            <PlaylistCard
              {playlist}
              href="{PLAYLISTS_HREF}&playlist={encodeURIComponent(playlist.spotifyId)}"
              onOpen={() => (gridScrollTop = scroller?.scrollTop ?? 0)}
              onAddToWishlist={() => addPlaylistToWishlist(playlist)}
              isAdding={addingPlaylistId === playlist.spotifyId}
            />
          {/each}
        </div>
      {/if}
    </div>
  </div>
{/if}

<!-- Disconnecting drops the saved Spotify session; it is not undone by a tap, so it asks. -->
<AlertDialog.Root bind:open={confirmDisconnect}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Disconnect Spotify?</AlertDialog.Title>
      <AlertDialog.Description>
        The saved Spotify sign-in and its library matches are cleared, so liked songs and playlists
        stop showing here until you connect again. Nothing in your library is removed.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action
        variant="destructive"
        onclick={() => {
          confirmDisconnect = false;
          void handleDisconnect();
        }}
      >
        Disconnect
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

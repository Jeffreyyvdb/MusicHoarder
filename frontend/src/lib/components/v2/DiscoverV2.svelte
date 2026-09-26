<script lang="ts">
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import FilterChip from '$lib/components/v2/FilterChip.svelte';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Switch } from '$lib/components/ui/switch';
  import { SearchField } from '$lib/components/ui/search-field';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import {
    Compass,
    Link2,
    Loader2,
    ListMusic,
    AlertCircle,
    Plus,
    CircleCheck,
    Music,
    Gift
  } from '@lucide/svelte';
  import {
    fetchDiscoverGenres,
    fetchDiscoverPlaylists,
    fetchDiscoverPlaylist,
    resolveDiscoverUrl,
    addWishlistSource,
    subscribeToResolvedPlaylist,
    playlistProviderLabel,
    removeWishlistSource,
    setWishlistSourceAutoSync,
    fetchSettings,
    ApiError,
    type DiscoverGenre,
    type DiscoverPlaylistSummary,
    type DiscoverPlaylistDetail,
    type DiscoverResolveResult
  } from '$lib/api-client';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';
  import { albumTint } from '$lib/album-tint';
  import { computeInitials } from '$lib/formatters';
  import { toast } from 'svelte-sonner';
  import DiscoverPlaylistCard from '$lib/components/discover/DiscoverPlaylistCard.svelte';
  import { heroTitle } from '$lib/components/discover/hero-title';
  import PlaylistGridSkeleton from '$lib/components/spotify/PlaylistGridSkeleton.svelte';
  import TrackListSkeleton from '$lib/components/spotify/TrackListSkeleton.svelte';

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  // ── Browse state ────────────────────────────────────────────────────────────
  let genres = $state<DiscoverGenre[]>([]);
  // null = "Top" (global top playlists).
  let selectedGenreId = $state<number | null>(null);
  let playlists = $state<DiscoverPlaylistSummary[]>([]);
  let loadingPlaylists = $state(true);
  let playlistsError = $state<string | null>(null);

  let searchQuery = $state('');
  let debouncedSearch = $state('');

  // ── Detail state ──────────────────────────────────────────────────────────
  // The open playlist is a page of its own: /discover?playlist=deezer:<id>, a push from the grid,
  // so Back returns to the grid (with its genre, search and scroll intact — this component stays
  // mounted) and a playlist can be linked. A bare id is accepted too.
  const DEEZER = 'deezer:';
  const playlistParam = $derived.by(() => {
    const raw = page.url.searchParams.get('playlist');
    if (!raw) return null;
    return raw.startsWith(DEEZER) ? raw.slice(DEEZER.length) || null : raw;
  });
  function playlistHref(id: string): string {
    return `/discover?playlist=${encodeURIComponent(DEEZER + id)}`;
  }
  let detail = $state<DiscoverPlaylistDetail | null>(null);
  let loadingDetail = $state(false);
  let detailError = $state<string | null>(null);

  // An open playlist names its page, so the Back label of anything pushed from it and the
  // browser-tab title (which the route announcer reads) say the playlist rather than "Discover".
  // Only once the detail is the one the URL names: a previous playlist lingers while the next
  // loads. titleOf is read first so the effect runs again once the navigation is recorded — until
  // then the entry does not exist and setTitle has nothing to patch.
  $effect(() => {
    const title =
      playlistParam && detail?.playlist.id === playlistParam ? detail.playlist.title : null;
    if (title && tabMemory.titleOf(page.url) !== title) tabMemory.setTitle(page.url, title);
  });

  // ── Add-by-link state (a sheet on every width) ─────────────────────────────
  let linkOpen = $state(false);
  let linkUrl = $state('');
  let resolving = $state(false);
  let resolveResult = $state<DiscoverResolveResult | null>(null);
  let resolveError = $state<{ kind: 'editorial' | 'generic'; message: string } | null>(null);
  let subscribingLink = $state(false);

  // ── Shared feedback ───────────────────────────────────────────────────────
  let busyKeys = $state(new Set<string>());
  // Deploy-time switch: whether subscribing also auto-downloads. null until settings load.
  let downloadsEnabled = $state<boolean | null>(null);

  function setBusy(key: string, on: boolean) {
    const next = new Set(busyKeys);
    if (on) next.add(key);
    else next.delete(key);
    busyKeys = next;
  }

  // Keep a playlist's subscription state consistent across the grid and the open detail view.
  function syncSubState(id: string, patch: Partial<DiscoverPlaylistSummary>) {
    for (const p of playlists) if (p.id === id) Object.assign(p, patch);
    if (detail && detail.playlist.id === id) Object.assign(detail.playlist, patch);
  }

  // Monotonic request tokens: a slower earlier response must not overwrite a newer one when the
  // genre/search switches (grid) or another playlist is opened (detail) mid-flight.
  let playlistsReq = 0;
  let detailReq = 0;

  // ── Loading ───────────────────────────────────────────────────────────────
  async function loadPlaylists(quiet = false) {
    const token = ++playlistsReq;
    if (!quiet) {
      loadingPlaylists = true;
      playlistsError = null;
    }
    try {
      const params = debouncedSearch
        ? { search: debouncedSearch }
        : selectedGenreId != null
          ? { genreId: selectedGenreId }
          : {};
      const res = await fetchDiscoverPlaylists(params);
      if (token !== playlistsReq) return; // superseded by a newer load
      playlists = res.playlists;
    } catch (err) {
      if (token !== playlistsReq) return;
      if (!quiet) playlistsError = err instanceof Error ? err.message : 'Failed to load playlists';
    } finally {
      if (token === playlistsReq && !quiet) loadingPlaylists = false;
    }
  }

  // Genres + settings load once on mount (no reactive reads).
  $effect(() => {
    void (async () => {
      try {
        genres = (await fetchDiscoverGenres()).genres;
      } catch {
        // Non-fatal — the grid still works without genre chips.
      }
    })();
    void (async () => {
      try {
        downloadsEnabled = (await fetchSettings()).downloads.enabled;
      } catch {
        // Non-fatal — we just omit the auto-download note.
      }
    })();
  });

  // Debounce the search box (~300ms).
  $effect(() => {
    const q = searchQuery;
    const id = setTimeout(() => {
      debouncedSearch = q.trim();
    }, 300);
    return () => clearTimeout(id);
  });

  // Reload the grid whenever the debounced search or the selected genre changes.
  $effect(() => {
    void debouncedSearch;
    void selectedGenreId;
    void loadPlaylists();
  });

  async function loadDetail(id: string, quiet = false) {
    const token = ++detailReq;
    if (!quiet) {
      loadingDetail = true;
      detailError = null;
    }
    try {
      const res = await fetchDiscoverPlaylist(id);
      if (token !== detailReq) return; // superseded by opening another playlist
      detail = res;
    } catch (err) {
      if (token !== detailReq) return;
      if (!quiet) detailError = err instanceof Error ? err.message : 'Failed to load playlist';
    } finally {
      if (token === detailReq && !quiet) loadingDetail = false;
    }
  }

  // The URL opens (and closes) the detail. Opening from the grid seeds the header from the grid's
  // summary so it shows at once; loadDetail then fills in the tracks.
  $effect(() => {
    const id = playlistParam;
    untrack(() => {
      if (!id) {
        detailReq++; // drop any response still in flight
        detail = null;
        detailError = null;
        loadingDetail = false;
        return;
      }
      if (detail?.playlist.id === id) return;
      const seed = playlists.find((p) => p.id === id);
      detail = seed ? { playlist: seed, tracks: [] } : null;
      detailError = null;
      void loadDetail(id);
    });
  });

  // ── Subscribe actions (grid + detail) ──────────────────────────────────────
  function subscribedMessage(title: string): string {
    return `Subscribed to “${title}” — new tracks will be synced${
      downloadsEnabled ? ' and downloaded' : ''
    } automatically.`;
  }

  async function subscribe(p: DiscoverPlaylistSummary) {
    setBusy(p.id, true);
    try {
      const res = await addWishlistSource('DeezerPlaylist', {
        deezerPlaylistId: p.id,
        autoSync: true
      });
      syncSubState(p.id, { subscribed: true, sourceId: res.sourceId, autoSync: true });
      toast.success(subscribedMessage(p.title));
      // The wishlist source snapshot runs in the background; refresh once so track
      // in-library / in-wishlist badges catch up.
      setTimeout(() => {
        if (playlistParam === p.id) void loadDetail(p.id, true);
        else void loadPlaylists(true);
      }, 4000);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to subscribe');
    } finally {
      setBusy(p.id, false);
    }
  }

  async function unsubscribe(p: DiscoverPlaylistSummary) {
    if (p.sourceId == null) return;
    setBusy(p.id, true);
    try {
      await removeWishlistSource(p.sourceId);
      syncSubState(p.id, { subscribed: false, sourceId: null, autoSync: null });
      toast.success(`Unsubscribed from “${p.title}”`);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to unsubscribe');
    } finally {
      setBusy(p.id, false);
    }
  }

  // Unsubscribing drops the source's sync history (adding it again starts over), so it asks first.
  let confirmUnsubscribe = $state(false);

  // The switch is bound through a getter/setter: it shows the request's intent while it runs and
  // the source's real state after, so a failed request puts it back instead of leaving the thumb
  // flipped over a setting that did not change.
  let autoSyncPending = $state<boolean | null>(null);
  async function toggleAutoSync(p: DiscoverPlaylistSummary, on: boolean) {
    if (p.sourceId == null) return;
    setBusy(p.id, true);
    autoSyncPending = on;
    try {
      const res = await setWishlistSourceAutoSync(p.sourceId, on);
      syncSubState(p.id, { autoSync: res.autoSync });
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to update auto-sync');
    } finally {
      autoSyncPending = null;
      setBusy(p.id, false);
    }
  }

  // ── Add by link ────────────────────────────────────────────────────────────
  async function onResolve() {
    const url = linkUrl.trim();
    if (!url || resolving) return;
    resolving = true;
    resolveError = null;
    resolveResult = null;
    try {
      resolveResult = await resolveDiscoverUrl(url);
    } catch (err) {
      if (err instanceof ApiError && err.code === 'spotify_editorial_blocked') {
        resolveError = { kind: 'editorial', message: err.message };
      } else {
        resolveError = {
          kind: 'generic',
          message: err instanceof Error ? err.message : 'Could not resolve that link'
        };
      }
    } finally {
      resolving = false;
    }
  }

  async function onSubscribeLink() {
    const r = resolveResult;
    if (!r) return;
    subscribingLink = true;
    try {
      const res = await subscribeToResolvedPlaylist(r);
      resolveResult = { ...r, subscribed: true };
      // Reflect on the grid/detail if this playlist is also visible there.
      syncSubState(r.playlistId, { subscribed: true, sourceId: res.sourceId, autoSync: true });
      toast.success(subscribedMessage(r.title));
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Failed to subscribe');
    } finally {
      subscribingLink = false;
    }
  }

  function clearLink() {
    linkUrl = '';
    resolveResult = null;
    resolveError = null;
  }

  function fmtDuration(ms: number | null): string {
    if (ms == null) return '--:--';
    const total = Math.round(ms / 1000);
    const m = Math.floor(total / 60);
    const s = total % 60;
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  function selectGenre(id: number | null) {
    selectedGenreId = id;
    // Genre selection and free-text search are mutually exclusive; clear the query.
    searchQuery = '';
    debouncedSearch = '';
  }

  const selectedGenreName = $derived(
    selectedGenreId == null ? 'Top' : (genres.find((g) => g.id === selectedGenreId)?.name ?? '')
  );
  const gridMeta = $derived.by(() => {
    if (loadingPlaylists || playlistsError) return undefined;
    const n = `${playlists.length} playlist${playlists.length === 1 ? '' : 's'}`;
    if (debouncedSearch) return `${n} matching “${debouncedSearch}”`;
    return selectedGenreName ? `${selectedGenreName} · ${n}` : n;
  });

  // The cover's tint fills an empty cover (no artwork) — identity lives in the art.
  const tint = $derived(
    detail ? albumTint(detail.playlist.creatorName ?? 'Deezer', detail.playlist.title) : null
  );
  const detailBusy = $derived(detail ? busyKeys.has(detail.playlist.id) : false);

  // The grid unmounts while a playlist is open; put it back where it was on return.
  let gridScroller = $state<HTMLElement | null>(null);
  let gridScrollTop = 0;
  $effect(() => {
    const el = gridScroller;
    if (el && gridScrollTop > 0) requestAnimationFrame(() => (el.scrollTop = gridScrollTop));
  });
  function rememberGridScroll() {
    if (gridScroller) gridScrollTop = gridScroller.scrollTop;
  }

  // On a phone the bar's inline title waits until the hero's title has scrolled under it (see
  // heroTitle); md+ keeps the desktop toolbar's title, beside a hero that is not centred.
  let heroVisible = $state(true);
</script>

<!-- ── Detail: a pushed page ─────────────────────────────────────────────────────────── -->
{#if playlistParam}
  <div
    class="hero-scroller flex min-h-0 flex-1 flex-col"
    data-hero-visible={compact && detail && heroVisible ? '' : undefined}
  >
    <ScrollArea class="min-h-0 flex-1" viewportClass="overscroll-contain">
      <PageToolbarV2
        title={detail?.playlist.title ?? 'Playlist'}
        largeTitle={false}
        back={{ label: 'Discover', href: '/discover' }}
      />

      {#if detail}
        {@const p = detail.playlist}
        <!-- Hero: the cover centred on a phone (Apple Music's playlist page), beside the title on
             desktop. -->
        <div
          class="flex flex-col items-center px-4 pt-3 pb-6 text-center md:flex-row md:items-end md:gap-7 md:px-7 md:pt-7 md:text-left"
        >
          <div
            class="relative grid size-44 shrink-0 place-items-center overflow-hidden rounded-md shadow-[0_12px_32px_rgb(0_0_0/0.22)] md:size-40"
            style="background: linear-gradient(135deg, {tint?.from} 0%, {tint?.to} 100%);"
          >
            {#if p.coverUrl}
              <img
                src={p.coverUrl}
                alt=""
                crossorigin="anonymous"
                draggable="false"
                class="absolute inset-0 size-full object-cover"
              />
            {:else}
              <span class="text-4xl font-bold tracking-[-0.04em] text-white">
                {computeInitials(p.title)}
              </span>
            {/if}
          </div>
          <div class="mt-4 min-w-0 md:mt-0 md:pb-1">
            <h2
              use:heroTitle={(v) => (heroVisible = v)}
              class="text-title-2 text-balance break-words md:text-[26px] md:leading-8"
            >
              {p.title}
            </h2>
            <p class="text-subheadline text-muted-foreground mt-1 md:text-sm">
              Deezer{p.creatorName ? ` · ${p.creatorName}` : ''} · {p.trackCount.toLocaleString()} song{p.trackCount ===
              1
                ? ''
                : 's'}
            </p>
            {#if p.description}
              <p
                class="text-footnote text-muted-foreground mx-auto mt-2 max-w-md text-pretty md:mx-0 md:max-w-2xl md:text-[13px]"
              >
                {p.description}
              </p>
            {/if}
            {#if !p.subscribed}
              <!-- The page's one prominent action. -->
              <Button
                size="pill"
                class="mt-5 min-w-44 md:h-9 md:min-w-0 md:px-4 md:text-sm"
                disabled={detailBusy}
                onclick={() => subscribe(p)}
              >
                {#if detailBusy}<Loader2 class="animate-spin" />{:else}<Plus />{/if}
                Subscribe
              </Button>
              {#if downloadsEnabled}
                <p class="text-footnote text-muted-foreground mt-2 md:text-xs">
                  New tracks added to a subscribed playlist are downloaded into your library
                  automatically.
                </p>
              {/if}
            {/if}
          </div>
        </div>

        {#if p.subscribed}
          <div class="pb-6 md:max-w-xl md:px-7">
            <GroupedList.Section
              header="Subscription"
              contentClass="bg-muted"
              footer={downloadsEnabled
                ? 'New tracks added to this playlist are downloaded into your library automatically.'
                : 'New tracks added to this playlist are added to your wishlist.'}
            >
              <GroupedList.Row label="Auto-sync new tracks">
                {#snippet trailing()}
                  <Switch
                    bind:checked={
                      () => autoSyncPending ?? p.autoSync ?? false,
                      (on) => void toggleAutoSync(p, on)
                    }
                    disabled={detailBusy}
                    aria-label="Auto-sync new tracks"
                  />
                {/snippet}
              </GroupedList.Row>
              <GroupedList.Row
                label="Unsubscribe"
                destructive
                disabled={detailBusy}
                onclick={() => (confirmUnsubscribe = true)}
              />
            </GroupedList.Section>
          </div>
        {/if}

        <!-- Tracks -->
        {#if detailError}
          <div class="flex flex-col items-center justify-center px-6 py-12 text-center">
            <AlertCircle class="text-destructive-text mb-3 size-10" aria-hidden="true" />
            <p class="text-body text-muted-foreground md:text-sm">{detailError}</p>
            <Button
              variant="outline"
              class="mt-4 h-11 rounded-full px-5 md:h-8 md:rounded-lg md:px-3"
              onclick={() => loadDetail(p.id)}
            >
              Retry
            </Button>
          </div>
        {:else if loadingDetail && detail.tracks.length === 0}
          <TrackListSkeleton />
        {:else if detail.tracks.length === 0}
          <div class="flex flex-col items-center justify-center py-12 text-center">
            <ListMusic class="text-muted-foreground mb-3 size-10" aria-hidden="true" />
            <p class="text-body text-muted-foreground md:text-sm">No tracks found</p>
          </div>
        {:else}
          <ul class="md:px-3" aria-label="Songs">
            {#each detail.tracks as track (track.deezerTrackId)}
              <li class="md:hover:bg-accent flex items-center gap-3 pl-4 md:rounded-md">
                <div class="bg-muted size-11 shrink-0 overflow-hidden rounded-sm md:size-10">
                  {#if track.coverUrl}
                    <img
                      src={track.coverUrl}
                      alt=""
                      loading="lazy"
                      class="size-full object-cover"
                      crossorigin="anonymous"
                    />
                  {:else}
                    <div class="flex size-full items-center justify-center">
                      <Music class="text-muted-foreground size-4" aria-hidden="true" />
                    </div>
                  {/if}
                </div>
                <!-- The hairline starts at the text, like a UITableView inset separator. -->
                <div
                  class="after:bg-separator relative flex min-h-14 min-w-0 flex-1 items-center gap-3 self-stretch py-2 pr-4 after:absolute after:inset-x-0 after:bottom-0 after:h-(--hairline) md:after:hidden"
                >
                  <div class="min-w-0 flex-1">
                    <div class="text-body truncate md:text-sm">{track.title}</div>
                    <div class="text-subheadline text-muted-foreground truncate md:text-xs">
                      {track.artist}{track.album ? ` · ${track.album}` : ''}
                    </div>
                  </div>
                  <!-- One trailing status glyph at every width, with its word for VoiceOver (and
                       beside it on desktop). On a phone it is the row's last item, after the
                       duration, as on the Spotify playlist page; its slot keeps its width when
                       empty so the durations still line up. -->
                  <span
                    class="flex shrink-0 items-center gap-1.5 max-md:order-last max-md:w-5 max-md:justify-center md:text-xs md:empty:hidden {track.inLibrary
                      ? 'text-primary'
                      : 'text-muted-foreground'}"
                  >
                    {#if track.inLibrary}
                      <CircleCheck class="size-5 md:size-4" aria-hidden="true" />
                      <span class="sr-only md:not-sr-only">In library</span>
                    {:else if track.inWishlist}
                      <Gift class="size-5 md:size-4" aria-hidden="true" />
                      <span class="sr-only md:not-sr-only">Wishlisted</span>
                    {/if}
                  </span>
                  <span
                    class="text-footnote text-muted-foreground w-10 shrink-0 text-right tabular-nums md:w-12 md:text-xs"
                  >
                    {fmtDuration(track.durationMs)}
                  </span>
                </div>
              </li>
            {/each}
          </ul>
        {/if}
      {:else if detailError}
        <div class="flex flex-col items-center justify-center px-6 py-16 text-center">
          <AlertCircle class="text-destructive-text mb-3 size-10" aria-hidden="true" />
          <p class="text-body text-muted-foreground md:text-sm">{detailError}</p>
          <Button
            variant="outline"
            class="mt-4 h-11 rounded-full px-5 md:h-8 md:rounded-lg md:px-3"
            onclick={() => playlistParam && loadDetail(playlistParam)}
          >
            Retry
          </Button>
        </div>
      {:else}
        <!-- A deep link: nothing to seed the header from until the playlist arrives. -->
        <div
          role="status"
          class="flex flex-col items-center px-4 pt-3 pb-6 md:flex-row md:items-end md:gap-7 md:px-7 md:pt-7"
        >
          <span class="sr-only">Loading playlist…</span>
          <Skeleton class="size-44 rounded-md md:size-40" />
          <div class="mt-4 flex flex-col items-center gap-2 md:items-start">
            <Skeleton class="h-6 w-48" />
            <Skeleton class="h-4 w-32" />
          </div>
        </div>
        <TrackListSkeleton />
      {/if}
    </ScrollArea>
  </div>
{:else}
  <!-- ── Browse ─────────────────────────────────────────────────────────────────────── -->
  <div class="flex min-h-0 flex-1 flex-col">
    <ScrollArea
      bind:viewportRef={gridScroller}
      class="min-h-0 flex-1"
      viewportClass="overscroll-contain"
    >
      <PageToolbarV2 title="Discover" meta={gridMeta} metaFrom="lg">
        {#snippet search()}
          <SearchField bind:value={searchQuery} label="Search playlists" />
        {/snippet}
        {#snippet actions()}
          {#if compact}
            <Button
              variant="ghost"
              size="icon"
              aria-label="Add a playlist from a link"
              onclick={() => (linkOpen = true)}
            >
              <Link2 />
            </Button>
          {:else}
            <Button
              variant="outline"
              size="sm"
              class="h-8 gap-1.5 px-2.5"
              onclick={() => (linkOpen = true)}
            >
              <Link2 class="size-4" />
              <span class="text-nav-sm">Add link…</span>
            </Button>
          {/if}
        {/snippet}
        {#snippet filterRow()}
          <FilterChip
            pressed={selectedGenreId === null && !debouncedSearch}
            onclick={() => selectGenre(null)}>Top</FilterChip
          >
          {#each genres as genre (genre.id)}
            <FilterChip
              pressed={selectedGenreId === genre.id && !debouncedSearch}
              onclick={() => selectGenre(genre.id)}>{genre.name}</FilterChip
            >
          {/each}
        {/snippet}
      </PageToolbarV2>

      {#if playlistsError}
        <div class="flex flex-col items-center justify-center px-6 py-12 text-center">
          <AlertCircle class="text-destructive-text mb-3 size-10" aria-hidden="true" />
          <p class="text-body text-muted-foreground md:text-sm">{playlistsError}</p>
          <Button
            variant="outline"
            class="mt-4 h-11 rounded-full px-5 md:h-8 md:rounded-lg md:px-3"
            onclick={() => loadPlaylists()}>Retry</Button
          >
        </div>
      {:else if loadingPlaylists}
        <PlaylistGridSkeleton />
      {:else if playlists.length === 0}
        <div class="flex flex-col items-center justify-center py-12 text-center">
          <Compass class="text-muted-foreground mb-3 size-10" aria-hidden="true" />
          <p class="text-body text-muted-foreground md:text-sm">
            {debouncedSearch ? 'No playlists match your search' : 'No playlists found'}
          </p>
        </div>
      {:else}
        <div
          class="grid grid-cols-2 gap-x-4 gap-y-5 px-4 pt-2 pb-4 sm:grid-cols-3 md:grid-cols-4 md:px-7 md:pt-4 lg:grid-cols-5"
        >
          {#each playlists as playlist (playlist.id)}
            <DiscoverPlaylistCard
              {playlist}
              href={playlistHref(playlist.id)}
              onOpen={rememberGridScroll}
              onQuickSubscribe={() => subscribe(playlist)}
              isBusy={busyKeys.has(playlist.id)}
            />
          {/each}
        </div>
      {/if}
    </ScrollArea>
  </div>
{/if}

<!-- Add a playlist from a link: the phone's path to it (it used to be a toolbar field that only
     fitted from 640px), and the desktop's too. Fields stay at the top of the sheet: the iOS
     keyboard is not dodged. -->
<BottomSheet.Root
  bind:open={linkOpen}
  title="Add playlist"
  description="Paste a Spotify, Deezer or YouTube playlist link to subscribe to it."
  onOpenChange={(open) => {
    if (!open) clearLink();
  }}
>
  {#snippet leading()}
    <BottomSheet.Action onclick={() => (linkOpen = false)}
      >{resolveResult?.subscribed ? 'Done' : 'Cancel'}</BottomSheet.Action
    >
  {/snippet}
  {#snippet trailing()}
    {#if resolveResult && !resolveResult.subscribed}
      <BottomSheet.Action prominent disabled={subscribingLink} onclick={onSubscribeLink}>
        {#if subscribingLink}<Loader2 class="size-4 animate-spin" />{/if}
        Subscribe
      </BottomSheet.Action>
    {:else if !resolveResult}
      <BottomSheet.Action prominent disabled={resolving || !linkUrl.trim()} onclick={onResolve}>
        {#if resolving}<Loader2 class="size-4 animate-spin" />{/if}
        Find
      </BottomSheet.Action>
    {/if}
  {/snippet}

  <div class="flex flex-col gap-6 px-4 pt-1">
    <Input
      type="url"
      bind:value={linkUrl}
      aria-label="Playlist link"
      placeholder="https://open.spotify.com/playlist/…"
      inputmode="url"
      enterkeyhint="go"
      autocapitalize="off"
      autocorrect="off"
      spellcheck={false}
      oninput={() => {
        resolveResult = null;
        resolveError = null;
      }}
      onkeydown={(e) => {
        if (e.key === 'Enter') void onResolve();
      }}
    />

    {#if resolveError}
      <div
        role="alert"
        class="bg-destructive/10 text-destructive-text text-subheadline flex items-start gap-2 rounded-xl px-4 py-3 md:text-sm"
      >
        <AlertCircle class="mt-0.5 size-4 shrink-0" aria-hidden="true" />
        <div class="min-w-0 flex-1">
          {#if resolveError.kind === 'editorial'}
            <p class="font-medium">This is a Spotify editorial playlist.</p>
            <p class="mt-0.5">{resolveError.message}</p>
            <p class="mt-1">
              Tip: search for it in Discover — the same editorial playlists are available via
              Deezer, which does allow subscribing.
            </p>
          {:else}
            {resolveError.message}
          {/if}
        </div>
      </div>
    {/if}
  </div>

  {#if resolveResult}
    {@const r = resolveResult}
    <GroupedList.Section
      class="mt-6"
      footer={downloadsEnabled
        ? r.provider === 'youtube'
          ? 'New videos added to it download automatically — the song and its music video.'
          : 'New tracks added to it download automatically.'
        : undefined}
    >
      <div class="flex items-center gap-3 px-4 py-3">
        <div class="bg-muted size-14 shrink-0 overflow-hidden rounded-sm">
          {#if r.coverUrl}
            <img src={r.coverUrl} alt="" class="size-full object-cover" crossorigin="anonymous" />
          {:else}
            <div class="flex size-full items-center justify-center">
              <ListMusic class="text-muted-foreground size-5" aria-hidden="true" />
            </div>
          {/if}
        </div>
        <div class="min-w-0 flex-1">
          <div class="text-headline truncate md:text-sm">{r.title}</div>
          <div class="text-subheadline text-muted-foreground md:text-xs">
            {playlistProviderLabel(r.provider)} · {r.trackCount.toLocaleString()}
            {r.provider === 'youtube' ? 'video' : 'track'}{r.trackCount === 1 ? '' : 's'}
          </div>
        </div>
        {#if r.subscribed}
          <span class="text-primary text-subheadline flex shrink-0 items-center gap-1.5 md:text-xs">
            <CircleCheck class="size-5 md:size-4" aria-hidden="true" /> Subscribed
          </span>
        {/if}
      </div>
    </GroupedList.Section>
  {/if}
</BottomSheet.Root>

<AlertDialog.Root bind:open={confirmUnsubscribe}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title
        >Unsubscribe from “{detail?.playlist.title ?? 'this playlist'}”?</AlertDialog.Title
      >
      <AlertDialog.Description>
        New tracks stop syncing into your wishlist and its sync history is dropped — subscribing
        again starts over. Tracks it already added stay on the wishlist and in your library.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action
        variant="destructive"
        onclick={() => {
          confirmUnsubscribe = false;
          if (detail) void unsubscribe(detail.playlist);
        }}
      >
        Unsubscribe
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

<style>
  /* The bar's inline title fades in once the hero title has gone under it (Apple Music). */
  .hero-scroller :global([data-mh-navbar] h1) {
    transition: opacity 150ms cubic-bezier(0.23, 1, 0.32, 1);
  }
  .hero-scroller[data-hero-visible] :global([data-mh-navbar] h1) {
    opacity: 0;
  }
</style>

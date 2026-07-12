<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Badge } from '$lib/components/ui/badge';
  import { Switch } from '$lib/components/ui/switch';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import {
    Compass,
    Search,
    Link2,
    Loader2,
    ArrowLeft,
    ListMusic,
    AlertCircle,
    Plus,
    X,
    CheckCircle2,
    Music,
    Download,
    Sparkles
  } from '@lucide/svelte';
  import {
    fetchDiscoverGenres,
    fetchDiscoverPlaylists,
    fetchDiscoverPlaylist,
    resolveDiscoverUrl,
    addWishlistSource,
    removeWishlistSource,
    setWishlistSourceAutoSync,
    fetchSettings,
    ApiError,
    type DiscoverGenre,
    type DiscoverPlaylistSummary,
    type DiscoverPlaylistDetail,
    type DiscoverResolveResult
  } from '$lib/api-client';
  import { albumTint } from '$lib/album-tint';
  import { computeInitials } from '$lib/formatters';
  import DiscoverPlaylistCard from '$lib/components/discover/DiscoverPlaylistCard.svelte';
  import PlaylistGridSkeleton from '$lib/components/spotify/PlaylistGridSkeleton.svelte';
  import TrackListSkeleton from '$lib/components/spotify/TrackListSkeleton.svelte';

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
  let selectedId = $state<string | null>(null);
  let detail = $state<DiscoverPlaylistDetail | null>(null);
  let loadingDetail = $state(false);
  let detailError = $state<string | null>(null);

  // ── Add-by-link state ─────────────────────────────────────────────────────
  let linkUrl = $state('');
  let resolving = $state(false);
  let resolveResult = $state<DiscoverResolveResult | null>(null);
  let resolveError = $state<{ kind: 'editorial' | 'generic'; message: string } | null>(null);
  let subscribingLink = $state(false);

  // ── Shared feedback ───────────────────────────────────────────────────────
  let banner = $state<{ type: 'success' | 'error'; message: string } | null>(null);
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

  function openPlaylist(p: DiscoverPlaylistSummary) {
    selectedId = p.id;
    // Seed the header instantly from the grid summary; loadDetail replaces it with tracks.
    detail = { playlist: p, tracks: [] };
    detailError = null;
    void loadDetail(p.id);
  }

  function closeDetail() {
    selectedId = null;
    detail = null;
    detailError = null;
  }

  // ── Subscribe actions (grid + detail) ──────────────────────────────────────
  function subscribedMessage(title: string): string {
    return `Subscribed to “${title}” — new tracks will be synced${
      downloadsEnabled ? ' and downloaded' : ''
    } automatically.`;
  }

  async function subscribe(p: DiscoverPlaylistSummary) {
    setBusy(p.id, true);
    banner = null;
    try {
      const res = await addWishlistSource('DeezerPlaylist', {
        deezerPlaylistId: p.id,
        autoSync: true
      });
      syncSubState(p.id, { subscribed: true, sourceId: res.sourceId, autoSync: true });
      banner = { type: 'success', message: subscribedMessage(p.title) };
      // The wishlist source snapshot runs in the background; refresh once so track
      // in-library / in-wishlist badges catch up.
      setTimeout(() => {
        if (selectedId === p.id) void loadDetail(p.id, true);
        else void loadPlaylists(true);
      }, 4000);
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Failed to subscribe' };
    } finally {
      setBusy(p.id, false);
    }
  }

  async function unsubscribe(p: DiscoverPlaylistSummary) {
    if (p.sourceId == null) return;
    setBusy(p.id, true);
    banner = null;
    try {
      await removeWishlistSource(p.sourceId);
      syncSubState(p.id, { subscribed: false, sourceId: null, autoSync: null });
    } catch (err) {
      banner = {
        type: 'error',
        message: err instanceof Error ? err.message : 'Failed to unsubscribe'
      };
    } finally {
      setBusy(p.id, false);
    }
  }

  async function toggleAutoSync(p: DiscoverPlaylistSummary) {
    if (p.sourceId == null) return;
    setBusy(p.id, true);
    banner = null;
    try {
      const res = await setWishlistSourceAutoSync(p.sourceId, !(p.autoSync ?? false));
      syncSubState(p.id, { autoSync: res.autoSync });
    } catch (err) {
      banner = {
        type: 'error',
        message: err instanceof Error ? err.message : 'Failed to update auto-sync'
      };
    } finally {
      setBusy(p.id, false);
    }
  }

  // ── Add by link ────────────────────────────────────────────────────────────
  async function onResolve() {
    const url = linkUrl.trim();
    if (!url) return;
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
    banner = null;
    try {
      const res =
        r.provider === 'deezer'
          ? await addWishlistSource('DeezerPlaylist', {
              deezerPlaylistId: r.playlistId,
              autoSync: true
            })
          : await addWishlistSource('Playlist', { playlistId: r.playlistId, autoSync: true });
      resolveResult = { ...r, subscribed: true };
      // Reflect on the grid/detail if this playlist is also visible there.
      syncSubState(r.playlistId, { subscribed: true, sourceId: res.sourceId, autoSync: true });
      banner = { type: 'success', message: subscribedMessage(r.title) };
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Failed to subscribe' };
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

  // Detail-view hero tint (matches the Spotify playlist detail idiom).
  const tint = $derived(
    detail ? albumTint(detail.playlist.creatorName ?? 'Deezer', detail.playlist.title) : null
  );
  const heroBackground = $derived(
    tint
      ? `linear-gradient(180deg, ${tint.from} 0%, color-mix(in oklch, ${tint.from} 60%, transparent) 60%, transparent 100%),` +
          ` linear-gradient(135deg, color-mix(in oklch, ${tint.to} 40%, transparent), transparent)`
      : ''
  );
  const detailBusy = $derived(detail ? busyKeys.has(detail.playlist.id) : false);
</script>

{#if selectedId && detail}
  {@const p = detail.playlist}
  <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <ScrollArea class="min-h-0 flex-1">
      <button
        type="button"
        onclick={closeDetail}
        class="absolute top-3 left-3 z-10 inline-flex items-center gap-1 rounded-full bg-black/30 px-2.5 py-1 text-xs text-white/85 backdrop-blur transition-colors hover:bg-black/40 hover:text-white sm:left-6"
      >
        <ArrowLeft class="size-3.5" />
        Back
      </button>

      <!-- Hero -->
      <div class="relative px-6 pt-6 pb-5 text-white sm:px-9" style="background: {heroBackground};">
        <div class="relative z-10 flex items-center gap-4 sm:items-end sm:gap-6">
          <div
            class="relative grid size-20 shrink-0 place-items-center overflow-hidden shadow-[0_24px_48px_rgba(0,0,0,0.35)] sm:size-28 lg:size-32"
            style="border-radius: 6px; background: linear-gradient(135deg, {tint?.from} 0%, {tint?.to} 100%);"
          >
            <div class="mh-cover-grain pointer-events-none absolute inset-0"></div>
            {#if p.coverUrl}
              <img
                src={p.coverUrl}
                alt=""
                loading="lazy"
                crossorigin="anonymous"
                class="absolute inset-0 size-full object-cover"
              />
            {:else}
              <div
                class="relative z-[2] text-2xl font-bold tracking-[-0.04em] text-white/95 [text-shadow:_0_1px_2px_rgba(0,0,0,0.2)] sm:text-3xl lg:text-4xl"
              >
                {computeInitials(p.title)}
              </div>
            {/if}
          </div>

          <div class="min-w-0 flex-1 pb-2">
            <div class="text-[11px] font-semibold tracking-wider opacity-85 uppercase">Playlist</div>
            <h1
              class="mt-2 text-[clamp(24px,5vw,44px)] leading-[0.95] font-extrabold tracking-[-0.03em] [text-wrap:balance]"
            >
              {p.title}
            </h1>
            {#if p.description}
              <p class="mt-2 max-w-2xl text-sm leading-snug text-white/80 [text-wrap:pretty]">
                {p.description}
              </p>
            {/if}
            <div class="mt-3 flex flex-wrap items-center gap-x-2.5 gap-y-2 text-[13px] opacity-90">
              {#if p.creatorName}
                <span class="inline-flex items-center gap-2 font-semibold">
                  <span
                    class="ring-2 ring-white/50 inline-block size-4 rounded-full"
                    style="background: {tint?.to};"
                  ></span>
                  <span>{p.creatorName}</span>
                </span>
                <span class="opacity-50">·</span>
              {/if}
              <span>{p.trackCount.toLocaleString()} song{p.trackCount === 1 ? '' : 's'}</span>
            </div>
          </div>
        </div>
      </div>

      <!-- Action bar -->
      <div
        class="border-border flex flex-wrap items-center gap-3 border-b bg-gradient-to-b from-black/5 to-transparent px-6 py-5 sm:px-9 dark:from-white/5"
      >
        {#if p.subscribed}
          <Button variant="outline" disabled={detailBusy} onclick={() => unsubscribe(p)}>
            {#if detailBusy}
              <Loader2 class="size-4 animate-spin" />
            {:else}
              <X class="size-4" />
            {/if}
            Unsubscribe
          </Button>
          <label class="text-muted-foreground flex cursor-pointer items-center gap-1.5 text-xs">
            <Switch
              size="sm"
              checked={p.autoSync ?? false}
              disabled={detailBusy}
              onCheckedChange={() => toggleAutoSync(p)}
              aria-label="Auto-sync new tracks"
            />
            Auto-sync new tracks
          </label>
        {:else}
          <Button disabled={detailBusy} onclick={() => subscribe(p)}>
            {#if detailBusy}
              <Loader2 class="size-4 animate-spin" />
            {:else}
              <Plus class="size-4" />
            {/if}
            Subscribe
          </Button>
        {/if}

        <div class="text-muted-foreground ml-auto flex items-center gap-3 text-xs">
          <span class="bg-primary/15 text-primary rounded px-2.5 py-1 font-mono">DISCOVER</span>
        </div>
      </div>

      {#if downloadsEnabled}
        <div
          class="border-border bg-muted/40 text-muted-foreground flex items-start gap-2 border-b px-6 py-3 text-xs sm:px-9"
        >
          <Download class="mt-0.5 size-3.5 shrink-0" />
          <span>
            New tracks added to a subscribed playlist are downloaded into your library automatically.
          </span>
        </div>
      {/if}

      {#if banner}
        <div
          class="mx-6 mt-3 rounded-md border px-3 py-2 text-sm sm:mx-9 {banner.type === 'success'
            ? 'border-primary/30 bg-primary/10 text-primary'
            : 'border-destructive/30 bg-destructive/10 text-destructive'}"
        >
          {banner.message}
        </div>
      {/if}

      <!-- Tracks -->
      {#if detailError}
        <div class="flex flex-col items-center justify-center py-12 text-center">
          <AlertCircle class="text-destructive mb-3 size-10" />
          <p class="text-muted-foreground">{detailError}</p>
          <Button variant="outline" size="sm" class="mt-4" onclick={() => loadDetail(p.id)}>
            Retry
          </Button>
        </div>
      {:else if loadingDetail}
        <TrackListSkeleton />
      {:else}
        <div class="flex flex-col gap-1 p-2 md:px-4">
          {#each detail.tracks as track (track.deezerTrackId)}
            <div class="hover:bg-secondary/40 flex items-center gap-3 rounded-md px-2 py-2 transition-colors">
              <div class="bg-secondary size-10 shrink-0 overflow-hidden rounded">
                {#if track.coverUrl}
                  <img src={track.coverUrl} alt="" class="size-full object-cover" crossorigin="anonymous" />
                {:else}
                  <div class="flex size-full items-center justify-center">
                    <Music class="text-muted-foreground size-4" />
                  </div>
                {/if}
              </div>
              <div class="min-w-0 flex-1">
                <div class="truncate text-sm font-medium">{track.title}</div>
                <div class="text-muted-foreground truncate text-xs">
                  {track.artist}{track.album ? ` · ${track.album}` : ''}
                </div>
              </div>
              {#if track.inLibrary}
                <Badge class="border-primary/40 bg-primary/15 text-primary hidden shrink-0 gap-1 sm:inline-flex">
                  <CheckCircle2 class="size-3" />
                  In library
                </Badge>
              {:else if track.inWishlist}
                <Badge variant="outline" class="hidden shrink-0 gap-1 sm:inline-flex">
                  <Sparkles class="size-3" />
                  Wishlisted
                </Badge>
              {/if}
              <span class="text-muted-foreground hidden w-12 shrink-0 text-right text-xs sm:inline">
                {fmtDuration(track.durationMs)}
              </span>
            </div>
          {/each}
          {#if detail.tracks.length === 0}
            <div class="flex flex-col items-center justify-center py-12 text-center">
              <ListMusic class="text-muted-foreground mb-3 size-10" />
              <p class="text-muted-foreground">No tracks found</p>
            </div>
          {/if}
        </div>
      {/if}
    </ScrollArea>
  </div>
{:else}
  <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
    <!-- Header -->
    <div class="border-border bg-card/30 border-b px-4 py-5 md:px-6">
      <div class="flex items-center gap-2">
        <Compass class="size-5" />
        <h1 class="text-2xl font-semibold tracking-tight">Discover</h1>
      </div>
      <p class="text-muted-foreground mt-1 text-sm">
        Browse editorial and chart playlists, then subscribe to auto-download new tracks into your
        library. Or paste a Spotify / Deezer playlist link to add one directly.
      </p>

      <!-- Add by link -->
      <div class="mt-4 flex flex-col gap-2">
        <div class="flex flex-col gap-2 sm:flex-row">
          <div class="relative max-w-xl flex-1">
            <Link2 class="text-muted-foreground absolute top-1/2 left-3 size-4 -translate-y-1/2" />
            <Input
              placeholder="Paste a Spotify or Deezer playlist link…"
              bind:value={linkUrl}
              onkeydown={(e) => {
                if (e.key === 'Enter') onResolve();
              }}
              class="bg-secondary border-0 pl-9"
            />
          </div>
          <Button variant="outline" onclick={onResolve} disabled={resolving || !linkUrl.trim()}>
            {#if resolving}
              <Loader2 class="size-4 animate-spin" />
            {:else}
              <Link2 class="size-4" />
            {/if}
            Add link
          </Button>
        </div>

        {#if resolveError}
          <div
            class="border-destructive/30 bg-destructive/10 text-destructive flex items-start gap-2 rounded-md border px-3 py-2 text-sm"
          >
            <AlertCircle class="mt-0.5 size-4 shrink-0" />
            <div class="flex-1">
              {#if resolveError.kind === 'editorial'}
                <p class="font-medium">This is a Spotify editorial playlist.</p>
                <p class="mt-0.5">{resolveError.message}</p>
                <p class="mt-1 text-xs opacity-90">
                  Tip: search for it below — the same editorial playlists are available via Deezer,
                  which does allow subscribing.
                </p>
              {:else}
                {resolveError.message}
              {/if}
            </div>
            <button
              type="button"
              onclick={() => (resolveError = null)}
              class="shrink-0 text-xs underline opacity-80 hover:opacity-100"
            >
              Dismiss
            </button>
          </div>
        {/if}

        {#if resolveResult}
          {@const r = resolveResult}
          <div class="border-border bg-card flex items-center gap-3 rounded-lg border p-3">
            <div class="bg-secondary size-12 shrink-0 overflow-hidden rounded">
              {#if r.coverUrl}
                <img src={r.coverUrl} alt="" class="size-full object-cover" crossorigin="anonymous" />
              {:else}
                <div class="flex size-full items-center justify-center">
                  <ListMusic class="text-muted-foreground size-5" />
                </div>
              {/if}
            </div>
            <div class="min-w-0 flex-1">
              <div class="flex items-center gap-2">
                <span class="truncate text-sm font-medium">{r.title}</span>
                <Badge variant="outline" class="shrink-0 text-[10px]">
                  {r.provider === 'deezer' ? 'Deezer' : 'Spotify'}
                </Badge>
              </div>
              <div class="text-muted-foreground text-xs">
                {r.trackCount.toLocaleString()} track{r.trackCount === 1 ? '' : 's'}{downloadsEnabled
                  ? ' · new tracks auto-download'
                  : ''}
              </div>
            </div>
            {#if r.subscribed}
              <Badge class="border-primary/40 bg-primary/15 text-primary shrink-0 gap-1">
                <CheckCircle2 class="size-3" />
                Subscribed
              </Badge>
            {:else}
              <Button size="sm" class="shrink-0" disabled={subscribingLink} onclick={onSubscribeLink}>
                {#if subscribingLink}
                  <Loader2 class="size-4 animate-spin" />
                {:else}
                  <Plus class="size-4" />
                {/if}
                Subscribe
              </Button>
            {/if}
            <button
              type="button"
              aria-label="Dismiss"
              onclick={clearLink}
              class="text-muted-foreground hover:text-foreground shrink-0"
            >
              <X class="size-4" />
            </button>
          </div>
        {/if}
      </div>
    </div>

    <!-- Search -->
    <div class="border-border flex items-center gap-3 border-b px-4 py-3 md:px-6">
      <div class="relative max-w-md flex-1">
        <Search class="text-muted-foreground absolute top-1/2 left-3 size-4 -translate-y-1/2" />
        <Input
          placeholder="Search playlists…"
          bind:value={searchQuery}
          class="bg-secondary border-0 pl-9"
        />
      </div>
    </div>

    <!-- Genre chips -->
    <div class="border-border border-b px-4 py-3 md:px-6">
      <div class="flex gap-2 overflow-x-auto pb-1">
        <button
          type="button"
          onclick={() => selectGenre(null)}
          class="shrink-0 rounded-full px-3 py-1 text-xs transition-colors {selectedGenreId === null &&
          !debouncedSearch
            ? 'bg-primary text-primary-foreground'
            : 'bg-secondary text-muted-foreground hover:bg-secondary/70'}"
        >
          Top
        </button>
        {#each genres as genre (genre.id)}
          <button
            type="button"
            onclick={() => selectGenre(genre.id)}
            class="shrink-0 rounded-full px-3 py-1 text-xs whitespace-nowrap transition-colors {selectedGenreId ===
              genre.id && !debouncedSearch
              ? 'bg-primary text-primary-foreground'
              : 'bg-secondary text-muted-foreground hover:bg-secondary/70'}"
          >
            {genre.name}
          </button>
        {/each}
      </div>
    </div>

    {#if banner}
      <div
        class="mx-4 mt-3 rounded-md border px-3 py-2 text-sm md:mx-6 {banner.type === 'success'
          ? 'border-primary/30 bg-primary/10 text-primary'
          : 'border-destructive/30 bg-destructive/10 text-destructive'}"
      >
        {banner.message}
      </div>
    {/if}

    <!-- Grid -->
    {#if playlistsError}
      <div class="flex flex-col items-center justify-center py-12 text-center">
        <AlertCircle class="text-destructive mb-3 size-10" />
        <p class="text-muted-foreground">{playlistsError}</p>
        <Button variant="outline" size="sm" class="mt-4" onclick={() => loadPlaylists()}>Retry</Button>
      </div>
    {:else if loadingPlaylists}
      <PlaylistGridSkeleton />
    {:else}
      <ScrollArea class="min-h-0 flex-1">
        <div class="grid grid-cols-2 gap-4 p-4 sm:grid-cols-3 md:grid-cols-4 md:p-6 lg:grid-cols-5">
          {#each playlists as playlist (playlist.id)}
            <DiscoverPlaylistCard
              {playlist}
              onClick={() => openPlaylist(playlist)}
              onQuickSubscribe={() => subscribe(playlist)}
              isBusy={busyKeys.has(playlist.id)}
            />
          {/each}
        </div>
        {#if playlists.length === 0}
          <div class="flex flex-col items-center justify-center py-12 text-center">
            <Compass class="text-muted-foreground mb-3 size-10" />
            <p class="text-muted-foreground">
              {debouncedSearch ? 'No playlists match your search' : 'No playlists found'}
            </p>
          </div>
        {/if}
      </ScrollArea>
    {/if}
  </div>
{/if}

<style>
  .mh-cover-grain {
    background:
      radial-gradient(circle at 30% 20%, rgba(255, 255, 255, 0.25), transparent 50%),
      radial-gradient(circle at 70% 80%, rgba(0, 0, 0, 0.2), transparent 50%);
  }
</style>

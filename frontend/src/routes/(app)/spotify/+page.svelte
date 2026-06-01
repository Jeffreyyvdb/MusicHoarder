<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Badge } from '$lib/components/ui/badge';
  import * as Tabs from '$lib/components/ui/tabs';
  import { Input } from '$lib/components/ui/input';
  import {
    fetchSpotifyStatus,
    fetchSpotifyConnectUrl,
    disconnectSpotify,
    fetchSpotifyLikedSongs,
    fetchSpotifyPlaylists,
    fetchSpotifyCredentials,
    type SpotifyStatusResponse,
    type SpotifyApiTrack,
    type SpotifyApiPlaylist,
    type SpotifyCredentialsResponse
  } from '$lib/api-client';
  import {
    Music,
    Search,
    Heart,
    ListMusic,
    Clock,
    AlertCircle,
    CheckCircle2,
    LogOut,
    ExternalLink,
    Loader2,
    Settings,
    KeyRound,
    Play
  } from '@lucide/svelte';
  import SpotifyTrackRow from '$lib/components/spotify/SpotifyTrackRow.svelte';
  import PlaylistCard from '$lib/components/spotify/PlaylistCard.svelte';
  import PaginationControls from '$lib/components/spotify/PaginationControls.svelte';
  import TrackListSkeleton from '$lib/components/spotify/TrackListSkeleton.svelte';
  import PlaylistGridSkeleton from '$lib/components/spotify/PlaylistGridSkeleton.svelte';
  import PlaylistDetailView from '$lib/components/spotify/PlaylistDetailView.svelte';
  import PipelineSubNavV2 from '$lib/components/v2/PipelineSubNavV2.svelte';
  import { LIBRARY_SUBNAV } from '$lib/library-subnav';
  import { uiVersion } from '$lib/stores/ui-version.svelte';

  let status = $state<SpotifyStatusResponse | null>(null);
  let credentials = $state<SpotifyCredentialsResponse | null>(null);
  let isLoadingStatus = $state(true);
  let isConnecting = $state(false);
  let isDisconnecting = $state(false);
  let error = $state<string | null>(null);
  let oauthBanner = $state<{ type: 'success' | 'error'; message: string } | null>(null);

  let likedSongs = $state<SpotifyApiTrack[]>([]);
  let likedTotal = $state(0);
  let likedOffset = $state(0);
  let isLoadingLiked = $state(false);
  let likedError = $state<string | null>(null);
  let likedSearchQuery = $state('');

  let playlists = $state<SpotifyApiPlaylist[]>([]);
  let isLoadingPlaylists = $state(false);
  let playlistsError = $state<string | null>(null);
  let selectedPlaylist = $state<SpotifyApiPlaylist | null>(null);
  let playlistSearchQuery = $state('');

  let expandedLikedMatchIds = $state(new Set<string>());

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
    void goto('/spotify', { replaceState: true, noScroll: true });
    void loadStatus();
  });

  async function loadLikedSongs(offset: number) {
    isLoadingLiked = true;
    likedError = null;
    try {
      const result = await fetchSpotifyLikedSongs(offset, likedLimit);
      likedSongs = result.items;
      likedTotal = result.total;
      likedOffset = result.offset;
    } catch (err) {
      likedError = err instanceof Error ? err.message : 'Failed to load liked songs';
    } finally {
      isLoadingLiked = false;
    }
  }

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
    }
  }

  // Load data when Spotify connects.
  $effect(() => {
    if (status?.connected) {
      void loadLikedSongs(0);
      void loadPlaylists();
    }
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
      selectedPlaylist = null;
      expandedLikedMatchIds = new Set();
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
      ? playlists.filter((p) =>
          p.name.toLowerCase().includes(playlistSearchQuery.toLowerCase())
        )
      : playlists
  );

  const hasCredentials = $derived(
    credentials?.hasClientSecret && credentials?.clientId
  );
</script>

<!-- In v2 the Spotify page belongs to the Library section, so it wears the same
     sub-nav bar as Albums/Artists/Tracks (count-less here — the page doesn't load
     the song set). It sits as a shrink-0 sibling above the flex-1 content; v1
     hides it and renders exactly as before. -->
{#if uiVersion.isV2}
  <PipelineSubNavV2 tabs={[...LIBRARY_SUBNAV]} active="spotify" />
{/if}

{#if isLoadingStatus}
  <div class="flex flex-1 items-center justify-center">
    <Loader2 class="text-muted-foreground size-8 animate-spin" />
  </div>
{:else if !status?.connected}
  <div class="flex flex-1 items-center justify-center p-6">
    <div class="max-w-md text-center">
      <div
        class="mx-auto mb-6 flex size-20 items-center justify-center rounded-full bg-[#1DB954]/10"
      >
        <Music class="size-10 text-[#1DB954]" />
      </div>
      <h1 class="mb-2 text-2xl font-bold">Connect Spotify</h1>
      <p class="text-muted-foreground mb-8">
        Link your Spotify account to browse your playlists and liked songs.
      </p>

      {#if oauthBanner}
        <div
          class="mb-6 rounded-lg border px-4 py-3 text-left text-sm {oauthBanner.type === 'success'
            ? 'border-primary/40 bg-primary/10 text-primary'
            : 'border-destructive/50 bg-destructive/10 text-destructive'}"
        >
          <div class="flex items-start gap-2">
            {#if oauthBanner.type === 'success'}
              <CheckCircle2 class="mt-0.5 size-4 shrink-0" />
            {:else}
              <AlertCircle class="mt-0.5 size-4 shrink-0" />
            {/if}
            <p class="flex-1">{oauthBanner.message}</p>
            <button
              type="button"
              onclick={() => (oauthBanner = null)}
              class="shrink-0 text-xs underline opacity-80 hover:opacity-100"
            >
              Dismiss
            </button>
          </div>
        </div>
      {/if}

      {#if error}
        <div
          class="border-destructive/50 bg-destructive/10 text-destructive mb-6 rounded-lg border px-4 py-3 text-sm"
        >
          {error}
        </div>
      {/if}

      {#if !hasCredentials}
        <div class="space-y-4">
          <div class="border-border bg-card rounded-lg border p-4 text-left">
            <div class="mb-2 flex items-center gap-2">
              <KeyRound class="text-muted-foreground size-4" />
              <span class="text-sm font-medium">Spotify API credentials required</span>
            </div>
            <p class="text-muted-foreground mb-3 text-xs">
              You need to configure your Spotify Client ID and Client Secret before connecting.
            </p>
            <Button variant="outline" size="sm" class="w-full" href="/settings">
              <Settings class="mr-2 size-4" />
              Go to Settings
            </Button>
          </div>
        </div>
      {:else}
        <Button
          size="lg"
          class="bg-[#1DB954] px-8 text-white hover:bg-[#1DB954]/90"
          onclick={handleConnect}
          disabled={isConnecting}
        >
          {#if isConnecting}
            <Loader2 class="mr-2 size-5 animate-spin" />
          {:else}
            <ExternalLink class="mr-2 size-5" />
          {/if}
          Connect with Spotify
        </Button>
      {/if}
    </div>
  </div>
{:else if selectedPlaylist}
  <PlaylistDetailView
    playlist={selectedPlaylist}
    onBack={() => (selectedPlaylist = null)}
  />
{:else}
  <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
    {#if oauthBanner}
      <div
        class="mx-4 mt-4 rounded-lg border px-4 py-3 text-sm md:mx-6 {oauthBanner.type ===
        'success'
          ? 'border-emerald-500/40 bg-emerald-500/10 text-emerald-700 dark:text-emerald-400'
          : 'border-destructive/50 bg-destructive/10 text-destructive'}"
      >
        <div class="flex items-start gap-2">
          {#if oauthBanner.type === 'success'}
            <CheckCircle2 class="mt-0.5 size-4 shrink-0" />
          {:else}
            <AlertCircle class="mt-0.5 size-4 shrink-0" />
          {/if}
          <p class="flex-1">{oauthBanner.message}</p>
          <button
            type="button"
            onclick={() => (oauthBanner = null)}
            class="shrink-0 text-xs underline opacity-80 hover:opacity-100"
          >
            Dismiss
          </button>
        </div>
      </div>
    {/if}

    <div class="border-border bg-card/30 border-b px-4 py-5 md:px-6">
      <div class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
        <div>
          <div class="flex items-center gap-2">
            <h1 class="text-2xl font-bold">Spotify</h1>
            <Badge class="border-0 bg-[#1DB954]/20 text-[#1DB954]">Connected</Badge>
          </div>
          {#if status.connectedAt}
            <p class="text-muted-foreground mt-1 text-sm">
              Connected since {new Date(status.connectedAt).toLocaleDateString()}
            </p>
          {/if}
        </div>
        <Button variant="outline" onclick={handleDisconnect} disabled={isDisconnecting}>
          {#if isDisconnecting}
            <Loader2 class="mr-2 size-4 animate-spin" />
          {:else}
            <LogOut class="mr-2 size-4" />
          {/if}
          Disconnect
        </Button>
      </div>
    </div>

    <Tabs.Root value="liked" class="flex min-h-0 flex-1 flex-col overflow-hidden">
      <div class="border-border border-b px-4 md:px-6">
        <Tabs.List class="h-12 bg-transparent p-0">
          <Tabs.Trigger
            value="liked"
            class="data-[state=active]:border-b-primary/50 h-12 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:bg-transparent data-[state=active]:shadow-none"
          >
            <Heart class="mr-2 size-4" />
            Liked Songs
          </Tabs.Trigger>
          <Tabs.Trigger
            value="playlists"
            class="data-[state=active]:border-b-primary/50 h-12 rounded-none border-0 border-b-2 border-transparent px-4 data-[state=active]:bg-transparent data-[state=active]:shadow-none"
          >
            <ListMusic class="mr-2 size-4" />
            Playlists
          </Tabs.Trigger>
        </Tabs.List>
      </div>

      <Tabs.Content value="liked" class="m-0 flex min-h-0 flex-1 flex-col overflow-hidden">
        <ScrollArea class="min-h-0 flex-1">
          <!-- Spotify-style hero — Liked Songs as a virtual playlist -->
          <div
            class="relative px-6 pt-10 pb-7 text-white sm:px-9"
            style="background: linear-gradient(180deg, oklch(0.35 0.16 320) 0%, color-mix(in oklch, oklch(0.35 0.16 320) 60%, transparent) 60%, transparent 100%), linear-gradient(135deg, color-mix(in oklch, oklch(0.62 0.16 5) 40%, transparent), transparent);"
          >
            <div class="relative z-10 flex flex-col items-end gap-6 sm:flex-row sm:gap-8">
              <div
                class="relative grid shrink-0 place-items-center overflow-hidden shadow-[0_24px_48px_rgba(0,0,0,0.35)]"
                style="width: 232px; height: 232px; border-radius: 6px; background: linear-gradient(135deg, oklch(0.45 0.18 320) 0%, oklch(0.62 0.16 5) 100%);"
              >
                <div class="mh-cover-grain pointer-events-none absolute inset-0"></div>
                <Heart
                  class="relative z-[2] size-24 fill-white text-white drop-shadow-[0_2px_8px_rgba(0,0,0,0.3)]"
                />
              </div>

              <div class="min-w-0 flex-1 pb-2">
                <div class="text-[11px] font-semibold tracking-wider opacity-85 uppercase">
                  Playlist
                </div>
                <h1
                  class="mt-3 text-[clamp(36px,5.5vw,80px)] leading-[0.95] font-extrabold tracking-[-0.03em] [text-wrap:balance]"
                >
                  Liked Songs
                </h1>
                <div class="mt-5 flex flex-wrap items-center gap-x-2.5 gap-y-2 text-[13px] opacity-90">
                  <span class="inline-flex items-center gap-2 font-semibold">
                    <span
                      class="ring-2 ring-white/50 inline-block size-4 rounded-full"
                      style="background: oklch(0.62 0.16 5);"
                    ></span>
                    <span>You</span>
                  </span>
                  <span class="opacity-50">·</span>
                  <span>
                    {likedTotal} song{likedTotal === 1 ? '' : 's'}
                  </span>
                </div>
              </div>
            </div>
          </div>

          <!-- Action bar -->
          <div
            class="border-border flex items-center gap-3 border-b bg-gradient-to-b from-black/5 to-transparent px-6 py-5 sm:px-9 dark:from-white/5"
          >
            <button
              type="button"
              aria-label="Play liked songs"
              disabled
              title="Playback for Spotify is not wired up yet"
              class="bg-[#1DB954] text-white grid place-items-center rounded-full shadow-[0_6px_16px_rgba(29,185,84,0.4)] transition-transform hover:scale-105 disabled:cursor-not-allowed disabled:opacity-60 disabled:hover:scale-100"
              style="width: 52px; height: 52px;"
            >
              <Play class="size-5 fill-current" />
            </button>

            <div class="text-muted-foreground ml-auto flex items-center gap-3 text-xs">
              <span class="bg-[#1DB954]/15 text-[#1DB954] rounded px-2.5 py-1 font-mono">
                SPOTIFY
              </span>
            </div>
          </div>

          <!-- Filter -->
          <div class="border-border flex items-center gap-3 border-b px-4 py-3 md:px-6">
            <div class="relative max-w-md flex-1">
              <Search class="text-muted-foreground absolute top-1/2 left-3 size-4 -translate-y-1/2" />
              <Input
                placeholder="Search liked songs..."
                bind:value={likedSearchQuery}
                class="bg-secondary border-0 pl-9"
              />
            </div>
            <span class="text-muted-foreground shrink-0 text-sm">{likedTotal} songs</span>
          </div>

          {#if likedError}
            <div class="flex flex-col items-center justify-center py-12 text-center">
              <AlertCircle class="text-destructive mb-3 size-10" />
              <p class="text-muted-foreground">{likedError}</p>
              <Button
                variant="outline"
                size="sm"
                class="mt-4"
                onclick={() => loadLikedSongs(likedOffset)}
              >
                Retry
              </Button>
            </div>
          {:else if isLoadingLiked}
            <TrackListSkeleton />
          {:else}
            <div
              class="text-muted-foreground border-border/50 hidden items-center gap-3 border-b px-6 py-2 text-xs md:flex"
            >
              <span class="w-8 text-right">#</span>
              <span class="size-10"></span>
              <span class="flex-1">Title</span>
              <span class="hidden max-w-[200px] md:block">Album</span>
              <span class="hidden w-24 text-right lg:block">Date Added</span>
              <span class="w-12 text-right"><Clock class="inline size-3.5" /></span>
              <span class="w-[120px] shrink-0 text-right">Library</span>
            </div>
            <div class="flex flex-col gap-2 p-2 md:px-4">
              {#each filteredLikedSongs as track, i (`${track.spotifyId}-${i}`)}
                <SpotifyTrackRow
                  {track}
                  index={likedOffset + i}
                  showDateAdded
                  expanded={expandedLikedMatchIds.has(track.spotifyId)}
                  onToggleExpand={() => toggleExpandedLiked(track.spotifyId)}
                />
              {/each}
              {#if filteredLikedSongs.length === 0 && !isLoadingLiked}
                <div class="flex flex-col items-center justify-center py-12 text-center">
                  <Heart class="text-muted-foreground mb-3 size-10" />
                  <p class="text-muted-foreground">
                    {likedSearchQuery ? 'No matching songs found' : 'No liked songs yet'}
                  </p>
                </div>
              {/if}
            </div>
            <PaginationControls
              offset={likedOffset}
              limit={likedLimit}
              total={likedTotal}
              onPageChange={loadLikedSongs}
              isLoading={isLoadingLiked}
            />
          {/if}
        </ScrollArea>
      </Tabs.Content>

      <Tabs.Content value="playlists" class="m-0 flex min-h-0 flex-1 flex-col overflow-hidden">
        <div class="border-border flex items-center gap-3 border-b px-4 py-3 md:px-6">
          <div class="relative max-w-md flex-1">
            <Search class="text-muted-foreground absolute top-1/2 left-3 size-4 -translate-y-1/2" />
            <Input
              placeholder="Search playlists..."
              bind:value={playlistSearchQuery}
              class="bg-secondary border-0 pl-9"
            />
          </div>
          <span class="text-muted-foreground shrink-0 text-sm">{playlists.length} playlists</span>
        </div>

        {#if playlistsError}
          <div class="flex flex-col items-center justify-center py-12 text-center">
            <AlertCircle class="text-destructive mb-3 size-10" />
            <p class="text-muted-foreground">{playlistsError}</p>
            <Button variant="outline" size="sm" class="mt-4" onclick={loadPlaylists}>
              Retry
            </Button>
          </div>
        {:else if isLoadingPlaylists}
          <PlaylistGridSkeleton />
        {:else}
          <ScrollArea class="min-h-0 flex-1">
            <div
              class="grid grid-cols-2 gap-4 p-4 sm:grid-cols-3 md:grid-cols-4 md:p-6 lg:grid-cols-5"
            >
              {#each filteredPlaylists as playlist (playlist.spotifyId)}
                <PlaylistCard {playlist} onClick={() => (selectedPlaylist = playlist)} />
              {/each}
            </div>
            {#if filteredPlaylists.length === 0 && !isLoadingPlaylists}
              <div class="flex flex-col items-center justify-center py-12 text-center">
                <ListMusic class="text-muted-foreground mb-3 size-10" />
                <p class="text-muted-foreground">
                  {playlistSearchQuery ? 'No matching playlists' : 'No playlists found'}
                </p>
              </div>
            {/if}
          </ScrollArea>
        {/if}
      </Tabs.Content>
    </Tabs.Root>
  </div>
{/if}

<style>
  .mh-cover-grain {
    background:
      radial-gradient(circle at 30% 20%, rgba(255, 255, 255, 0.25), transparent 50%),
      radial-gradient(circle at 70% 80%, rgba(0, 0, 0, 0.2), transparent 50%);
  }
</style>

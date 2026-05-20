<script lang="ts">
  import { onMount } from 'svelte';
  import { ChevronRight, Heart, ScanLine, ListPlus, KeyRound, Loader2 } from '@lucide/svelte';
  import MobileHeader from '$lib/components/mobile/MobileHeader.svelte';
  import {
    fetchSpotifyStatus,
    fetchSpotifyConnectUrl,
    fetchSpotifyLikedSongs,
    fetchSpotifyPlaylists,
    fetchSpotifyPlaylistTracks,
    type SpotifyStatusResponse,
    type SpotifyApiPlaylist,
    type SpotifyApiTrack
  } from '$lib/api-client';
  import { albumTint } from '$lib/album-tint';

  let status = $state<SpotifyStatusResponse | null>(null);
  let likedTotal = $state(0);
  let playlists = $state<SpotifyApiPlaylist[]>([]);
  let isLoading = $state(true);

  let openPlaylist = $state<SpotifyApiPlaylist | null>(null);
  let openTracks = $state<SpotifyApiTrack[]>([]);
  let isLoadingTracks = $state(false);

  async function load() {
    isLoading = true;
    try {
      status = await fetchSpotifyStatus().catch(
        () => ({ connected: false, hasCredentials: false, tokenExpired: false }) as SpotifyStatusResponse
      );
      if (status?.connected) {
        const [liked, pls] = await Promise.all([
          fetchSpotifyLikedSongs(0, 1).catch(() => ({ total: 0, offset: 0, limit: 1, items: [] })),
          fetchSpotifyPlaylists().catch(() => ({ items: [] }))
        ]);
        likedTotal = liked.total;
        const seen = new Set<string>();
        playlists = pls.items.filter((p) => (seen.has(p.spotifyId) ? false : (seen.add(p.spotifyId), true)));
      }
    } finally {
      isLoading = false;
    }
  }

  onMount(load);

  async function open(p: SpotifyApiPlaylist) {
    openPlaylist = p;
    isLoadingTracks = true;
    openTracks = [];
    try {
      const res = await fetchSpotifyPlaylistTracks(p.spotifyId, 0, 100);
      openTracks = res.items;
    } catch {
      openTracks = [];
    } finally {
      isLoadingTracks = false;
    }
  }

  async function connect() {
    try {
      const res = await fetchSpotifyConnectUrl();
      window.location.href = res.authorizationUrl;
    } catch {
      // ignore — surfaced on the desktop settings flow
    }
  }

  function dur(ms: number): string {
    const s = Math.round(ms / 1000);
    return `${Math.floor(s / 60)}:${String(s % 60).padStart(2, '0')}`;
  }

  function pillFor(t: SpotifyApiTrack) {
    const m = t.libraryMatch?.matchStatus;
    if (m === 'InLibrary') return { cls: 'ok', label: 'Lib' };
    if (m === 'PossibleMatch') return { cls: 'q', label: 'Maybe' };
    return { cls: 'miss', label: 'Add' };
  }

  const counts = $derived.by(() => {
    let lib = 0,
      maybe = 0,
      miss = 0;
    for (const t of openTracks) {
      const m = t.libraryMatch?.matchStatus;
      if (m === 'InLibrary') lib++;
      else if (m === 'PossibleMatch') maybe++;
      else miss++;
    }
    return { lib, maybe, miss };
  });
</script>

{#snippet logo(size: number)}
  <svg width={size} height={size} viewBox="0 0 24 24" fill="#1db954" aria-hidden="true">
    <circle cx="12" cy="12" r="11" />
    <path
      d="M6 9.5c4-1.2 9-1 13 1M6.8 12.5c3.4-1 7.5-0.7 11 1.1M7.8 15.4c2.6-0.7 5.7-0.5 8.6 1"
      stroke="#000"
      stroke-width="1.6"
      stroke-linecap="round"
      fill="none"
    />
  </svg>
{/snippet}

{#if openPlaylist}
  {@const pl = openPlaylist}
  {@const tint = albumTint(pl.ownerName ?? 'Spotify', pl.name)}
  <div class="mob">
    <MobileHeader back="Spotify" onback={() => (openPlaylist = null)} />
    <div class="mob-scroll">
      <div class="flex items-end gap-3.5 px-4 pt-1 pb-4.5">
        <div
          class="mob-sp-pl-art h-24 w-24 rounded-[10px]"
          style="background: linear-gradient(135deg, {tint.from}, {tint.to});"
        ></div>
        <div class="min-w-0 flex-1">
          <div class="text-muted-foreground text-[10.5px] font-semibold tracking-[0.12em]">PLAYLIST</div>
          <div class="my-1 text-[22px] leading-tight font-bold tracking-[-0.02em]">{pl.name}</div>
          <div class="text-muted-foreground text-[11.5px]">
            by <span class="font-mono">{pl.ownerName ?? 'spotify'}</span> · {pl.trackCount} tracks
          </div>
        </div>
      </div>

      <div class="text-muted-foreground flex flex-wrap gap-1.5 px-4 pb-3.5 text-[11px]">
        <span><strong>{counts.lib}</strong> in library</span>
        <span class="text-muted-foreground/50">·</span>
        <span class="text-primary"><strong>{counts.maybe}</strong> possible</span>
        <span class="text-muted-foreground/50">·</span>
        <span style="color: #c08020;"><strong>{counts.miss}</strong> missing</span>
      </div>

      {#if counts.miss + counts.maybe > 0}
        <div class="px-4 pb-3">
          <button class="mob-btn primary"><ListPlus size={13} />Queue {counts.miss + counts.maybe} missing tracks</button>
        </div>
      {/if}

      {#if isLoadingTracks}
        <div class="text-muted-foreground px-6 py-10 text-center text-sm">Loading tracks…</div>
      {:else}
        {#each openTracks as t (t.spotifyId)}
          {@const p = pillFor(t)}
          <div class="mob-sp-track">
            <div class="mob-sp-track-meta">
              <div class="mob-sp-track-t">{t.title}</div>
              <div class="mob-sp-track-s">{t.artist} · {t.album}</div>
            </div>
            <span class="mob-pill {p.cls}">{p.label}</span>
            <span class="mob-track-d">{dur(t.durationMs)}</span>
          </div>
        {/each}
      {/if}
      <div class="h-8"></div>
    </div>
  </div>
{:else}
  <div class="mob">
    <MobileHeader
      title="Spotify"
      sub={status?.connected ? 'Connected' : 'Not connected'}
    >
      {#snippet right()}
        <button class="mob-h-btn" aria-label="Sync" disabled={isLoading} onclick={load}>
          {#if isLoading}<Loader2 size={16} class="animate-spin" />{:else}<ScanLine size={16} />{/if}
        </button>
      {/snippet}
    </MobileHeader>
    <div class="mob-scroll">
      {#if isLoading}
        <div class="text-muted-foreground px-6 py-16 text-center text-sm">Loading…</div>
      {:else if !status?.connected}
        <div class="px-4 pt-6">
          <div class="mob-card p-5 text-center" style="margin: 0 0 16px;">
            <div class="mx-auto mb-3 w-fit">{@render logo(40)}</div>
            <div class="text-base font-semibold">Connect Spotify</div>
            <div class="text-muted-foreground mx-auto mt-1.5 mb-4 max-w-xs text-[13px] leading-relaxed">
              Import your liked songs and playlists. {status?.hasCredentials
                ? ''
                : 'Add your client ID and secret in Profile → Spotify first.'}
            </div>
            {#if status?.hasCredentials}
              <button class="mob-btn primary" onclick={connect}>Connect Spotify account</button>
            {:else}
              <a class="mob-btn" href="/settings"><KeyRound size={14} />Add credentials</a>
            {/if}
          </div>
        </div>
      {:else}
        <div class="mob-sp-banner">
          {@render logo(22)}
          <div class="mob-sp-banner-meta">
            <div class="mob-sp-banner-t">
              {likedTotal.toLocaleString()} liked · {playlists.length} playlists
            </div>
            <div class="mob-sp-banner-s">
              {status.connectedAt ? `Connected ${new Date(status.connectedAt).toLocaleDateString()}` : 'Connected'}
            </div>
          </div>
          <span class="mob-sp-state"><span class="mh-pulse"></span>live</span>
        </div>

        <button class="mob-sp-pl-row" onclick={() => open({ spotifyId: 'liked', name: 'Liked Songs', trackCount: likedTotal, ownerName: 'you', imageUrl: null, description: null })}>
          <div class="mob-sp-pl-art" style="background: linear-gradient(135deg, #4527a0, #7c4dff);">
            <Heart size={18} fill="#fff" color="#fff" />
          </div>
          <div class="min-w-0 flex-1">
            <div class="mob-sp-pl-name">Liked Songs</div>
            <div class="mob-sp-pl-meta">{likedTotal.toLocaleString()} tracks</div>
          </div>
          <ChevronRight size={14} class="text-muted-foreground/50" />
        </button>

        <div class="mob-grouped-h">Playlists</div>
        {#each playlists as p (p.spotifyId)}
          {@const tint = albumTint(p.ownerName ?? 'Spotify', p.name)}
          <button class="mob-sp-pl-row" onclick={() => open(p)}>
            <div class="mob-sp-pl-art" style="background: linear-gradient(135deg, {tint.from}, {tint.to});"></div>
            <div class="min-w-0 flex-1">
              <div class="mob-sp-pl-name">{p.name}</div>
              <div class="mob-sp-pl-meta">
                {#if p.ownerName}by <span class="font-mono">{p.ownerName}</span> · {/if}{p.trackCount} tracks
              </div>
            </div>
            <ChevronRight size={14} class="text-muted-foreground/50" />
          </button>
        {/each}
        <div class="h-8"></div>
      {/if}
    </div>
  </div>
{/if}

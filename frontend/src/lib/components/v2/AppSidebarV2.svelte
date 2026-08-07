<script lang="ts">
  import { untrack } from 'svelte';
  import { afterNavigate } from '$app/navigation';
  import { page } from '$app/state';
  import { ChevronRight, LogOut, Music } from '@lucide/svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import { NAV_GROUPS, resolveNav, type NavItem } from '$lib/nav';
  import {
    buildAlbumsFromSongs,
    mergeAlbumsByName,
    fetchOverview,
    fetchStats,
    mapEnrichmentStatus,
    type ApiOverview,
    type ApiStats
  } from '$lib/api-client';
  import { signOutAndReset } from '$lib/auth/sign-out';
  import { isBuiltSong } from '$lib/album-sections';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { cn } from '$lib/utils';

  // The running build's version (clean semver), surfaced by the root layout load.
  const version = $derived(page.data.appVersion as string | null | undefined);

  // ── v2 information architecture ───────────────────────────────────────────
  // Four groups — Listen / Inbox / Add / Manage — each with its items listed flush beneath
  // (the shadcn "sidebar-04" docs style). The groups, their items and the active-route rules
  // all live in $lib/nav; this component only renders them and attaches the live counts.
  let overview = $state<ApiOverview | null>(null);
  let stats = $state<ApiStats | null>(null);

  // The sidebar is mounted on every app page and only needs counts, so it reads
  // the shared songs store instead of pulling its own copy of the library. That
  // makes this the one place the dataset is warmed, and everything else that
  // resolves a song from it — the command palette, the song-detail overlay —
  // finds it already loaded instead of waiting on a fetch of its own.
  const songs = $derived(songsStore.songs);

  $effect(() => {
    // untrack: ensureLoaded reads the same isLoading flag its own fetch writes,
    // and a tracked read would re-run this effect (and its overview/stats calls).
    untrack(() => songsStore.ensureLoaded());
    let cancelled = false;
    void (async () => {
      const [ovRes, stRes] = await Promise.allSettled([fetchOverview(), fetchStats()]);
      if (cancelled) return;
      if (ovRes.status === 'fulfilled') overview = ovRes.value;
      if (stRes.status === 'fulfilled') stats = stRes.value;
    })();
    return () => {
      cancelled = true;
    };
  });

  // ── derived counts ────────────────────────────────────────────────────────
  // The Listen group reflects the clean output only, so every Listen count below
  // is over built (LibraryBuildStatus.Done + destinationPath) songs — matching
  // what LibraryV2 actually lists. Storage/review figures stay over all
  // songs/stats (those are pipeline, not library, numbers).
  const builtSongs = $derived(songs.filter(isBuiltSong));
  const totalTracks = $derived(songs.length === 0 ? null : builtSongs.length);
  const totalBytes = $derived(stats?.storage?.totalBytes ?? null);
  const storagePct = $derived(
    totalBytes != null ? Math.min(100, Math.round((totalBytes / (2 * 1024 ** 4)) * 100)) : null
  );
  const queueRemaining = $derived(
    overview?.job
      ? Math.max(0, (overview.job.tracksDiscovered ?? 0) - (overview.job.tracksProcessed ?? 0))
      : null
  );
  const indexing = $derived(overview?.job?.status === 'running');

  const reviewCount = $derived.by(() => {
    if (songs.length === 0) return null;
    return songs
      .map((s) => mapEnrichmentStatus(s.enrichmentStatus))
      .filter((s) => s === 'needsreview' || s === 'failed').length;
  });
  // Merged like the grid, so this badge and the grid's own "N albums" footer agree.
  const albumCount = $derived.by(() =>
    songs.length === 0 ? null : mergeAlbumsByName(buildAlbumsFromSongs(builtSongs)).length
  );
  const likedCount = $derived.by(() =>
    songs.length === 0 ? null : builtSongs.filter((s) => s.likedAtUtc).length
  );
  const artistCount = $derived.by(() => {
    if (songs.length === 0) return null;
    const set = new Set<string>();
    for (const s of builtSongs) {
      const a = (s.albumArtist ?? s.artist ?? '').trim();
      if (a) set.add(a.toLowerCase());
    }
    return set.size;
  });

  const sourcePath = $derived(overview?.sourcePath ?? null);
  const destPath = $derived(overview?.destinationPath ?? null);
  const watchedFolders = $derived([sourcePath, destPath].filter(Boolean).length);
  const folderTooltip = $derived(
    [sourcePath && `Source: ${sourcePath}`, destPath && `Destination: ${destPath}`]
      .filter(Boolean)
      .join('\n')
  );

  // Counts stay here rather than in $lib/nav: that module is pure data with no store access,
  // which is what lets the tests import it. Keyed by item id.
  const COUNTS: Record<string, () => number | string | null> = {
    albums: () => albumCount,
    artists: () => artistCount,
    tracks: () => totalTracks,
    liked: () => likedCount,
    review: () => reviewCount
  };
  // The one group that carries an attention badge on its header.
  const BADGES: Record<string, () => number | null> = { inbox: () => reviewCount };

  // Single matcher, shared with the mobile bar, the tab strip and the top-bar title.
  const match = $derived(resolveNav(page.url));

  function itemActive(item: NavItem): boolean {
    return match?.item?.id === item.id;
  }

  function fmtCount(n: number | string | null | undefined): string {
    if (n == null) return '…';
    return typeof n === 'number' ? n.toLocaleString() : n;
  }

  function fmtSize(bytes: number): string {
    const gib = bytes / 1024 ** 3;
    if (gib >= 1) return `${gib.toFixed(0)} GB`;
    return `${(bytes / 1024 ** 2).toFixed(0)} MB`;
  }

  const user = $derived(
    page.data.user as
      | { email: string; role: 'Owner' | 'Demo'; displayName: string | null }
      | undefined
  );

  // On mobile the sidebar is an off-canvas Sheet; close it after navigating so a
  // tapped destination doesn't leave the drawer open over the freshly-loaded page.
  const sidebar = Sidebar.useSidebar();
  afterNavigate(() => {
    if (sidebar.isMobile) sidebar.setOpenMobile(false);
  });
</script>

<Sidebar.Root collapsible="offcanvas" variant="floating">
  <Sidebar.Header class="gap-0 px-2 pt-3 pb-2">
    <Sidebar.Menu>
      <Sidebar.MenuItem>
        <Sidebar.MenuButton size="lg" tooltipContent="MusicHoarder">
          {#snippet child({ props })}
            <a {...props} href="/pipeline">
              <div
                class="bg-primary text-primary-foreground flex aspect-square size-[30px] shrink-0 items-center justify-center rounded-lg shadow-sm"
              >
                <Music class="size-4" />
              </div>
              <div class="grid min-w-0 flex-1 text-left leading-tight">
                <span class="truncate text-sm font-semibold">MusicHoarder</span>
                <span class="text-muted-foreground truncate text-[11px]">
                  {version ? `v${version} · ` : ''}self-hosted
                </span>
              </div>
            </a>
          {/snippet}
        </Sidebar.MenuButton>
      </Sidebar.MenuItem>
    </Sidebar.Menu>
  </Sidebar.Header>

  <Sidebar.Content class="gap-3.5 px-2 py-1.5">
    {#each NAV_GROUPS as group (group.id)}
      {@const groupActive = match?.group.id === group.id}
      <!-- Only one nav level carries emphasis at a time: when an item is active it alone is
           highlighted and the group header stays neutral (it is already "expanded" by being
           on that route). The header takes the emphasis only where the group matched but no
           item did — a track page, or the library's source view. -->
      {@const headerActive = groupActive && match?.item == null}
      {@const badge = BADGES[group.id]?.()}
      <Sidebar.Group class="p-0">
        <a
          href={group.href}
          data-active={groupActive || undefined}
          class={cn(
            'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left transition-colors',
            'text-sidebar-foreground hover:bg-sidebar-accent',
            'focus-visible:ring-sidebar-ring outline-none focus-visible:ring-2'
          )}
        >
          <group.icon
            class={cn('size-4 shrink-0', headerActive ? 'text-primary' : 'text-muted-foreground')}
          />
          <span
            class={cn(
              'text-nav flex-1 font-semibold tracking-[-0.005em]',
              headerActive && 'text-primary'
            )}>{group.label}</span>
          {#if group.live && indexing}
            <span class="bg-primary mh-v2-pulse size-[7px] shrink-0 rounded-full"></span>
          {/if}
          {#if badge != null && badge > 0}
            <!-- Attention badge: one small filled circle in the accent, iOS
                 style. Amber stays reserved for the offline warning. -->
            <span
              class="bg-primary text-primary-foreground text-nav-badge grid h-[17px] min-w-[17px] shrink-0 place-items-center rounded-full px-1 leading-none font-semibold tabular-nums"
            >{badge.toLocaleString()}</span>
          {/if}
        </a>
        <Sidebar.GroupContent class="mt-0.5 flex flex-col gap-px">
          {#each group.items as item (item.id)}
            {@const active = itemActive(item)}
            {@const count = COUNTS[item.id]?.()}
            <a
              href={item.href}
              data-active={active || undefined}
              class={cn(
                'flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 transition-colors',
                'text-sidebar-foreground/70 hover:bg-sidebar-accent hover:text-sidebar-foreground',
                'data-[active=true]:text-primary data-[active=true]:font-medium',
                'focus-visible:ring-sidebar-ring outline-none focus-visible:ring-2'
              )}
            >
              <item.icon
                class={cn(
                  'size-3.5 shrink-0',
                  item.live && indexing
                    ? 'text-primary'
                    : active
                      ? 'text-primary'
                      : 'text-muted-foreground/70'
                )}
              />
              {#if item.live && indexing}
                <span class="bg-primary mh-v2-pulse size-1.5 shrink-0 rounded-full"></span>
              {/if}
              <span class="text-nav flex-1 truncate">{item.label}</span>
              {#if count != null}
                <span
                  class={cn(
                    'text-nav-count tabular-nums',
                    active ? 'text-sidebar-foreground/70' : 'text-muted-foreground/70'
                  )}
                >{fmtCount(count)}</span>
              {/if}
            </a>
          {/each}
        </Sidebar.GroupContent>
      </Sidebar.Group>
    {/each}
  </Sidebar.Content>

  <Sidebar.Footer class="gap-2 border-t px-3.5 pt-3 pb-3.5">
    {#if queueRemaining != null && queueRemaining > 0}
      <div class="text-nav-xs flex items-center gap-2">
        <span class="bg-primary mh-v2-pulse size-[7px] shrink-0 rounded-full"></span>
        <span class="text-muted-foreground flex-1 whitespace-nowrap">Indexing</span>
        <span class="text-foreground/80 text-nav-count tabular-nums whitespace-nowrap">
          {queueRemaining.toLocaleString()} active
        </span>
      </div>
    {/if}
    {#if totalBytes != null}
      <div class="text-nav-xs flex items-center gap-2">
        <span class="text-muted-foreground flex-1 whitespace-nowrap">Storage</span>
        <span class="text-foreground/80 text-nav-count tabular-nums whitespace-nowrap">
          {fmtSize(totalBytes)} / 2 TB
        </span>
      </div>
      <div class="bg-sidebar-border h-[3px] overflow-hidden rounded-full">
        <div class="bg-primary h-full transition-[width] duration-300" style="width: {storagePct ?? 0}%;"></div>
      </div>
    {/if}
    {#if watchedFolders > 0}
      <!-- Human status line — the raw source/destination paths live in Settings
           (and in the tooltip), not in permanent chrome. -->
      <a
        href="/settings"
        title={folderTooltip}
        class="text-muted-foreground hover:text-foreground focus-visible:ring-sidebar-ring text-nav-xs flex items-center gap-2 rounded-sm outline-none transition-colors focus-visible:ring-2"
      >
        <span class="flex-1 whitespace-nowrap">
          Watching {watchedFolders} {watchedFolders === 1 ? 'folder' : 'folders'}
        </span>
      </a>
    {/if}
    {#if user}
      <div
        class="bg-surface-sunken border-sidebar-border mt-1 flex items-center gap-[9px] rounded-md border px-2.5 py-2"
      >
        <a
          href="/settings"
          class="focus-visible:ring-sidebar-ring flex min-w-0 flex-1 items-center gap-[9px] rounded-sm outline-none focus-visible:ring-2"
          aria-label="Account settings"
        >
          <div
            class="flex size-6 shrink-0 items-center justify-center rounded-full bg-gradient-to-br from-cyan-700/90 to-cyan-300/90 text-[10.5px] font-semibold text-white"
          >
            {(user.displayName ?? user.email).slice(0, 2).toUpperCase()}
          </div>
          <div class="min-w-0 flex-1">
            <div class="truncate text-[11.5px] font-medium">{user.displayName ?? user.email}</div>
            <div class="text-muted-foreground truncate text-[10.5px]">{user.email}</div>
          </div>
          <ChevronRight class="text-muted-foreground size-3.5 shrink-0" />
        </a>
        <button
          type="button"
          aria-label="Sign out"
          class="text-muted-foreground hover:bg-sidebar-accent hover:text-foreground focus-visible:ring-sidebar-ring grid size-[26px] shrink-0 place-items-center rounded-md outline-none transition-colors focus-visible:ring-2"
          onclick={() => signOutAndReset()}
        >
          <LogOut class="size-3.5" />
        </button>
      </div>
    {/if}
  </Sidebar.Footer>
</Sidebar.Root>

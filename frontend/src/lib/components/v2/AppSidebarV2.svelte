<script lang="ts">
  import { afterNavigate } from '$app/navigation';
  import { page } from '$app/state';
  import {
    Activity,
    ChartColumnBig,
    ChevronRight,
    Copy,
    Disc3,
    FolderTree,
    Gauge,
    Heart,
    History,
    Inbox,
    Library,
    ListMusic,
    ListVideo,
    LogOut,
    Music,
    Music2,
    Settings,
    Sparkles,
    Tags,
    TrendingUp,
    Users,
    Workflow,
    type Icon as IconType
  } from '@lucide/svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import {
    buildAlbumsFromSongs,
    fetchOverview,
    fetchSongs,
    fetchStats,
    mapEnrichmentStatus,
    type ApiOverview,
    type ApiSong,
    type ApiStats
  } from '$lib/api-client';
  import { signOutAndReset } from '$lib/auth/sign-out';
  import { isBuiltSong } from '$lib/album-sections';
  import { cn } from '$lib/utils';

  // The running build's version (clean semver), surfaced by the root layout load.
  const version = $derived(page.data.appVersion as string | null | undefined);

  // ── v2 information architecture ───────────────────────────────────────────
  // Four flat sections, each with its sub-items listed flush beneath (the
  // shadcn "sidebar-04" docs style). For Phase 0 every sub-item deep-links into
  // the EXISTING v1 route so nothing is lost; later phases repoint them to the
  // new /pipeline · /inbox routes.
  type SubKey =
    | 'conveyor'
    | 'folders'
    | 'quality'
    | 'performance'
    | 'review'
    | 'dupes'
    | 'aiflag'
    | 'albums'
    | 'artists'
    | 'tracks'
    | 'spotify'
    | 'wishlist'
    | 'playlists';

  type SubItem = {
    id: SubKey;
    label: string;
    href: string;
    icon: typeof IconType;
    /** Show a live pulse dot (e.g. the conveyor). */
    live?: boolean;
    /** Numeric/string count rendered on the right. */
    count?: () => number | string | null;
  };

  type SectionId = 'stats' | 'pipeline' | 'inbox' | 'library' | 'history' | 'settings';

  type Section = {
    id: SectionId;
    label: string;
    href: string;
    icon: typeof IconType;
    live?: boolean;
    badge?: () => number | null;
    sub: SubItem[];
  };

  let overview = $state<ApiOverview | null>(null);
  let stats = $state<ApiStats | null>(null);
  let songs = $state<ApiSong[]>([]);

  $effect(() => {
    let cancelled = false;
    void (async () => {
      const [ovRes, stRes, songsRes] = await Promise.allSettled([
        fetchOverview(),
        fetchStats(),
        fetchSongs()
      ]);
      if (cancelled) return;
      if (ovRes.status === 'fulfilled') overview = ovRes.value;
      if (stRes.status === 'fulfilled') stats = stRes.value;
      if (songsRes.status === 'fulfilled') songs = songsRes.value;
    })();
    return () => {
      cancelled = true;
    };
  });

  // ── derived counts ────────────────────────────────────────────────────────
  // The Library section reflects the clean output only, so every Library count
  // below is over built (LibraryBuildStatus.Done + destinationPath) songs —
  // matching what LibraryV2 actually lists. Storage/review figures stay over all
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
  const albumCount = $derived.by(() => (songs.length === 0 ? null : buildAlbumsFromSongs(builtSongs).length));
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

  const NAV: Section[] = [
    {
      // Single entry — a read-only "hoard at a glance" stats dashboard.
      id: 'stats',
      label: 'Stats',
      href: '/stats',
      icon: ChartColumnBig,
      sub: []
    },
    {
      id: 'pipeline',
      label: 'Pipeline',
      href: '/pipeline',
      icon: Workflow,
      live: true,
      sub: [
        { id: 'conveyor', label: 'Conveyor', href: '/pipeline', icon: Activity, live: true },
        { id: 'folders', label: 'By folder', href: '/directories', icon: FolderTree },
        { id: 'quality', label: 'AI quality', href: '/quality', icon: Gauge },
        { id: 'performance', label: 'Performance over time', href: '/performance', icon: TrendingUp }
      ]
    },
    {
      id: 'inbox',
      label: 'Inbox',
      href: '/inbox',
      icon: Inbox,
      badge: () => reviewCount,
      sub: [
        { id: 'review', label: 'Tag review', href: '/inbox?tab=review', icon: Tags, count: () => reviewCount },
        { id: 'dupes', label: 'Duplicates', href: '/inbox?tab=dupes', icon: Copy },
        { id: 'aiflag', label: 'AI flagged', href: '/inbox?tab=ai', icon: Sparkles }
      ]
    },
    {
      id: 'library',
      label: 'Library',
      href: '/library',
      icon: Library,
      sub: [
        { id: 'albums', label: 'Albums', href: '/library', icon: Disc3, count: () => albumCount },
        { id: 'artists', label: 'Artists', href: '/artists', icon: Users, count: () => artistCount },
        { id: 'tracks', label: 'All tracks', href: '/tracks', icon: ListMusic, count: () => totalTracks },
        { id: 'spotify', label: 'Spotify', href: '/spotify', icon: Music2 },
        { id: 'wishlist', label: 'Wishlist', href: '/wishlist', icon: Heart },
        { id: 'playlists', label: 'Playlists', href: '/playlists', icon: ListVideo }
      ]
    },
    {
      // Single entry — a global feed of changes MusicHoarder wrote to the destination library.
      id: 'history',
      label: 'History',
      href: '/history',
      icon: History,
      sub: []
    },
    {
      // Single entry — settings lives entirely on /settings, no sidebar sub-pages.
      id: 'settings',
      label: 'Settings',
      href: '/settings',
      icon: Settings,
      sub: []
    }
  ];

  const pathname = $derived(page.url.pathname);

  // Active state keyed on the current v1 route. Library albums vs source view
  // both live under /library — only the album root counts as "Albums".
  const isSourceView = $derived(page.url.searchParams.get('view') === 'source');
  // Which Inbox subtab is selected (defaults to Tag review when on /inbox).
  const inboxTab = $derived(page.url.searchParams.get('tab') ?? 'review');
  const onInbox = $derived(pathname.startsWith('/inbox'));

  function subActive(item: SubItem): boolean {
    switch (item.id) {
      case 'conveyor':
        return pathname === '/pipeline';
      case 'folders':
        return pathname.startsWith('/directories');
      case 'quality':
        return pathname.startsWith('/quality');
      case 'performance':
        return pathname.startsWith('/performance');
      case 'aiflag':
        return onInbox && inboxTab === 'ai';
      case 'review':
        return onInbox && inboxTab === 'review';
      case 'dupes':
        return onInbox && inboxTab === 'dupes';
      case 'albums':
        return (pathname === '/library' || pathname.startsWith('/library/')) && !isSourceView;
      case 'artists':
        return pathname.startsWith('/artists');
      case 'tracks':
        return pathname === '/tracks';
      case 'spotify':
        return pathname.startsWith('/spotify');
      case 'wishlist':
        return pathname.startsWith('/wishlist');
      case 'playlists':
        return pathname.startsWith('/playlists');
      default:
        return false;
    }
  }

  function sectionActive(section: Section): boolean {
    // Sections with no sub-items (Settings) highlight on their own route.
    if (section.sub.length === 0) return pathname.startsWith(section.href);
    return section.sub.some((s) => subActive(s));
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
                <span class="text-muted-foreground truncate font-mono text-[10.5px]">
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
    {#each NAV as section (section.id)}
      {@const secActive = sectionActive(section)}
      {@const leafActive = secActive && section.sub.length === 0}
      {@const badge = section.badge?.()}
      <Sidebar.Group class="p-0">
        <a
          href={section.href}
          data-active={secActive || undefined}
          class={cn(
            'flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left transition-colors',
            'text-sidebar-foreground hover:bg-sidebar-accent'
          )}
        >
          <section.icon
            class={cn(
              'size-4 shrink-0',
              leafActive ? 'text-primary' : secActive ? 'text-sidebar-foreground' : 'text-muted-foreground'
            )}
          />
          <span
            class={cn(
              'flex-1 text-[13px] font-semibold tracking-[-0.005em]',
              leafActive && 'text-primary'
            )}>{section.label}</span>
          {#if section.live && indexing}
            <span class="bg-primary mh-v2-pulse size-[7px] shrink-0 rounded-full"></span>
          {/if}
          {#if badge != null && badge > 0}
            <span
              class="rounded-full border border-amber-500/35 bg-amber-500/20 px-[7px] py-px font-mono text-[10.5px] text-amber-700 dark:text-amber-400"
            >{badge.toLocaleString()}</span>
          {/if}
        </a>
        {#if section.sub.length > 0}
        <Sidebar.GroupContent class="mt-0.5 flex flex-col gap-px">
          {#each section.sub as item (item.id)}
            {@const active = subActive(item)}
            {@const count = item.count?.()}
            <a
              href={item.href}
              data-active={active || undefined}
              class={cn(
                'flex w-full items-center gap-2 rounded-md px-2.5 py-1.5 transition-colors',
                'text-sidebar-foreground/70 hover:bg-sidebar-accent hover:text-sidebar-foreground',
                'data-[active=true]:text-primary data-[active=true]:font-medium'
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
              <span class="flex-1 truncate text-[13px]">{item.label}</span>
              {#if count != null}
                <span
                  class={cn(
                    'text-[10.5px] tabular-nums',
                    active ? 'text-sidebar-foreground/70' : 'text-muted-foreground/70'
                  )}
                >{fmtCount(count)}</span>
              {/if}
            </a>
          {/each}
        </Sidebar.GroupContent>
        {/if}
      </Sidebar.Group>
    {/each}
  </Sidebar.Content>

  <Sidebar.Footer class="gap-2 border-t px-3.5 pt-3 pb-3.5">
    {#if queueRemaining != null && queueRemaining > 0}
      <div class="flex items-center gap-2 text-[11px]">
        <span class="bg-primary mh-v2-pulse size-[7px] shrink-0 rounded-full"></span>
        <span class="text-muted-foreground flex-1 whitespace-nowrap">Indexing</span>
        <span class="text-foreground/80 text-[10.5px] tabular-nums whitespace-nowrap">
          {queueRemaining.toLocaleString()} active
        </span>
      </div>
    {/if}
    {#if totalBytes != null}
      <div class="flex items-center gap-2 text-[11px]">
        <span class="text-muted-foreground flex-1 whitespace-nowrap">Storage</span>
        <span class="text-foreground/80 text-[10.5px] tabular-nums whitespace-nowrap">
          {fmtSize(totalBytes)} / 2 TB
        </span>
      </div>
      <div class="bg-sidebar-border h-[3px] overflow-hidden rounded-full">
        <div class="bg-primary h-full transition-[width] duration-300" style="width: {storagePct ?? 0}%;"></div>
      </div>
    {/if}
    {#if sourcePath}
      <div class="flex items-center gap-1.5 text-[10.5px]">
        <span class="text-muted-foreground/80 w-6 shrink-0 font-mono text-[9px] font-semibold tracking-[0.08em]">SRC</span>
        <span class="text-muted-foreground truncate font-mono" title={sourcePath}>{sourcePath}</span>
      </div>
    {/if}
    {#if destPath}
      <div class="flex items-center gap-1.5 text-[10.5px]">
        <span class="text-muted-foreground/80 w-6 shrink-0 font-mono text-[9px] font-semibold tracking-[0.08em]">DST</span>
        <span class="text-muted-foreground truncate font-mono" title={destPath}>{destPath}</span>
      </div>
    {/if}
    {#if user}
      <div
        class="bg-surface-sunken border-sidebar-border mt-1 flex items-center gap-[9px] rounded-md border px-2.5 py-2"
      >
        <a
          href="/settings"
          class="flex min-w-0 flex-1 items-center gap-[9px]"
          aria-label="Account settings"
        >
          <div
            class="flex size-6 shrink-0 items-center justify-center rounded-full bg-gradient-to-br from-cyan-700/90 to-cyan-300/90 text-[10.5px] font-semibold text-white"
          >
            {(user.displayName ?? user.email).slice(0, 2).toUpperCase()}
          </div>
          <div class="min-w-0 flex-1">
            <div class="truncate text-[11.5px] font-medium">{user.displayName ?? user.email}</div>
            <div class="text-muted-foreground truncate font-mono text-[9.5px]">{user.email}</div>
          </div>
          <ChevronRight class="text-muted-foreground size-3.5 shrink-0" />
        </a>
        <button
          type="button"
          aria-label="Sign out"
          class="text-muted-foreground hover:bg-sidebar-accent hover:text-foreground grid size-[26px] shrink-0 place-items-center rounded-md transition-colors"
          onclick={() => signOutAndReset()}
        >
          <LogOut class="size-3.5" />
        </button>
      </div>
    {/if}
  </Sidebar.Footer>
</Sidebar.Root>

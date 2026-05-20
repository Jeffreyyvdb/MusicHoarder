<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import {
    AlertTriangle,
    Calendar,
    Copy,
    FileWarning,
    FolderOpen,
    HardDrive,
    Inbox,
    LayoutDashboard,
    Library,
    ListMusic,
    LogOut,
    Music,
    Music2,
    Settings,
    Users
  } from '@lucide/svelte';
  import { signOut } from '$lib/api-client';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import {
    fetchOverview,
    fetchSongs,
    fetchStats,
    mapEnrichmentStatus,
    type ApiOverview,
    type ApiSong,
    type ApiStats
  } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  const navItems = [
    { href: '/overview', label: 'Overview', icon: LayoutDashboard },
    { href: '/app', label: 'Library', icon: Library },
    { href: '/artists', label: 'Artists', icon: Users },
    { href: '/spotify', label: 'Spotify', icon: Music2 },
    { href: '/review', label: 'Review', icon: FileWarning },
    { href: '/settings', label: 'Settings', icon: Settings }
  ] as const;

  type SectionId = 'lib' | 'recent' | 'dupes' | 'missing' | 'queue';

  const SECTIONS: { id: SectionId; label: string; icon: typeof Library; warn?: boolean; accent?: boolean }[] = [
    { id: 'lib', label: 'Library', icon: Library },
    { id: 'recent', label: 'Recently Added', icon: Calendar },
    { id: 'dupes', label: 'Duplicates', icon: Copy, warn: true },
    { id: 'missing', label: 'Missing metadata', icon: AlertTriangle, warn: true },
    { id: 'queue', label: 'Import Queue', icon: Inbox, accent: true }
  ];

  const ORGANIZE = [
    { id: 'artist', label: 'By Artist' },
    { id: 'genre', label: 'By Genre' },
    { id: 'year', label: 'By Year' },
    { id: 'label', label: 'By Label' }
  ] as const;

  type SourceKind = 'connected' | 'unknown';
  type SourceRow = { id: string; label: string; color: string; status: SourceKind; rate?: string | null };
  const SOURCES_BASE: SourceRow[] = [
    { id: 'mb', label: 'MusicBrainz', color: '#ba478f', status: 'unknown' },
    { id: 'ac', label: 'AcoustID', color: '#6a89cc', status: 'unknown' },
    { id: 'dg', label: 'Discogs', color: '#1a1a1a', status: 'unknown' },
    { id: 'sp', label: 'Spotify', color: '#1db954', status: 'unknown' },
    { id: 'lf', label: 'Last.fm', color: '#d51007', status: 'unknown' },
    { id: 'am', label: 'Apple Music', color: '#fa243c', status: 'unknown' },
    { id: 'lr', label: 'LRCLIB', color: '#4a9a6a', status: 'unknown' },
    { id: 'gn', label: 'Genius', color: '#f9e300', status: 'unknown' },
    { id: 'caa', label: 'Cover Art Archive', color: '#ba478f', status: 'unknown' }
  ];

  function formatLibrarySize(bytes: number): string {
    const gib = bytes / (1024 * 1024 * 1024);
    if (gib >= 1) return `${gib.toFixed(1)} GB`;
    const mib = bytes / (1024 * 1024);
    return `${mib.toFixed(0)} MB`;
  }

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

  const pathname = $derived(page.url.pathname);
  const onLibrary = $derived(pathname === '/app' || pathname.startsWith('/app/'));
  const activeSection = $derived<SectionId>(
    (page.url.searchParams.get('section') as SectionId | null) ?? 'lib'
  );

  const totalBytes = $derived(stats?.storage?.totalBytes ?? null);
  const totalTracks = $derived(stats?.tracks?.total ?? null);
  const storagePct = $derived(totalBytes != null ? Math.min(100, Math.round((totalBytes / (2 * 1024 ** 4)) * 100)) : null);
  const queueRemaining = $derived(
    overview?.job
      ? Math.max(0, (overview.job.tracksDiscovered ?? 0) - (overview.job.tracksProcessed ?? 0))
      : null
  );

  const sourcePath = $derived(overview?.sourcePath ?? null);
  const destPath = $derived(overview?.destinationPath ?? null);

  const counts = $derived.by(() => {
    if (songs.length === 0) {
      return { lib: totalTracks, recent: null, dupes: null, missing: null, queue: queueRemaining };
    }
    const normalized = songs.map((s) => mapEnrichmentStatus(s.enrichmentStatus));
    const missing = normalized.filter((s) => s === 'needsreview' || s === 'failed').length;
    // Duplicate heuristic: shared (artist, title, ~duration) or fingerprint identity.
    const fpSeen = new Map<string, number>();
    const titleSeen = new Map<string, number>();
    for (const s of songs) {
      const fp = s.fingerprint?.trim();
      if (fp) fpSeen.set(fp, (fpSeen.get(fp) ?? 0) + 1);
      const artist = (s.albumArtist ?? s.artist ?? '').trim().toLowerCase();
      const title = (s.title ?? s.fileName).trim().toLowerCase();
      const dur = s.durationSeconds ? Math.round(s.durationSeconds) : 0;
      const key = `${artist}::${title}::${dur}`;
      titleSeen.set(key, (titleSeen.get(key) ?? 0) + 1);
    }
    let dupes = 0;
    for (const v of fpSeen.values()) if (v > 1) dupes += v;
    for (const v of titleSeen.values()) if (v > 1) dupes += v;
    // Recently added = last 50 by id-desc (id monotonic in this app).
    const recent = Math.min(50, songs.length);
    return {
      lib: totalTracks ?? songs.length,
      recent,
      dupes,
      missing,
      queue: queueRemaining
    };
  });

  const organizeCounts = $derived.by(() => {
    if (songs.length === 0) return { artist: null, genre: null, year: null, label: null };
    const artistSet = new Set<string>();
    const yearSet = new Set<number>();
    for (const s of songs) {
      const a = (s.albumArtist ?? s.artist ?? '').trim();
      if (a) artistSet.add(a.toLowerCase());
      if (s.year) yearSet.add(s.year);
    }
    return {
      artist: artistSet.size,
      genre: null as number | null,
      year: yearSet.size,
      label: null as number | null
    };
  });

  const sourceRows = $derived<SourceRow[]>(
    SOURCES_BASE.map((s) => {
      if (s.id === 'sp' && songs.some((song) => song.spotifyId)) {
        const matched = songs.filter((song) => song.spotifyId).length;
        return { ...s, status: 'connected', rate: `${Math.min(99, Math.round((matched / Math.max(1, songs.length)) * 100))}%` };
      }
      if (s.id === 'mb' && songs.some((song) => song.musicBrainzId)) {
        const matched = songs.filter((song) => song.musicBrainzId).length;
        return { ...s, status: 'connected', rate: `${Math.min(99, Math.round((matched / Math.max(1, songs.length)) * 100))}%` };
      }
      if (s.id === 'ac' && songs.some((song) => song.acoustIdTrackId || song.fingerprint)) {
        const matched = songs.filter((song) => song.acoustIdTrackId).length;
        return { ...s, status: 'connected', rate: matched ? `${Math.min(99, Math.round((matched / Math.max(1, songs.length)) * 100))}%` : null };
      }
      if (s.id === 'lr' && songs.some((song) => song.lrclibId)) {
        const matched = songs.filter((song) => song.lrclibId).length;
        return { ...s, status: 'connected', rate: `${Math.min(99, Math.round((matched / Math.max(1, songs.length)) * 100))}%` };
      }
      return s;
    })
  );

  function fmtCount(n: number | null | undefined): string {
    if (n == null) return '…';
    return n.toLocaleString();
  }
</script>

<Sidebar.Root collapsible="icon">
  <Sidebar.Header>
    <Sidebar.Menu>
      <Sidebar.MenuItem>
        <Sidebar.MenuButton size="lg" tooltipContent="MusicHoarder">
          {#snippet child({ props })}
            <a {...props} href="/overview">
              <div
                class="bg-primary text-primary-foreground flex aspect-square size-8 shrink-0 items-center justify-center rounded-lg shadow-sm"
              >
                <Music class="size-4" />
              </div>
              <div class="grid min-w-0 flex-1 text-left leading-tight">
                <span class="truncate text-sm font-semibold">MusicHoarder</span>
                <span class="text-muted-foreground truncate text-[11px]">
                  {overview?.job?.status === 'running' ? 'v0.4.2 · indexing' : 'v0.4.2 · idle'}
                </span>
              </div>
            </a>
          {/snippet}
        </Sidebar.MenuButton>
      </Sidebar.MenuItem>
    </Sidebar.Menu>
  </Sidebar.Header>

  <Sidebar.Content>
    {#if sourcePath || destPath}
      <Sidebar.Group class="group-data-[collapsible=icon]:hidden">
        <Sidebar.GroupLabel>Paths</Sidebar.GroupLabel>
        <Sidebar.GroupContent class="px-2 pt-1">
          {#if sourcePath}
            <div class="mb-2">
              <div class="text-muted-foreground/80 text-[9.5px] font-semibold tracking-[0.08em] uppercase">
                Source
              </div>
              <div class="mt-0.5 flex items-center gap-1.5 text-[11px]">
                <FolderOpen class="text-muted-foreground size-3 shrink-0" />
                <span class="text-muted-foreground truncate font-mono" title={sourcePath}>{sourcePath}</span>
              </div>
            </div>
          {/if}
          {#if destPath}
            <div>
              <div class="text-muted-foreground/80 text-[9.5px] font-semibold tracking-[0.08em] uppercase">
                Destination
              </div>
              <div class="mt-0.5 flex items-center gap-1.5 text-[11px]">
                <HardDrive class="text-muted-foreground size-3 shrink-0" />
                <span class="text-muted-foreground truncate font-mono" title={destPath}>{destPath}</span>
              </div>
            </div>
          {/if}
        </Sidebar.GroupContent>
      </Sidebar.Group>
    {/if}

    <Sidebar.Group>
      <Sidebar.GroupLabel>Navigation</Sidebar.GroupLabel>
      <Sidebar.GroupContent>
        <Sidebar.Menu>
          {#each navItems as item (item.href)}
            {@const isActive = item.href === '/app' ? onLibrary : pathname.startsWith(item.href)}
            <Sidebar.MenuItem>
              <Sidebar.MenuButton {isActive} tooltipContent={item.label}>
                {#snippet child({ props })}
                  <a {...props} href={item.href}>
                    <item.icon class="size-4" />
                    <span>{item.label}</span>
                  </a>
                {/snippet}
              </Sidebar.MenuButton>
            </Sidebar.MenuItem>
          {/each}
        </Sidebar.Menu>
      </Sidebar.GroupContent>
    </Sidebar.Group>

    {#if onLibrary}
      <Sidebar.Group class="group-data-[collapsible=icon]:hidden">
        <Sidebar.GroupLabel>Library</Sidebar.GroupLabel>
        <Sidebar.GroupContent class="px-2">
          {#each SECTIONS as section (section.id)}
            {@const isActive = activeSection === section.id}
            {@const count = counts[section.id]}
            <a
              href={section.id === 'lib' ? '/app' : `/app?section=${section.id}`}
              data-active={isActive || undefined}
              class={cn(
                'mb-0.5 flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-[12.5px] transition-colors',
                'text-sidebar-foreground/80 hover:bg-sidebar-accent hover:text-sidebar-accent-foreground',
                'data-[active=true]:bg-primary/10 data-[active=true]:text-foreground data-[active=true]:font-medium',
                section.warn && !isActive && '[&_svg]:text-amber-600 dark:[&_svg]:text-amber-500',
                section.accent && !isActive && 'text-primary'
              )}
            >
              <section.icon class={cn('size-3.5 shrink-0', isActive && 'text-primary')} />
              <span class="flex-1 truncate text-left">{section.label}</span>
              <span class="text-muted-foreground font-mono text-[10.5px]">
                {fmtCount(count)}
              </span>
            </a>
          {/each}
          <a
            href="/app/files"
            class="text-sidebar-foreground/80 hover:bg-sidebar-accent hover:text-sidebar-accent-foreground mb-0.5 flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-[12.5px] transition-colors"
          >
            <FolderOpen class="size-3.5 shrink-0" />
            <span class="flex-1 truncate text-left">Files (source / dest)</span>
          </a>
        </Sidebar.GroupContent>
      </Sidebar.Group>

      <Sidebar.Group class="group-data-[collapsible=icon]:hidden">
        <Sidebar.GroupLabel>Organize by</Sidebar.GroupLabel>
        <Sidebar.GroupContent class="px-2">
          {#each ORGANIZE as item (item.id)}
            {@const count = organizeCounts[item.id]}
            <div
              class="text-sidebar-foreground/70 hover:bg-sidebar-accent mb-0.5 flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-[12.5px] transition-colors"
              title="Coming soon"
            >
              <ListMusic class="size-3.5 shrink-0" />
              <span class="flex-1 truncate text-left">{item.label}</span>
              <span class="text-muted-foreground font-mono text-[10.5px]">{fmtCount(count)}</span>
            </div>
          {/each}
        </Sidebar.GroupContent>
      </Sidebar.Group>
    {/if}

    <Sidebar.Group class="group-data-[collapsible=icon]:hidden">
      <Sidebar.GroupLabel>Enrichment sources</Sidebar.GroupLabel>
      <Sidebar.GroupContent class="px-2">
        {#each sourceRows as source (source.id)}
          <div class="flex items-center gap-2 px-2 py-1 text-[12px]">
            <span
              class={cn(
                'size-2 shrink-0 rounded-full ring-2 ring-white/60 dark:ring-white/10',
                source.status === 'unknown' && 'opacity-40'
              )}
              style="background: {source.color};"
            ></span>
            <span class="text-sidebar-foreground/80 flex-1 truncate">{source.label}</span>
            {#if source.rate}
              <span class="text-muted-foreground font-mono text-[10.5px]">{source.rate}</span>
            {/if}
          </div>
        {/each}
      </Sidebar.GroupContent>
    </Sidebar.Group>
  </Sidebar.Content>

  <Sidebar.Footer
    class={cn(
      'group-data-[collapsible=icon]:hidden gap-1.5',
      playerStore.currentSong && 'mb-14 sm:mb-16'
    )}
  >
    {#if queueRemaining != null && queueRemaining > 0}
      <div class="flex items-center gap-2 px-2 py-1 text-[11px]">
        <span class="bg-primary mh-pulse-dot size-1.5 rounded-full"></span>
        <span class="text-muted-foreground flex-1">Indexing</span>
        <span class="text-foreground/80 font-mono">{queueRemaining.toLocaleString()} left</span>
      </div>
    {/if}
    {#if page.data.user}
      {@const u = page.data.user as { email: string; role: 'Owner' | 'Demo'; displayName: string | null }}
      <div class="bg-sidebar-accent/40 flex items-center gap-2 rounded-lg px-2 py-2">
        <div
          class="flex size-7 shrink-0 items-center justify-center rounded-full bg-gradient-to-br from-cyan-500/80 to-indigo-500/80 text-[11px] font-semibold text-white"
        >
          {(u.displayName ?? u.email).slice(0, 2).toUpperCase()}
        </div>
        <div class="min-w-0 flex-1">
          <div class="truncate text-xs font-medium">{u.displayName ?? u.email}</div>
          <div class="text-muted-foreground truncate font-mono text-[10px]">{u.role}</div>
        </div>
        <button
          type="button"
          aria-label="Sign out"
          class="hover:text-foreground text-muted-foreground rounded p-1"
          onclick={async () => {
            await signOut();
            await goto('/login', { invalidateAll: true });
          }}
        >
          <LogOut class="size-3.5" />
        </button>
      </div>
    {/if}
    {#if totalBytes !== null}
      <div class="bg-sidebar-accent/40 rounded-lg p-3">
        <div class="flex items-center gap-2 text-sm">
          <HardDrive class="text-primary size-4" />
          <span class="font-medium">Library Size</span>
        </div>
        <p class="mt-1 text-xl font-bold">{formatLibrarySize(totalBytes)}</p>
        {#if totalTracks !== null}
          <p class="text-muted-foreground text-[11px]">
            {totalTracks.toLocaleString()} tracks · {storagePct ?? 0}% of 2 TB
          </p>
        {/if}
        <div class="bg-border mt-2 h-[3px] overflow-hidden rounded-full">
          <div class="bg-primary h-full transition-[width] duration-300" style="width: {storagePct ?? 0}%;"></div>
        </div>
      </div>
    {/if}
  </Sidebar.Footer>

  <Sidebar.Rail />
</Sidebar.Root>

<style>
  :global(.mh-pulse-dot) {
    box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    animation: mh-pulse 2s infinite;
  }
  @keyframes mh-pulse {
    0% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    }
    70% {
      box-shadow: 0 0 0 6px oklch(0.5 0.17 145 / 0);
    }
    100% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0);
    }
  }
</style>

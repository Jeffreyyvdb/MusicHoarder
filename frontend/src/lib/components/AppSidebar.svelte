<script lang="ts">
  import { page } from '$app/state';
  import {
    FileWarning,
    FolderOpen,
    LayoutDashboard,
    Music,
    Music2,
    Settings,
    Users
  } from '@lucide/svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import { fetchOverview, fetchStats, type ApiOverview, type ApiStats } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  type LibraryView = 'albums' | 'source' | 'destination';

  const libraryViews: { value: LibraryView; label: string }[] = [
    { value: 'albums', label: 'Albums' },
    { value: 'source', label: 'Source' },
    { value: 'destination', label: 'Destination' }
  ];

  const navItems = [
    { href: '/overview', label: 'Overview', icon: LayoutDashboard },
    { href: '/app', label: 'Library', icon: FolderOpen },
    { href: '/artists', label: 'Artists', icon: Users },
    { href: '/spotify', label: 'Spotify', icon: Music2 },
    { href: '/review', label: 'Review', icon: FileWarning },
    { href: '/settings', label: 'Settings', icon: Settings }
  ];

  function formatLibrarySize(bytes: number): string {
    const gib = bytes / (1024 * 1024 * 1024);
    if (gib >= 1) return `${gib.toFixed(1)} GB`;
    const mib = bytes / (1024 * 1024);
    return `${mib.toFixed(0)} MB`;
  }

  let overview = $state<ApiOverview | null>(null);
  let stats = $state<ApiStats | null>(null);

  $effect(() => {
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

  const pathname = $derived(page.url.pathname);
  const onLibrary = $derived(pathname === '/app' || pathname.startsWith('/app/'));
  const activeLibraryView = $derived<LibraryView>(
    onLibrary ? ((page.url.searchParams.get('view') as LibraryView | null) ?? 'albums') : 'albums'
  );

  const pathsByView = $derived<Record<LibraryView, string | null>>({
    albums: null,
    source: overview?.sourcePath ?? null,
    destination: overview?.destinationPath ?? null
  });

  const totalBytes = $derived(stats?.storage?.totalBytes ?? null);
  const totalTracks = $derived(stats?.tracks?.total ?? null);
</script>

<Sidebar.Root collapsible="icon">
  <Sidebar.Header>
    <Sidebar.Menu>
      <Sidebar.MenuItem>
        <Sidebar.MenuButton size="lg" tooltipContent="MusicHoarder">
          {#snippet child({ props })}
            <a {...props} href="/overview">
              <div
                class="bg-primary text-primary-foreground flex aspect-square size-8 shrink-0 items-center justify-center rounded-lg"
              >
                <Music class="size-4" />
              </div>
              <div class="grid min-w-0 flex-1 text-left leading-tight">
                <span class="truncate text-sm font-semibold">MusicHoarder</span>
                <span class="text-muted-foreground truncate text-xs">Library manager</span>
              </div>
            </a>
          {/snippet}
        </Sidebar.MenuButton>
      </Sidebar.MenuItem>
    </Sidebar.Menu>
  </Sidebar.Header>

  <Sidebar.Content>
    <Sidebar.Group>
      <Sidebar.GroupLabel>Navigation</Sidebar.GroupLabel>
      <Sidebar.GroupContent>
        <Sidebar.Menu>
          {#each navItems as item (item.href)}
            {@const isActive =
              item.href === '/app' ? onLibrary : pathname.startsWith(item.href)}
            <Sidebar.MenuItem>
              <Sidebar.MenuButton {isActive} tooltipContent={item.label}>
                {#snippet child({ props })}
                  <a {...props} href={item.href}>
                    <item.icon class="size-4" />
                    <span>{item.label}</span>
                  </a>
                {/snippet}
              </Sidebar.MenuButton>

              {#if item.href === '/app' && onLibrary}
                <Sidebar.MenuSub>
                  {#each libraryViews as view (view.value)}
                    {@const href =
                      view.value === 'albums' ? '/app' : `/app?view=${view.value}`}
                    {@const isActiveView = activeLibraryView === view.value}
                    {@const path = pathsByView[view.value]}
                    <Sidebar.MenuSubItem>
                      <a
                        {href}
                        data-active={isActiveView || undefined}
                        class="text-sidebar-foreground/80 outline-hidden hover:bg-sidebar-accent hover:text-sidebar-accent-foreground focus-visible:ring-sidebar-ring data-[active=true]:bg-sidebar-accent data-[active=true]:text-sidebar-accent-foreground flex min-w-0 flex-col gap-0.5 rounded-md px-2 py-1.5 text-sm transition-colors focus-visible:ring-2 data-[active=true]:font-medium"
                      >
                        <span class="truncate">{view.label}</span>
                        {#if path}
                          <span
                            class="text-muted-foreground truncate font-mono text-[10px] leading-tight"
                            title={path}
                          >
                            {path}
                          </span>
                        {/if}
                      </a>
                    </Sidebar.MenuSubItem>
                  {/each}
                </Sidebar.MenuSub>
              {/if}
            </Sidebar.MenuItem>
          {/each}
        </Sidebar.Menu>
      </Sidebar.GroupContent>
    </Sidebar.Group>
  </Sidebar.Content>

  {#if totalBytes !== null}
    <Sidebar.Footer
      class={cn(
        'group-data-[collapsible=icon]:hidden',
        playerStore.currentSong && 'mb-14 sm:mb-16'
      )}
    >
      <div class="bg-sidebar-accent/60 rounded-lg p-3">
        <div class="flex items-center gap-2 text-sm">
          <FolderOpen class="text-primary size-4" />
          <span class="font-medium">Library Size</span>
        </div>
        <p class="mt-1 text-2xl font-bold">{formatLibrarySize(totalBytes)}</p>
        {#if totalTracks !== null}
          <p class="text-muted-foreground text-xs">
            {totalTracks.toLocaleString()} tracks total
          </p>
        {/if}
      </div>
    </Sidebar.Footer>
  {/if}

  <Sidebar.Rail />
</Sidebar.Root>

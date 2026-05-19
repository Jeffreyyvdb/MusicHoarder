<script lang="ts">
  import { page } from '$app/state';
  import * as Resizable from '$lib/components/ui/resizable';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as Sheet from '$lib/components/ui/sheet';
  import { Button } from '$lib/components/ui/button';
  import FolderTree from '$lib/components/file-browser/FolderTree.svelte';
  import FileGrid from '$lib/components/file-browser/FileGrid.svelte';
  import BreadcrumbNav from '$lib/components/file-browser/BreadcrumbNav.svelte';
  import TrackDetails from '$lib/components/file-browser/TrackDetails.svelte';
  import Toolbar from '$lib/components/file-browser/Toolbar.svelte';
  import AlbumGridView from '$lib/components/file-browser/AlbumGridView.svelte';
  import AlbumDetailView from '$lib/components/file-browser/AlbumDetailView.svelte';
  import type {
    LibrarySortBy,
    LibrarySortDirection,
    LibraryFilterBy
  } from '$lib/components/file-browser/Toolbar.svelte';
  import {
    findAncestorFolderId,
    findFileById,
    getPathToFile
  } from '$lib/mock-data';
  import type { FileItem } from '$lib/types';
  import { Menu } from '@lucide/svelte';
  import { cn } from '$lib/utils';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import {
    buildFileSystemFromSongs,
    fetchSongs,
    type ApiSong,
    type LibraryPathMode
  } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';

  const EMPTY_LIBRARY: FileItem[] = [
    {
      id: 'root',
      name: 'music',
      type: 'folder',
      path: '/Volumes/music',
      parentId: null,
      children: []
    }
  ];

  function countAudioFiles(items: FileItem[]): number {
    let count = 0;
    for (const item of items) {
      if (item.type === 'audio') count++;
      if (item.children && item.children.length > 0) count += countAudioFiles(item.children);
    }
    return count;
  }

  type SearchableItem = { item: FileItem; searchText: string };

  function buildSearchIndex(items: FileItem[]): SearchableItem[] {
    const result: SearchableItem[] = [];
    const stack = [...items];
    while (stack.length > 0) {
      const item = stack.pop();
      if (!item) continue;
      const parts: string[] = [item.name, item.path];
      if (item.type === 'audio' && item.metadata) {
        parts.push(
          item.metadata.title,
          item.metadata.artist,
          item.metadata.album,
          item.metadata.format
        );
      }
      result.push({ item, searchText: parts.join('\0').toLowerCase() });
      if (item.type === 'folder' && item.children?.length) stack.push(...item.children);
    }
    return result;
  }

  function getAggregateSize(item: FileItem): number {
    if (item.type === 'audio') return item.metadata?.fileSize ?? 0;
    let totalSize = 0;
    const stack = [...(item.children ?? [])];
    while (stack.length > 0) {
      const child = stack.pop();
      if (!child) continue;
      if (child.type === 'audio') {
        totalSize += child.metadata?.fileSize ?? 0;
        continue;
      }
      if (child.children?.length) stack.push(...child.children);
    }
    return totalSize;
  }

  function getRecencyValue(item: FileItem): number {
    if (item.type === 'audio') {
      const songId = Number(item.id.replace('song:', ''));
      return Number.isFinite(songId) ? songId : 0;
    }
    let newestSongId = 0;
    const stack = [...(item.children ?? [])];
    while (stack.length > 0) {
      const child = stack.pop();
      if (!child) continue;
      if (child.type === 'audio') {
        const parsedSongId = Number(child.id.replace('song:', ''));
        if (Number.isFinite(parsedSongId)) {
          newestSongId = Math.max(newestSongId, parsedSongId);
        }
        continue;
      }
      if (child.children?.length) stack.push(...child.children);
    }
    return newestSongId;
  }

  const isMobile = new IsMobile();

  let songs = $state<ApiSong[]>([]);
  let currentFolderId = $state<string>('root');
  let selectedFileId = $state<string | null>(null);
  let viewMode = $state<'grid' | 'list'>('grid');
  let searchQuery = $state('');
  let sortBy = $state<LibrarySortBy>('name');
  let sortDirection = $state<LibrarySortDirection>('asc');
  let filterBy = $state<LibraryFilterBy>('all');
  let showDetails = $state(false);
  let sidebarOpen = $state(false);
  let apiError = $state<string | null>(null);
  let isLoading = $state(true);
  let isRefreshing = $state(false);
  let isHydrated = $state(false);
  let expandedFolderIds = $state<Set<string>>(new Set(['root']));

  let appliedSongDeepLink: string | null = null;
  let prevDetailsRequestId = 0;
  let isMountedRef = true;

  const viewParam = $derived(page.url.searchParams.get('view'));
  const libraryView = $derived<'albums' | 'source' | 'destination'>(
    viewParam === 'source' || viewParam === 'destination' ? viewParam : 'albums'
  );
  const libraryMode = $derived<LibraryPathMode>(
    libraryView === 'source' ? 'source' : 'destination'
  );

  const fileSystem = $derived(
    songs.length > 0 ? buildFileSystemFromSongs(songs, libraryMode) : EMPTY_LIBRARY
  );

  // Persist view mode in localStorage on the client only.
  $effect(() => {
    if (typeof window === 'undefined') return;
    const stored = localStorage.getItem('musichoarder-library-view') as 'grid' | 'list' | null;
    if (stored === 'grid' || stored === 'list') viewMode = stored;
    isHydrated = true;
  });

  $effect(() => {
    if (!isHydrated || typeof window === 'undefined') return;
    localStorage.setItem('musichoarder-library-view', viewMode);
  });

  async function loadSongs(mode: 'initial' | 'refresh') {
    try {
      if (mode === 'initial') isLoading = true;
      else isRefreshing = true;

      const loadedSongs = await fetchSongs();
      if (!isMountedRef) return;
      songs = loadedSongs;
      apiError = null;
    } catch (err) {
      if (!isMountedRef) return;
      songs = [];
      const message = err instanceof Error ? err.message : 'Unknown API error';
      apiError = `Unable to load library data from API. ${message}`;
    } finally {
      if (isMountedRef) {
        if (mode === 'initial') isLoading = false;
        else isRefreshing = false;
      }
    }
  }

  $effect(() => {
    isMountedRef = true;
    void loadSongs('initial');
    return () => {
      isMountedRef = false;
    };
  });

  // Reset selection state when the library mode changes (source ↔ destination).
  $effect(() => {
    void libraryMode;
    currentFolderId = 'root';
    selectedFileId = null;
    showDetails = false;
    expandedFolderIds = new Set(['root']);
    appliedSongDeepLink = null;
  });

  const currentFolder = $derived(findFileById(fileSystem, currentFolderId));

  $effect(() => {
    if (currentFolder || currentFolderId === 'root') return;
    currentFolderId = 'root';
    selectedFileId = null;
    showDetails = false;
  });

  const breadcrumbPath = $derived(getPathToFile(fileSystem, currentFolderId));
  const selectedFile = $derived(
    selectedFileId ? findFileById(fileSystem, selectedFileId) : null
  );

  const expectedSongCount = $derived(
    libraryMode === 'destination'
      ? songs.filter((song) => Boolean(song.destinationPath?.trim())).length
      : songs.length
  );
  const mappedSongCount = $derived(countAudioFiles(fileSystem));

  const coverageWarning = $derived.by(() => {
    if (apiError || isLoading || expectedSongCount === 0) return null;
    if (mappedSongCount === expectedSongCount) return null;
    return `Loaded ${mappedSongCount} of ${expectedSongCount} ${libraryMode} songs. Some songs could not be mapped to folders.`;
  });

  const searchIndex = $derived(buildSearchIndex(currentFolder?.children ?? []));

  const visibleItems = $derived.by(() => {
    const folderItems = currentFolder?.children ?? [];
    const query = searchQuery.trim().toLowerCase();

    const searchedItems =
      query.length === 0
        ? folderItems
        : searchIndex.filter((entry) => entry.searchText.includes(query)).map((entry) => entry.item);

    const filtered = searchedItems.filter((item) => {
      switch (filterBy) {
        case 'audio':
          return item.type === 'audio';
        case 'folders':
          return item.type === 'folder';
        case 'pendingEnrichment':
          return item.type === 'audio' && item.metadata?.enrichmentStatus === 'pending';
        case 'all':
        default:
          return true;
      }
    });

    const sorted = [...filtered].sort((left, right) => {
      if (left.type !== right.type) return left.type === 'folder' ? -1 : 1;
      let result = 0;
      switch (sortBy) {
        case 'size':
          result = getAggregateSize(left) - getAggregateSize(right);
          break;
        case 'type':
          result = left.type.localeCompare(right.type);
          break;
        case 'dateModified':
          result = getRecencyValue(left) - getRecencyValue(right);
          break;
        case 'name':
        default:
          result = left.name.localeCompare(right.name, undefined, { sensitivity: 'base' });
          break;
      }
      if (result === 0)
        result = left.name.localeCompare(right.name, undefined, { sensitivity: 'base' });
      if (result === 0) result = left.id.localeCompare(right.id);
      return sortDirection === 'asc' ? result : -result;
    });
    return sorted;
  });

  const emptyStateMessage = $derived.by(() => {
    if (searchQuery.trim().length > 0) return 'No files match your search in this folder tree';
    if (filterBy !== 'all') return 'No files match the selected filter';
    return 'This folder is empty';
  });

  // "Show details" handshake from MiniPlayer — when the user clicks the
  // mini-player's track info, select that song and open the details sheet.
  $effect(() => {
    const reqId = playerStore.detailsRequestId;
    if (reqId === prevDetailsRequestId) return;
    prevDetailsRequestId = reqId;
    if (!playerStore.currentSong) return;
    const songFileId = `song:${playerStore.currentSong.id}`;
    selectedFileId = songFileId;
    showDetails = true;
  });

  // Deep-link via ?song=<id> — open the song in the details panel and expand
  // its ancestor folders so it's visible in the tree.
  $effect(() => {
    if (isLoading || apiError) return;
    const raw = page.url.searchParams.get('song');
    if (raw == null || raw === '') return;
    const songId = Number.parseInt(raw, 10);
    if (!Number.isFinite(songId) || songId < 1) return;

    const fileId = `song:${songId}`;
    const file = findFileById(fileSystem, fileId);
    if (!file) return;

    const key = `${libraryMode}:${songId}`;
    if (appliedSongDeepLink === key) return;
    appliedSongDeepLink = key;

    const parentId = findAncestorFolderId(fileSystem, fileId);
    if (parentId) {
      currentFolderId = parentId;
      const path = getPathToFile(fileSystem, parentId);
      const ids = path.map((p) => p.id);
      const next = new Set(expandedFolderIds);
      for (const id of ids) next.add(id);
      expandedFolderIds = next;
    }

    selectedFileId = fileId;
    showDetails = true;
  });

  function handleFolderSelect(item: FileItem) {
    if (item.type === 'folder') {
      currentFolderId = item.id;
      selectedFileId = null;
      showDetails = false;
      sidebarOpen = false;
    }
  }

  function handleFileSelect(item: FileItem) {
    if (item.type === 'folder' && isMobile.current) {
      handleFileOpen(item);
      return;
    }
    selectedFileId = item.id;
    if (item.type === 'audio') showDetails = true;
  }

  function handleFileOpen(item: FileItem) {
    if (item.type === 'folder') {
      currentFolderId = item.id;
      selectedFileId = null;
      showDetails = false;
    } else {
      selectedFileId = item.id;
      showDetails = true;
    }
  }

  function handleNavigate(item: FileItem) {
    currentFolderId = item.id;
    selectedFileId = null;
    showDetails = false;
  }

  const albumParam = $derived(page.url.searchParams.get('album'));
  const albumKey = $derived(albumParam ? decodeURIComponent(albumParam) : null);
  const detailsOpen = $derived(showDetails && selectedFile?.type === 'audio');
</script>

{#if libraryView === 'albums'}
  <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
    {#if apiError}
      <div class="border-border bg-card/30 text-destructive border-b px-4 py-2 text-xs md:px-6">
        {apiError}
      </div>
    {/if}
    {#if albumKey}
      <AlbumDetailView {songs} {albumKey} {isLoading} />
    {:else}
      <AlbumGridView {songs} {isLoading} {searchQuery} />
    {/if}
  </div>
  <Sheet.Root open={detailsOpen} onOpenChange={(open) => !open && (showDetails = false)}>
    <Sheet.Content
      side={isMobile.current ? 'bottom' : 'right'}
      class={cn('p-0 [&>button]:hidden', isMobile.current ? 'h-[85vh]' : 'w-full sm:max-w-md')}
    >
      <Sheet.Title class="sr-only">Track Details</Sheet.Title>
      <Sheet.Description class="sr-only">
        View track metadata, lyrics, and sources
      </Sheet.Description>
      {#if selectedFile?.type === 'audio'}
        <TrackDetails
          file={selectedFile}
          onClose={() => (showDetails = false)}
          onResetEnrichment={() => void loadSongs('refresh')}
        />
      {/if}
    </Sheet.Content>
  </Sheet.Root>
{:else}
  <Sheet.Root bind:open={sidebarOpen}>
    <Sheet.Content side="left" class="w-72 p-0">
      <Sheet.Title class="sr-only">Library Navigation</Sheet.Title>
      <Sheet.Description class="sr-only">Browse your music library folders</Sheet.Description>
      <div
        class="border-sidebar-border bg-sidebar text-sidebar-foreground flex h-full min-h-0 flex-col border-r"
      >
        <ScrollArea class="min-h-0 flex-1 p-2">
          <FolderTree
            items={fileSystem}
            selectedId={currentFolderId}
            onSelect={handleFolderSelect}
            bind:expandedIds={expandedFolderIds}
          />
        </ScrollArea>
      </div>
    </Sheet.Content>
  </Sheet.Root>

  {#if isMobile.current}
    <Sheet.Root
      open={showDetails && !!selectedFile}
      onOpenChange={(open) => !open && (showDetails = false)}
    >
      <Sheet.Content side="bottom" class="h-[85vh] p-0 [&>button]:hidden">
        <Sheet.Title class="sr-only">Track Details</Sheet.Title>
        <Sheet.Description class="sr-only">
          View track metadata, lyrics, and sources
        </Sheet.Description>
        {#if selectedFile?.type === 'audio'}
          <TrackDetails
            file={selectedFile}
            onClose={() => (showDetails = false)}
            onResetEnrichment={() => void loadSongs('refresh')}
          />
        {/if}
      </Sheet.Content>
    </Sheet.Root>
  {/if}

  {#if !isHydrated || !isMobile.current}
    <Resizable.PaneGroup id="library-browser-panels" direction="horizontal" class="min-h-0 flex-1">
      <Resizable.Pane id="library-sidebar-panel" order={1} defaultSize={20} minSize={15} maxSize={30}>
        <div
          class="border-sidebar-border bg-sidebar text-sidebar-foreground flex h-full min-h-0 flex-col border-r"
        >
          <ScrollArea class="min-h-0 flex-1 p-2">
            <FolderTree
              items={fileSystem}
              selectedId={currentFolderId}
              onSelect={handleFolderSelect}
              bind:expandedIds={expandedFolderIds}
            />
          </ScrollArea>
        </div>
      </Resizable.Pane>

      <Resizable.Handle />

      <Resizable.Pane id="library-main-panel" order={2} defaultSize={showDetails ? 50 : 80}>
        <div class="flex h-full min-h-0 flex-col">
          <div class="border-border bg-card/30 border-b px-4 py-2">
            <BreadcrumbNav path={breadcrumbPath} onNavigate={handleNavigate} />
          </div>

          <Toolbar
            {viewMode}
            onViewModeChange={(m) => (viewMode = m)}
            {searchQuery}
            onSearchChange={(q) => (searchQuery = q)}
            {sortBy}
            {sortDirection}
            {filterBy}
            onSortByChange={(v) => (sortBy = v)}
            onSortDirectionChange={(v) => (sortDirection = v)}
            onFilterByChange={(v) => (filterBy = v)}
            onRefresh={() => void loadSongs('refresh')}
            {isRefreshing}
          />
          {#if isLoading}
            <div class="border-border bg-card/30 text-muted-foreground border-b px-4 py-2 text-xs">
              Loading library...
            </div>
          {/if}
          {#if coverageWarning}
            <div class="border-border bg-card/30 text-muted-foreground border-b px-4 py-2 text-xs">
              {coverageWarning}
            </div>
          {/if}
          {#if apiError}
            <div class="border-border bg-card/30 text-muted-foreground border-b px-4 py-2 text-xs">
              {apiError}
            </div>
          {/if}

          <div class="min-h-0 flex-1">
            <FileGrid
              items={visibleItems}
              selectedId={selectedFileId}
              onSelect={handleFileSelect}
              onOpen={handleFileOpen}
              {viewMode}
              emptyMessage={emptyStateMessage}
            />
          </div>

          <div
            class="border-border bg-card/30 text-muted-foreground flex items-center justify-between border-t px-4 py-1.5 text-xs"
          >
            <span>
              {visibleItems.length} items ({libraryView})
            </span>
            {#if selectedFile}
              <span class="max-w-[200px] truncate">Selected: {selectedFile.name}</span>
            {/if}
          </div>
        </div>
      </Resizable.Pane>

      {#if showDetails && selectedFile?.type === 'audio'}
        <Resizable.Handle />
        <Resizable.Pane id="library-details-panel" order={3} defaultSize={30} minSize={25} maxSize={40}>
          <TrackDetails
            file={selectedFile}
            onClose={() => (showDetails = false)}
            onResetEnrichment={() => void loadSongs('refresh')}
          />
        </Resizable.Pane>
      {/if}
    </Resizable.PaneGroup>
  {:else}
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      <div class="border-border bg-card/30 flex items-center gap-2 border-b px-3 py-2">
        <Button
          variant="ghost"
          size="icon"
          class="size-8 shrink-0"
          onclick={() => (sidebarOpen = true)}
        >
          <Menu class="size-4" />
        </Button>
        <BreadcrumbNav path={breadcrumbPath} onNavigate={handleNavigate} />
      </div>

      <Toolbar
        {viewMode}
        onViewModeChange={(m) => (viewMode = m)}
        {searchQuery}
        onSearchChange={(q) => (searchQuery = q)}
        {sortBy}
        {sortDirection}
        {filterBy}
        onSortByChange={(v) => (sortBy = v)}
        onSortDirectionChange={(v) => (sortDirection = v)}
        onFilterByChange={(v) => (filterBy = v)}
        onRefresh={() => void loadSongs('refresh')}
        {isRefreshing}
      />
      {#if isLoading}
        <div class="border-border bg-card/30 text-muted-foreground border-b px-3 py-2 text-xs">
          Loading library...
        </div>
      {/if}
      {#if coverageWarning}
        <div class="border-border bg-card/30 text-muted-foreground border-b px-3 py-2 text-xs">
          {coverageWarning}
        </div>
      {/if}
      {#if apiError}
        <div class="border-border bg-card/30 text-muted-foreground border-b px-3 py-2 text-xs">
          {apiError}
        </div>
      {/if}

      <div class="min-h-0 flex-1">
        <FileGrid
          items={visibleItems}
          selectedId={selectedFileId}
          onSelect={handleFileSelect}
          onOpen={handleFileOpen}
          {viewMode}
          emptyMessage={emptyStateMessage}
        />
      </div>

      <div
        class="border-border bg-card/30 text-muted-foreground flex items-center justify-between border-t px-3 py-1.5 text-xs"
      >
        <span>
          {visibleItems.length} items ({libraryView})
        </span>
        {#if selectedFile}
          <span class="max-w-[150px] truncate">{selectedFile.name}</span>
        {/if}
      </div>
    </div>
  {/if}
{/if}

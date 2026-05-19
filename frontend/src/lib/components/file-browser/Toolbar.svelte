<script lang="ts">
  import { Grid3X3, List, Search, SlidersHorizontal, FolderInput, RefreshCw } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { cn } from '$lib/utils';

  export type LibrarySortBy = 'name' | 'dateModified' | 'size' | 'type';
  export type LibrarySortDirection = 'asc' | 'desc';
  export type LibraryFilterBy = 'all' | 'audio' | 'folders' | 'pendingEnrichment';

  type Props = {
    viewMode: 'grid' | 'list';
    onViewModeChange: (mode: 'grid' | 'list') => void;
    searchQuery: string;
    onSearchChange: (query: string) => void;
    sortBy: LibrarySortBy;
    sortDirection: LibrarySortDirection;
    filterBy: LibraryFilterBy;
    onSortByChange: (value: LibrarySortBy) => void;
    onSortDirectionChange: (value: LibrarySortDirection) => void;
    onFilterByChange: (value: LibraryFilterBy) => void;
    onRefresh: () => void;
    isRefreshing: boolean;
  };
  const {
    viewMode,
    onViewModeChange,
    searchQuery,
    onSearchChange,
    sortBy,
    sortDirection,
    filterBy,
    onSortByChange,
    onSortDirectionChange,
    onFilterByChange,
    onRefresh,
    isRefreshing
  }: Props = $props();

  const SEARCH_DEBOUNCE_MS = 150;

  // localQuery mirrors searchQuery initially, then drifts on user input until
  // debounce fires. The $effect resyncs if the parent overrides the query
  // (e.g. via a clear button).
  let localQuery = $state('');
  let debounceHandle: ReturnType<typeof setTimeout> | null = null;

  $effect(() => {
    localQuery = searchQuery;
  });

  function handleSearchInput(value: string) {
    localQuery = value;
    if (debounceHandle !== null) clearTimeout(debounceHandle);
    debounceHandle = setTimeout(() => onSearchChange(value), SEARCH_DEBOUNCE_MS);
  }
</script>

<div
  class="border-border bg-card/50 flex items-center gap-1.5 border-b px-2 py-2 sm:gap-2 sm:px-4"
>
  <div class="relative min-w-0 flex-1 sm:max-w-sm">
    <Search class="text-muted-foreground absolute top-1/2 left-2.5 size-4 -translate-y-1/2" />
    <Input
      type="search"
      placeholder="Search..."
      value={localQuery}
      oninput={(e) => handleSearchInput((e.target as HTMLInputElement).value)}
      class="bg-secondary h-8 border-0 pl-8"
    />
  </div>

  <div class="bg-secondary flex shrink-0 items-center gap-1 rounded-lg p-1">
    <Button
      variant="ghost"
      size="icon"
      class={cn('size-7', viewMode === 'grid' && 'bg-background shadow-sm')}
      onclick={() => onViewModeChange('grid')}
    >
      <Grid3X3 class="size-4" />
    </Button>
    <Button
      variant="ghost"
      size="icon"
      class={cn('size-7', viewMode === 'list' && 'bg-background shadow-sm')}
      onclick={() => onViewModeChange('list')}
    >
      <List class="size-4" />
    </Button>
  </div>

  <DropdownMenu.Root>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <Button {...props} variant="ghost" size="icon" class="size-8 shrink-0">
          <SlidersHorizontal class="size-4" />
        </Button>
      {/snippet}
    </DropdownMenu.Trigger>
    <DropdownMenu.Content align="end">
      <DropdownMenu.Label>Sort By</DropdownMenu.Label>
      <DropdownMenu.RadioGroup
        value={sortBy}
        onValueChange={(v) => onSortByChange(v as LibrarySortBy)}
      >
        <DropdownMenu.RadioItem value="name">Name</DropdownMenu.RadioItem>
        <DropdownMenu.RadioItem value="dateModified">Date Modified</DropdownMenu.RadioItem>
        <DropdownMenu.RadioItem value="size">Size</DropdownMenu.RadioItem>
        <DropdownMenu.RadioItem value="type">Type</DropdownMenu.RadioItem>
      </DropdownMenu.RadioGroup>
      <DropdownMenu.Separator />
      <DropdownMenu.Label>Sort Direction</DropdownMenu.Label>
      <DropdownMenu.RadioGroup
        value={sortDirection}
        onValueChange={(v) => onSortDirectionChange(v as LibrarySortDirection)}
      >
        <DropdownMenu.RadioItem value="asc">Ascending</DropdownMenu.RadioItem>
        <DropdownMenu.RadioItem value="desc">Descending</DropdownMenu.RadioItem>
      </DropdownMenu.RadioGroup>
      <DropdownMenu.Separator />
      <DropdownMenu.Label>Filter</DropdownMenu.Label>
      <DropdownMenu.RadioGroup
        value={filterBy}
        onValueChange={(v) => onFilterByChange(v as LibraryFilterBy)}
      >
        <DropdownMenu.RadioItem value="all">All Files</DropdownMenu.RadioItem>
        <DropdownMenu.RadioItem value="audio">Audio Only</DropdownMenu.RadioItem>
        <DropdownMenu.RadioItem value="folders">Folders Only</DropdownMenu.RadioItem>
        <DropdownMenu.RadioItem value="pendingEnrichment">Pending Enrichment</DropdownMenu.RadioItem>
      </DropdownMenu.RadioGroup>
    </DropdownMenu.Content>
  </DropdownMenu.Root>

  <div class="bg-border hidden h-6 w-px sm:block"></div>

  <Button variant="ghost" size="sm" class="hidden gap-2 sm:flex">
    <FolderInput class="size-4" />
    <span class="hidden md:inline">Import</span>
  </Button>

  <Button
    variant="ghost"
    size="icon"
    class="size-8 shrink-0"
    onclick={onRefresh}
    disabled={isRefreshing}
    aria-label="Refresh library"
  >
    <RefreshCw class={cn('size-4', isRefreshing && 'animate-spin')} />
  </Button>
</div>

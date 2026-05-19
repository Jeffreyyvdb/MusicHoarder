<script lang="ts">
  import type { FileItem } from '$lib/types';
  import { ChevronRight, Folder, FolderOpen } from '@lucide/svelte';
  import { cn } from '$lib/utils';
  import { getContext } from 'svelte';
  import { FOLDER_TREE_KEY, type FolderTreeContext } from './folder-tree-context';
  import Self from './FolderTreeBranch.svelte';

  type Props = {
    items: FileItem[];
    selectedId: string | null;
    onSelect: (item: FileItem) => void;
    level: number;
  };
  const { items, selectedId, onSelect, level }: Props = $props();

  const ctx = getContext<FolderTreeContext>(FOLDER_TREE_KEY);
  const folderItems = $derived(items.filter((item) => item.type === 'folder'));

  function handleFolderClick(item: FileItem) {
    onSelect(item);
    const hasChildren = item.children?.some((child) => child.type === 'folder');
    if (hasChildren && !ctx.isExpanded(item.id)) {
      ctx.setExpanded(item.id, true);
    }
  }

  function handleChevronClick(e: MouseEvent, item: FileItem) {
    e.stopPropagation();
    ctx.toggleExpanded(item.id);
  }

  function handleChevronKeydown(e: KeyboardEvent, item: FileItem) {
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      e.stopPropagation();
      ctx.toggleExpanded(item.id);
    }
  }
</script>

<div class="space-y-0.5">
  {#each folderItems as item (item.id)}
    {@const isExpanded = ctx.isExpanded(item.id)}
    {@const hasChildren = item.children?.some((c) => c.type === 'folder')}
    {@const isSelected = selectedId === item.id}
    <div>
      <button
        onclick={() => handleFolderClick(item)}
        class={cn(
          'flex w-full items-center gap-1 rounded-md px-2 py-1.5 text-sm transition-colors',
          'hover:bg-sidebar-accent hover:text-sidebar-accent-foreground',
          isSelected && 'bg-sidebar-accent text-sidebar-accent-foreground'
        )}
        style="padding-left: {level * 12 + 8}px"
      >
        {#if hasChildren}
          <span
            class="flex size-4 shrink-0 cursor-pointer items-center justify-center"
            role="button"
            tabindex="0"
            aria-label={isExpanded ? 'Collapse folder' : 'Expand folder'}
            onclick={(e) => handleChevronClick(e, item)}
            onkeydown={(e) => handleChevronKeydown(e, item)}
          >
            <ChevronRight
              class={cn(
                'text-muted-foreground hover:text-foreground size-3 transition-transform',
                isExpanded && 'rotate-90'
              )}
            />
          </span>
        {:else}
          <span class="flex size-4 shrink-0 items-center justify-center"></span>
        {/if}
        {#if isExpanded}
          <FolderOpen class="text-primary size-4 shrink-0" />
        {:else}
          <Folder class="text-primary size-4 shrink-0" />
        {/if}
        <span class="truncate">{item.name}</span>
      </button>

      {#if isExpanded && hasChildren && item.children}
        <Self items={item.children} {selectedId} {onSelect} level={level + 1} />
      {/if}
    </div>
  {/each}
</div>

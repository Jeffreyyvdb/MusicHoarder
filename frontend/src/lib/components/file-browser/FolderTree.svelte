<script lang="ts">
  import type { FileItem } from '$lib/types';
  import { setContext } from 'svelte';
  import { FOLDER_TREE_KEY, type FolderTreeContext } from './folder-tree-context';
  import FolderTreeBranch from './FolderTreeBranch.svelte';

  type Props = {
    items: FileItem[];
    selectedId: string | null;
    onSelect: (item: FileItem) => void;
    expandedIds?: Set<string>;
    onExpandedChange?: (ids: Set<string>) => void;
  };

  let {
    items,
    selectedId,
    onSelect,
    expandedIds = $bindable(undefined),
    onExpandedChange
  }: Props = $props();

  // Default-expand the root-level folders. We snapshot only on first mount
  // (items can change as new songs load, but we don't want to re-expand
  // everything on every refresh).
  let internalIds = $state(new Set<string>());
  let initialized = false;
  $effect(() => {
    if (initialized) return;
    initialized = true;
    const ids = new Set<string>();
    for (const i of items) if (i.type === 'folder') ids.add(i.id);
    internalIds = ids;
  });

  function getIds(): Set<string> {
    return expandedIds ?? internalIds;
  }

  function updateIds(next: Set<string>) {
    if (onExpandedChange) onExpandedChange(next);
    else internalIds = next;
  }

  const ctx: FolderTreeContext = {
    isExpanded: (id: string) => getIds().has(id),
    toggleExpanded: (id: string) => {
      const next = new Set(getIds());
      if (next.has(id)) next.delete(id);
      else next.add(id);
      updateIds(next);
    },
    setExpanded: (id: string, expanded: boolean) => {
      const next = new Set(getIds());
      if (expanded) next.add(id);
      else next.delete(id);
      updateIds(next);
    }
  };

  setContext<FolderTreeContext>(FOLDER_TREE_KEY, ctx);
</script>

<FolderTreeBranch {items} {selectedId} {onSelect} level={0} />

<script lang="ts">
  import type { FileItem } from '$lib/types';
  import { ChevronRight, Home } from '@lucide/svelte';

  type Props = {
    path: FileItem[];
    onNavigate: (item: FileItem) => void;
  };
  const { path, onNavigate }: Props = $props();

  // On mobile, collapse to first + last 2 with an ellipsis.
  const shouldCollapse = $derived(path.length > 3);
  const visiblePath = $derived(shouldCollapse ? [path[0], ...path.slice(-2)] : path);
</script>

<nav class="scrollbar-none flex items-center gap-0.5 overflow-x-auto text-sm sm:gap-1">
  {#each visiblePath as item, index (item.id)}
    {@const isFirst = index === 0}
    {@const isLast = index === visiblePath.length - 1}
    {@const showEllipsis = shouldCollapse && index === 1}
    <div class="flex shrink-0 items-center gap-0.5 sm:gap-1">
      {#if index > 0}
        {#if showEllipsis}
          <ChevronRight class="text-muted-foreground size-3.5 sm:size-4" />
          <span class="text-muted-foreground px-1">...</span>
        {/if}
        <ChevronRight class="text-muted-foreground size-3.5 sm:size-4" />
      {/if}
      <button
        onclick={() => onNavigate(item)}
        class="hover:bg-secondary flex items-center gap-1 rounded px-1.5 py-1 transition-colors sm:gap-1.5 sm:px-2"
      >
        {#if isFirst}<Home class="size-3.5" />{/if}
        <span
          class="max-w-[80px] truncate sm:max-w-[150px] {isLast
            ? 'font-medium'
            : 'text-muted-foreground'}"
        >
          {item.name}
        </span>
      </button>
    </div>
  {/each}
</nav>

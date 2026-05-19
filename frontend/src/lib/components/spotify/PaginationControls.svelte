<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { ChevronLeft, ChevronRight } from '@lucide/svelte';

  type Props = {
    offset: number;
    limit: number;
    total: number;
    onPageChange: (newOffset: number) => void;
    isLoading: boolean;
  };
  const { offset, limit, total, onPageChange, isLoading }: Props = $props();

  const currentPage = $derived(Math.floor(offset / limit) + 1);
  const totalPages = $derived(Math.ceil(total / limit));
</script>

{#if totalPages > 1}
  <div class="flex items-center justify-center gap-2 py-4">
    <Button
      variant="outline"
      size="sm"
      disabled={offset === 0 || isLoading}
      onclick={() => onPageChange(Math.max(0, offset - limit))}
    >
      <ChevronLeft class="size-4" />
    </Button>
    <span class="text-muted-foreground px-2 text-sm">
      Page {currentPage} of {totalPages}
    </span>
    <Button
      variant="outline"
      size="sm"
      disabled={offset + limit >= total || isLoading}
      onclick={() => onPageChange(offset + limit)}
    >
      <ChevronRight class="size-4" />
    </Button>
  </div>
{/if}

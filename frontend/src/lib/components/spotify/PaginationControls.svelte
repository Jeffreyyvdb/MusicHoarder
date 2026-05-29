<script lang="ts">
  import * as Pagination from '$lib/components/ui/pagination/index.js';

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
  <div class="flex items-center justify-center py-4">
    <Pagination.Root
      count={total}
      perPage={limit}
      page={currentPage}
      onPageChange={(p) => onPageChange((p - 1) * limit)}
    >
      {#snippet children({ pages, currentPage })}
        <Pagination.Content>
          <Pagination.Item>
            <Pagination.PrevButton disabled={isLoading} />
          </Pagination.Item>
          {#each pages as page (page.key)}
            {#if page.type === 'ellipsis'}
              <Pagination.Item>
                <Pagination.Ellipsis />
              </Pagination.Item>
            {:else}
              <Pagination.Item>
                <Pagination.Link
                  {page}
                  isActive={currentPage === page.value}
                  disabled={isLoading}
                >
                  {page.value}
                </Pagination.Link>
              </Pagination.Item>
            {/if}
          {/each}
          <Pagination.Item>
            <Pagination.NextButton disabled={isLoading} />
          </Pagination.Item>
        </Pagination.Content>
      {/snippet}
    </Pagination.Root>
  </div>
{/if}

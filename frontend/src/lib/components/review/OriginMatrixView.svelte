<script lang="ts">
  import type { OriginMatrix } from '$lib/review-helpers';
  import { cn } from '$lib/utils';
  import * as Table from '$lib/components/ui/table/index.js';

  type Props = { matrix: OriginMatrix };
  const { matrix }: Props = $props();
</script>

{#if matrix.columns.length <= 1}
  <div class="text-muted-foreground py-8 text-center text-sm">
    No provider proposed any values for this file.
  </div>
{:else}
  <div class="border-border overflow-x-auto rounded-lg border">
    <Table.Root class="w-full border-collapse text-left">
      <Table.Header>
        <Table.Row class="bg-surface-sunken/60">
          <Table.Head
            class="border-border text-muted-foreground border-r border-b px-3 py-2.5 align-bottom"
          >
            <div class="font-mono text-[10px] tracking-[0.06em]">FIELD ↓</div>
            <div class="font-mono text-[10px] tracking-[0.06em]">PROVIDER →</div>
          </Table.Head>
          {#each matrix.columns as col (col.key)}
            <Table.Head class="border-border min-w-[140px] border-b px-3 py-2.5">
              <div class="flex items-center gap-1.5">
                <span class="size-2 rounded-full" style="background: {col.color}"></span>
                <span class="text-[12px] font-semibold">{col.label}</span>
              </div>
            </Table.Head>
          {/each}
        </Table.Row>
      </Table.Header>
      <Table.Body>
        {#each matrix.rows as row (row.field)}
          <Table.Row class="border-border border-b last:border-b-0">
            <Table.Head
              scope="row"
              class="border-border bg-surface-sunken/40 border-r px-3 py-2.5 align-middle font-medium"
            >
              <span class="text-[13px]">{row.label}</span>
              {#if row.missing}
                <span class="text-muted-foreground/60 ml-1.5 font-mono text-[10px] lowercase">missing</span>
              {/if}
            </Table.Head>
            {#each row.cells as cell, ci (matrix.columns[ci].key)}
              <Table.Cell class="px-2 py-1.5 align-middle whitespace-normal">
                {#if cell.value == null}
                  <span class="text-muted-foreground/30 block text-center">·</span>
                {:else}
                  <div
                    class={cn(
                      'relative rounded-md px-2.5 py-2 text-[13px] break-words',
                      cell.winning && 'border-primary bg-primary/10 border'
                    )}
                  >
                    {#if cell.pct != null}
                      <span class="text-primary absolute top-1 right-1.5 font-mono text-[10px]">{cell.pct}%</span>
                    {/if}
                    <span class={cn(cell.value.length > 18 && 'font-mono text-[11px]')}>{cell.value}</span>
                  </div>
                {/if}
              </Table.Cell>
            {/each}
          </Table.Row>
        {/each}
      </Table.Body>
    </Table.Root>
  </div>
{/if}

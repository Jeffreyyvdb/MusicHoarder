<script lang="ts">
  import type { OriginMatrix } from '$lib/review-helpers';
  import { cn } from '$lib/utils';

  type Props = { matrix: OriginMatrix };
  const { matrix }: Props = $props();
</script>

{#if matrix.columns.length <= 1}
  <div class="text-muted-foreground py-8 text-center text-sm">
    No provider proposed any values for this file.
  </div>
{:else}
  <div class="border-border overflow-x-auto rounded-lg border">
    <table class="w-full border-collapse text-left">
      <thead>
        <tr class="bg-surface-sunken/60">
          <th class="border-border text-muted-foreground border-r border-b px-3 py-2.5 align-bottom">
            <div class="font-mono text-[10px] tracking-[0.06em]">FIELD ↓</div>
            <div class="font-mono text-[10px] tracking-[0.06em]">PROVIDER →</div>
          </th>
          {#each matrix.columns as col (col.key)}
            <th class="border-border min-w-[140px] border-b px-3 py-2.5">
              <div class="flex items-center gap-1.5">
                <span class="size-2 rounded-full" style="background: {col.color}"></span>
                <span class="text-[12px] font-semibold">{col.label}</span>
              </div>
            </th>
          {/each}
        </tr>
      </thead>
      <tbody>
        {#each matrix.rows as row (row.field)}
          <tr class="border-border border-b last:border-b-0">
            <th
              scope="row"
              class="border-border bg-surface-sunken/40 border-r px-3 py-2.5 align-middle font-medium"
            >
              <span class="text-[13px]">{row.label}</span>
              {#if row.missing}
                <span class="text-muted-foreground/60 ml-1.5 font-mono text-[10px] lowercase">missing</span>
              {/if}
            </th>
            {#each row.cells as cell, ci (matrix.columns[ci].key)}
              <td class="px-2 py-1.5 align-middle">
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
              </td>
            {/each}
          </tr>
        {/each}
      </tbody>
    </table>
  </div>
{/if}

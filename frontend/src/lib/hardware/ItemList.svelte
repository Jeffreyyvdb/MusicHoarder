<script lang="ts" module>
  import type { Snippet } from 'svelte';

  export interface ItemListColumn<T> {
    key: string;
    label: string;
    cell: Snippet<[T, number]>;
    align?: 'start' | 'end';
    width?: string;
  }
</script>

<script lang="ts" generics="T">
  // A list laid out as a table: rows on engraved rules in a recessed tray, columns on one grid. The
  // current row can carry a progress, 0 to 1, which fills it from the left with a playhead, so the
  // row is its own progress bar. It is a single-select UI SDK listbox: arrows, Home, End and typing
  // move through it; Enter or a click chooses a row.
  import { Listbox, ListboxItem } from '@ui-sdk/svelte/listbox';

  let {
    label,
    rows,
    columns,
    rowKey,
    value,
    onValueChange,
    progress
  }: {
    label: string;
    rows: readonly T[];
    columns: ItemListColumn<T>[];
    rowKey: (row: T, index: number) => string;
    value: string | null;
    onValueChange: (value: string) => void;
    progress?: number;
  } = $props();

  const template = $derived(columns.map((c) => c.width ?? 'auto').join(' '));
</script>

<div data-kit="item-list" style:--columns={template}>
  <div data-kit="item-list-head" aria-hidden="true">
    {#each columns as column (column.key)}
      <span data-align={column.align}>{column.label}</span>
    {/each}
  </div>
  <Listbox
    aria-label={label}
    data-kit="item-list-body"
    activation="manual"
    {value}
    onValueChange={(next: unknown) => next != null && onValueChange(String(next))}
  >
    {#each rows as row, index (rowKey(row, index))}
      {@const key = rowKey(row, index)}
      {@const current = key === value && progress !== undefined}
      <ListboxItem
        value={key}
        data-kit="item-list-row"
        style={current ? `--progress: ${progress}` : undefined}
        data-progress={current ? '' : undefined}
      >
        {#each columns as column (column.key)}
          <span data-align={column.align}>{@render column.cell(row, index)}</span>
        {/each}
      </ListboxItem>
    {/each}
  </Listbox>
</div>

<script lang="ts">
  import { ArrowRight, Plus } from '@lucide/svelte';
  import { Input } from '$lib/components/ui/input';
  import * as Table from '$lib/components/ui/table/index.js';
  import type { BeforeAfterRow, EditableFieldKey } from '$lib/review-helpers';
  import { cn } from '$lib/utils';

  type Props = {
    rows: BeforeAfterRow[];
    /** Current FINAL value per field (edited state lives in the page). */
    values: Record<string, string>;
    readonly?: boolean;
    fromFolder: string;
    fileName: string;
    fromMeta: string;
    destinationPath: string;
    destFormat: string;
    onset: (key: EditableFieldKey, value: string) => void;
    oncopy: (key: EditableFieldKey, embedded: string) => void;
  };

  const { rows, values, readonly = false, fromFolder, fileName, fromMeta, destinationPath, destFormat, onset, oncopy }: Props = $props();

  // Split the destination into a directory part + final filename for emphasis.
  const destDir = $derived(destinationPath.slice(0, destinationPath.lastIndexOf('/') + 1));
  const destFile = $derived(destinationPath.slice(destinationPath.lastIndexOf('/') + 1));
</script>

<!-- From → will write to (paths stay monospace — genuinely technical strings). -->
<div class="grid items-stretch gap-3 sm:grid-cols-[1fr_auto_1fr]">
  <div class="border-border bg-surface-sunken/40 rounded-lg border p-4">
    <div class="text-muted-foreground text-[11px] font-medium">From</div>
    <div class="text-muted-foreground mt-2 font-mono text-[11px] break-all">{fromFolder}/</div>
    <div class="mt-0.5 font-mono text-[13px] font-semibold break-all">{fileName}</div>
    <div class="text-muted-foreground mt-2 text-[11px]">{fromMeta}</div>
  </div>
  <div class="text-muted-foreground hidden items-center justify-center sm:flex">
    <ArrowRight class="size-5" />
  </div>
  <div class="border-primary bg-primary/10 rounded-lg border p-4">
    <div class="text-primary text-[11px] font-medium">Will write to</div>
    <div class="text-muted-foreground mt-2 font-mono text-[11px] break-all">{destDir}</div>
    <div class="text-primary mt-0.5 font-mono text-[13px] font-semibold break-all">{destFile}</div>
    <div class="text-muted-foreground mt-2 text-[11px]">{destFormat}</div>
  </div>
</div>

<!-- FIELD / EMBEDDED / FINAL / SOURCE — the Table primitive already wraps in an
     overflow-x-auto container; the min-w forces a horizontal scroll on phones
     instead of crushing the editable FINAL column. -->
<div class="border-border mt-4 overflow-hidden rounded-lg border">
  <Table.Root class="min-w-[34rem]">
    <Table.Header>
      <Table.Row
        class="bg-surface-sunken/60 text-muted-foreground hover:bg-surface-sunken/60 text-[11px]"
      >
        <Table.Head class="text-muted-foreground h-auto w-20 px-4 py-2.5 font-normal">Field</Table.Head>
        <Table.Head class="text-muted-foreground h-auto px-4 py-2.5 font-normal">Embedded</Table.Head>
        <Table.Head class="text-muted-foreground h-auto px-4 py-2.5 font-normal"
          >{readonly ? 'Final' : 'Final · edit if needed'}</Table.Head
        >
        <Table.Head class="text-muted-foreground h-auto w-px px-4 py-2.5 text-right font-normal">Source</Table.Head>
      </Table.Row>
    </Table.Header>
    <Table.Body>
      {#each rows as row (row.key)}
        <Table.Row class="hover:bg-transparent">
          <Table.Cell class="w-20 px-4 py-2.5 text-[13px] font-medium">{row.label}</Table.Cell>
          <Table.Cell class="px-4 py-2.5">
            <div class="flex min-w-0 items-center gap-1.5">
              <span class={cn('truncate text-[13px]', !row.embedded && 'text-muted-foreground/60 italic')}>
                {row.embedded || '(empty)'}
              </span>
              {#if !readonly && row.embedded && row.embedded !== (values[row.key] ?? '')}
                <button
                  type="button"
                  title="Copy embedded value into final"
                  class="text-muted-foreground hover:text-primary border-border hover:border-primary grid size-5 shrink-0 place-items-center rounded border"
                  onclick={() => oncopy(row.key, row.embedded)}
                >
                  <Plus class="size-3" />
                </button>
              {/if}
            </div>
          </Table.Cell>
          <Table.Cell class="px-4 py-2.5">
            <div class="min-w-0">
              {#if readonly}
                <span class="text-[13px] break-words whitespace-normal">{values[row.key] || '—'}</span>
              {:else}
                <Input
                  value={values[row.key] ?? ''}
                  type={row.key === 'year' || row.key === 'trackNumber' ? 'number' : 'text'}
                  placeholder={`enter ${row.label.toLowerCase()}`}
                  class="h-8"
                  oninput={(e) => onset(row.key, (e.target as HTMLInputElement).value)}
                />
              {/if}
            </div>
          </Table.Cell>
          <Table.Cell class="px-4 py-2.5 text-right">
            <div class="flex justify-end">
              {#if row.sourceLabel}
                <span class="text-muted-foreground inline-flex items-center gap-1.5 text-[11px] whitespace-nowrap">
                  <span class="size-1.5 rounded-full" style="background: {row.sourceColor}"></span>
                  {row.sourceLabel}{#if row.sourcePct != null}&nbsp;<span class="tabular-nums">{row.sourcePct}%</span>{/if}
                </span>
              {:else}
                <span class="text-muted-foreground/40">·</span>
              {/if}
            </div>
          </Table.Cell>
        </Table.Row>
      {/each}
    </Table.Body>
  </Table.Root>
</div>

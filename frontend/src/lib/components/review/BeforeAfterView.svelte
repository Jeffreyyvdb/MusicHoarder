<script lang="ts">
  import { ArrowRight, Plus } from '@lucide/svelte';
  import { Input } from '$lib/components/ui/input';
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

<!-- FROM → WILL WRITE TO -->
<div class="grid items-stretch gap-3 sm:grid-cols-[1fr_auto_1fr]">
  <div class="border-border bg-surface-sunken/40 rounded-lg border p-4">
    <div class="text-muted-foreground font-mono text-[10px] tracking-[0.08em]">FROM</div>
    <div class="text-muted-foreground mt-2 font-mono text-[11px] break-all">{fromFolder}/</div>
    <div class="mt-0.5 font-mono text-[13px] font-semibold break-all">{fileName}</div>
    <div class="text-muted-foreground mt-2 font-mono text-[11px]">{fromMeta}</div>
  </div>
  <div class="text-muted-foreground hidden items-center justify-center sm:flex">
    <ArrowRight class="size-5" />
  </div>
  <div class="border-primary bg-primary/10 rounded-lg border p-4">
    <div class="text-primary font-mono text-[10px] tracking-[0.08em]">WILL WRITE TO</div>
    <div class="text-muted-foreground mt-2 font-mono text-[11px] break-all">{destDir}</div>
    <div class="text-primary mt-0.5 font-mono text-[13px] font-semibold break-all">{destFile}</div>
    <div class="text-muted-foreground mt-2 font-mono text-[11px]">{destFormat}</div>
  </div>
</div>

<!-- FIELD / EMBEDDED / FINAL / SOURCE -->
<div class="border-border mt-4 overflow-hidden rounded-lg border">
  <div
    class="bg-surface-sunken/60 text-muted-foreground border-border grid grid-cols-[80px_1fr_1fr_auto] gap-3 border-b px-4 py-2.5 font-mono text-[10px] tracking-[0.06em]"
  >
    <div>FIELD</div>
    <div>EMBEDDED</div>
    <div>FINAL{#if !readonly} · EDIT IF NEEDED{/if}</div>
    <div class="text-right">SOURCE</div>
  </div>
  {#each rows as row (row.key)}
    <div class="border-border grid grid-cols-[80px_1fr_1fr_auto] items-center gap-3 border-b px-4 py-2.5 last:border-b-0">
      <div class="text-[13px] font-medium">{row.label}</div>
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
      <div class="min-w-0">
        {#if readonly}
          <span class="text-[13px] break-words">{values[row.key] || '—'}</span>
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
      <div class="flex justify-end">
        {#if row.sourceLabel}
          <span class="border-border inline-flex items-center gap-1.5 rounded-md border px-1.5 py-0.5 whitespace-nowrap">
            <span class="size-1.5 rounded-full" style="background: {row.sourceColor}"></span>
            <span class="text-[10.5px]">{row.sourceLabel}</span>
            {#if row.sourcePct != null}
              <span class="text-muted-foreground border-border ml-0.5 border-l pl-1.5 font-mono text-[10.5px]"
                >{row.sourcePct}</span
              >
            {/if}
          </span>
        {:else}
          <span class="text-muted-foreground/40">·</span>
        {/if}
      </div>
    </div>
  {/each}
</div>

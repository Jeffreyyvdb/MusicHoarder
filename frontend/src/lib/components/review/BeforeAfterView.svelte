<script lang="ts">
  import { ArrowRight, Plus } from '@lucide/svelte';
  import { Input } from '$lib/components/ui/input';
  import * as Table from '$lib/components/ui/table/index.js';
  import * as GroupedList from '$lib/components/ui/grouped-list';
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
    /**
     * The narrow form's sections bring their own 16px side margins on a phone (a page's grouped
     * list). Off when the host column is already padded — the readonly dossier in QualityDetail,
     * whose candidate cards above run edge to edge of that column. Defaults to off in readonly.
     */
    inset?: boolean;
    /**
     * The narrow form's cell fill. `bg-card` suits a grouped background; a plain host (the desktop
     * detail pane, white in light mode) passes `bg-muted` so the cells still read as a group.
     */
    cellClass?: string;
  };

  const {
    rows,
    values,
    readonly = false,
    fromFolder,
    fileName,
    fromMeta,
    destinationPath,
    destFormat,
    onset,
    oncopy,
    inset = !readonly,
    cellClass
  }: Props = $props();

  const uid = $props.id();

  // Split the destination into a directory part + final filename for emphasis.
  const destDir = $derived(destinationPath.slice(0, destinationPath.lastIndexOf('/') + 1));
  const destFile = $derived(destinationPath.slice(destinationPath.lastIndexOf('/') + 1));

  function numeric(key: EditableFieldKey): boolean {
    return key === 'year' || key === 'trackNumber';
  }
  function canUseEmbedded(row: BeforeAfterRow): boolean {
    return !readonly && !!row.embedded && row.embedded !== (values[row.key] ?? '');
  }

  // The keyboard's return key walks the form (its label says "next"), and closes it on the last
  // field ("done") — a key that is labelled but does nothing reads as broken.
  function onFieldKeydown(e: KeyboardEvent, index: number) {
    if (e.key !== 'Enter' || e.isComposing) return;
    e.preventDefault();
    const next = rows[index + 1];
    if (next) document.getElementById(`${uid}-${next.key}`)?.focus();
    else (e.currentTarget as HTMLInputElement).blur();
  }
</script>

{#snippet pathRow(label: string, dir: string, file: string, meta: string)}
  <div
    class="after:bg-separator relative px-4 py-2.5 after:absolute after:right-0 after:bottom-0 after:left-4 after:h-(--hairline) last:after:hidden"
  >
    <div class="text-footnote text-muted-foreground">{label}</div>
    <!-- The folder is cut at its START (the part nearest the file stays), the file name is
         never cut: together a middle truncation, done with an RTL box around an isolated
         LTR run. -->
    <div
      class="text-footnote text-muted-foreground mt-0.5 truncate text-left font-mono [direction:rtl]"
    >
      <bdi>{dir}</bdi>
    </div>
    <div class="text-subheadline font-mono font-medium break-all">{file}</div>
    <div class="text-footnote text-muted-foreground mt-0.5">{meta}</div>
  </div>
{/snippet}

<!--
  Two layouts, picked by the width this view is given rather than the viewport's: the 4-column
  table needs ~34rem, which a phone never has and an iPad-portrait detail pane (820 − 320 list)
  doesn't either. Narrower than that it becomes the iOS grouped form — one row per field with a
  full-width field and the embedded value as a footnote — instead of a table that scrolls
  sideways inside a pane that already scrolls vertically. Both are rendered; the container query
  shows one (a display:none field is neither visible nor focusable).
-->
<div class="@container">
  <!-- ── Wide: path cards + the field table ───────────────────────────────────────────── -->
  <div class="hidden @min-[34rem]:block">
    <!-- From → will write to (paths stay monospace — genuinely technical strings). -->
    <div class="grid grid-cols-[1fr_auto_1fr] items-stretch gap-3">
      <div class="bg-muted rounded-lg p-4">
        <div class="text-muted-foreground text-[11px] font-medium">From</div>
        <div class="text-muted-foreground mt-2 font-mono text-[11px] break-all">{fromFolder}/</div>
        <div class="mt-0.5 font-mono text-[13px] font-semibold break-all">{fileName}</div>
        <div class="text-muted-foreground mt-2 text-[11px]">{fromMeta}</div>
      </div>
      <div class="text-muted-foreground flex items-center justify-center">
        <ArrowRight class="size-5" aria-hidden="true" />
      </div>
      <div class="border-primary bg-primary/10 rounded-lg border p-4">
        <div class="text-primary text-[11px] font-medium">Will write to</div>
        <div class="text-muted-foreground mt-2 font-mono text-[11px] break-all">{destDir}</div>
        <div class="text-primary mt-0.5 font-mono text-[13px] font-semibold break-all">
          {destFile}
        </div>
        <div class="text-muted-foreground mt-2 text-[11px]">{destFormat}</div>
      </div>
    </div>

    <!-- FIELD / EMBEDDED / FINAL / SOURCE. -->
    <div class="border-border mt-4 overflow-hidden rounded-lg border">
      <Table.Root>
        <Table.Header>
          <Table.Row class="bg-muted text-muted-foreground hover:bg-muted text-[11px]">
            <Table.Head class="text-muted-foreground h-auto w-20 px-4 py-2.5 font-normal"
              >Field</Table.Head
            >
            <Table.Head class="text-muted-foreground h-auto px-4 py-2.5 font-normal"
              >Embedded</Table.Head
            >
            <Table.Head class="text-muted-foreground h-auto px-4 py-2.5 font-normal"
              >{readonly ? 'Final' : 'Final · edit if needed'}</Table.Head
            >
            <Table.Head class="text-muted-foreground h-auto w-px px-4 py-2.5 text-right font-normal"
              >Source</Table.Head
            >
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each rows as row (row.key)}
            <Table.Row class="hover:bg-transparent">
              <Table.Cell class="w-20 px-4 py-2.5 text-[13px] font-medium">{row.label}</Table.Cell>
              <Table.Cell class="px-4 py-2.5">
                <div class="flex min-w-0 items-center gap-1.5">
                  <span
                    class={cn(
                      'truncate text-[13px]',
                      !row.embedded && 'text-muted-foreground-dim italic'
                    )}
                  >
                    {row.embedded || '(empty)'}
                  </span>
                  {#if canUseEmbedded(row)}
                    <!-- 20px glyph in a dense table row; the hit area grows to 44px on touch. -->
                    <button
                      type="button"
                      title="Copy embedded value into final"
                      aria-label={`Use the embedded ${row.label.toLowerCase()}`}
                      class="text-muted-foreground hover:text-primary border-border hover:border-primary relative grid size-5 shrink-0 place-items-center rounded border pointer-coarse:after:absolute pointer-coarse:after:-inset-3"
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
                    <span class="text-[13px] break-words whitespace-normal"
                      >{values[row.key] || '—'}</span
                    >
                  {:else}
                    <Input
                      value={values[row.key] ?? ''}
                      type={numeric(row.key) ? 'number' : 'text'}
                      autocapitalize="off"
                      autocorrect="off"
                      autocomplete="off"
                      spellcheck={false}
                      aria-label={row.label}
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
                    <span
                      class="text-muted-foreground inline-flex items-center gap-1.5 text-[11px] whitespace-nowrap"
                    >
                      <span
                        class="size-1.5 rounded-full"
                        style="background: {row.sourceColor}"
                        aria-hidden="true"
                      ></span>
                      {row.sourceLabel}{#if row.sourcePct != null}&nbsp;<span class="tabular-nums"
                          >{row.sourcePct}%</span
                        >{/if}
                    </span>
                  {:else}
                    <span class="text-muted-foreground-dim" aria-label="No source">·</span>
                  {/if}
                </div>
              </Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    </div>
  </div>

  <!-- ── Narrow: the grouped form ─────────────────────────────────────────────────────── -->
  <div class="flex flex-col gap-7 @min-[34rem]:hidden">
    <GroupedList.Section
      class={cn(!inset && 'mx-0')}
      contentClass={cellClass}
      header={readonly ? 'Tags' : 'Tags to write'}
      footer={readonly ? undefined : 'Each field starts from the picked candidate.'}
    >
      {#each rows as row, i (row.key)}
        <div
          class="after:bg-separator relative px-4 py-2.5 after:absolute after:right-0 after:bottom-0 after:left-4 after:h-(--hairline) last:after:hidden"
        >
          {#if readonly}
            <div class="text-footnote text-muted-foreground">{row.label}</div>
            <div class="text-body mt-0.5 break-words">
              {values[row.key] || '—'}
            </div>
          {:else}
            <label for="{uid}-{row.key}" class="text-footnote text-muted-foreground block"
              >{row.label}</label
            >
            <!-- Names are written as typed: iOS must not autocorrect or capitalise them ("jay-z"
                 must not arrive as "Jay-z"). -->
            <Input
              id="{uid}-{row.key}"
              value={values[row.key] ?? ''}
              type={numeric(row.key) ? 'number' : 'text'}
              inputmode={numeric(row.key) ? 'numeric' : undefined}
              autocapitalize="off"
              autocorrect="off"
              autocomplete="off"
              spellcheck={false}
              enterkeyhint={i < rows.length - 1 ? 'next' : 'done'}
              placeholder={`Enter ${row.label.toLowerCase()}`}
              class="mt-1"
              oninput={(e) => onset(row.key, (e.target as HTMLInputElement).value)}
              onkeydown={(e) => onFieldKeydown(e, i)}
            />
          {/if}
          <div class="text-footnote text-muted-foreground mt-1.5 flex min-w-0 items-center gap-x-2">
            <span class="min-w-0 truncate">
              Embedded: <span class={cn(!row.embedded && 'italic')}>{row.embedded || 'empty'}</span>
            </span>
            {#if canUseEmbedded(row)}
              <!-- The 44pt hit area grows down and sideways, never up: 6px up is exactly the gap
                   to the field above, so the field keeps all of its own 44pt (it lost 6 to an
                   even inset). 6 + the 18px line + 20px down (this cell's and the next one's
                   padding, stopping short of its label) = 44; z-10 lifts it over the next cell,
                   which is painted later. -->
              <button
                type="button"
                aria-label={`Use the embedded ${row.label.toLowerCase()}`}
                class="text-primary relative z-10 shrink-0 font-medium outline-none after:absolute after:-inset-x-2 after:-top-1.5 after:-bottom-5 focus-visible:underline"
                onclick={() => oncopy(row.key, row.embedded)}>Use</button
              >
            {/if}
            {#if row.sourceLabel}
              <span class="ml-auto flex shrink-0 items-center gap-1.5">
                <span
                  class="size-2 rounded-full"
                  style="background: {row.sourceColor}"
                  aria-hidden="true"
                ></span>
                {row.sourceLabel}{#if row.sourcePct != null}&nbsp;<span class="tabular-nums"
                    >{row.sourcePct}%</span
                  >{/if}
              </span>
            {/if}
          </div>
        </div>
      {/each}
    </GroupedList.Section>

    <GroupedList.Section class={cn(!inset && 'mx-0')} contentClass={cellClass} header="File">
      {@render pathRow('From', `${fromFolder}/`, fileName, fromMeta)}
      {@render pathRow(readonly ? 'Writes to' : 'Will write to', destDir, destFile, destFormat)}
    </GroupedList.Section>
  </div>
</div>

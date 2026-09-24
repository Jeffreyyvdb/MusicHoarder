<script lang="ts">
  import { Check } from '@lucide/svelte';
  import { Spinner } from '$lib/components/ui/spinner/index.js';
  import { providerColor, type ReviewCandidate } from '$lib/review-helpers';
  import { cn } from '$lib/utils';
  import { radioGroup, radioTabIndex } from './radio-group';

  type Props = {
    candidates: ReviewCandidate[];
    pickedKey: string | null;
    loading?: boolean;
    onpick: (c: ReviewCandidate) => void;
    /** Single-column cards (a narrow pane). Defaults to a responsive 2-col grid. */
    single?: boolean;
    /** Audit/overview mode: cards are non-interactive. */
    readonly?: boolean;
    /**
     * `cards` (default): the desktop card grid. `rows`: iOS checkmark rows for a phone's grouped
     * form — the caller wraps them in a GroupedList.Section, so the rows bring no container.
     */
    layout?: 'cards' | 'rows';
  };

  const {
    candidates,
    pickedKey,
    loading = false,
    onpick,
    single = false,
    readonly = false,
    layout = 'cards'
  }: Props = $props();

  function pctOf(c: ReviewCandidate): number | null {
    return c.score != null ? Math.round(c.score * 100) : null;
  }

  // One pick among the candidates: a radio group (one Tab stop, arrows move and pick, VoiceOver
  // says "2 of 4, checked"), not a row of toggle buttons. Readonly (the quality dossier) is a
  // plain list with nothing to pick.
  const pickedIndex = $derived(candidates.findIndex((c) => c.key === pickedKey));
  function radioProps(index: number, picked: boolean) {
    if (readonly) return {};
    return {
      role: 'radio' as const,
      'aria-checked': picked,
      tabindex: radioTabIndex(index, pickedIndex)
    };
  }
</script>

{#if layout === 'rows'}
  <!-- A single-choice list: the pick is the trailing checkmark, the score sits beside it. -->
  {#if loading && candidates.length === 0}
    <div class="text-body text-muted-foreground flex min-h-11 items-center gap-2 px-4 py-2">
      <Spinner class="size-4" /> Loading candidates…
    </div>
  {:else if candidates.length === 0}
    <div class="text-subheadline text-muted-foreground px-4 py-3">
      No fingerprint matches. Edit the tags below, or skip this file.
    </div>
  {:else}
    <div
      role={readonly ? undefined : 'radiogroup'}
      aria-label={readonly ? undefined : 'Candidate matches'}
      use:radioGroup
    >
      {#each candidates as c, i (c.key)}
        {@const picked = pickedKey === c.key}
        {@const pct = pctOf(c)}
        <button
          type="button"
          onclick={() => onpick(c)}
          disabled={readonly}
          {...radioProps(i, picked)}
          class="group/cand after:bg-separator focus-visible:ring-ring/50 enabled:active:bg-accent relative grid min-h-11 w-full grid-cols-[minmax(0,1fr)_auto] items-center gap-3 px-4 py-2.5 text-left transition-colors duration-100 outline-none after:absolute after:right-0 after:bottom-0 after:left-4 after:h-(--hairline) last:after:hidden focus-visible:ring-3 focus-visible:ring-inset"
        >
          <span class="min-w-0">
            <span class="text-body block truncate">{c.title}</span>
            <span class="text-subheadline text-muted-foreground block truncate">
              {[c.artist, c.album, c.year].filter(Boolean).join(' · ')}
            </span>
            <span class="text-footnote text-muted-foreground mt-0.5 flex items-center gap-1.5">
              <span
                class="size-2 shrink-0 rounded-full"
                style="background: {providerColor(c.source)}"
                aria-hidden="true"
              ></span>
              {c.source}
            </span>
          </span>
          <span class="flex items-center gap-2">
            {#if pct != null}
              <span class="text-body text-muted-foreground tabular-nums">{pct}%</span>
            {/if}
            <Check
              class={cn('text-primary size-5', !picked && 'invisible')}
              strokeWidth={2.5}
              aria-hidden="true"
            />
          </span>
        </button>
      {/each}
    </div>
  {/if}
{:else if loading && candidates.length === 0}
  <div class="text-muted-foreground flex items-center gap-2 py-2 text-sm">
    <Spinner class="size-4" /> Loading candidates…
  </div>
{:else if candidates.length === 0}
  <div class="text-muted-foreground bg-muted rounded-md px-3.5 py-4 text-[12.5px]">
    No fingerprint matches. Enter metadata manually below or skip this file.
  </div>
{:else}
  <!-- Columns follow the width this grid is given, not the window's: a phone, the quality
       dossier and a narrow desktop pane get one column, a roomy pane two. The tracks are
       minmax(0, 1fr) so a long album line truncates instead of widening its card past the pane
       (an implicit auto track grows to the text's full width, and the pane clips it). -->
  <div class="@container">
    <div
      class={cn('grid grid-cols-1 gap-2', !single && '@min-[30rem]:grid-cols-2')}
      role={readonly ? undefined : 'radiogroup'}
      aria-label={readonly ? undefined : 'Candidate matches'}
      use:radioGroup
    >
      {#each candidates as c, i (c.key)}
        {@const picked = pickedKey === c.key}
        {@const pct = pctOf(c)}
        <button
          type="button"
          onclick={() => onpick(c)}
          disabled={readonly}
          {...radioProps(i, picked)}
          class={cn(
            'flex items-center gap-3 rounded-lg border p-3 text-left transition-[color,background-color,border-color,transform] duration-100 ease-out',
            !readonly && 'active:scale-[0.99]',
            picked
              ? 'border-primary bg-primary/10'
              : readonly
                ? 'border-border bg-background cursor-default'
                : 'border-border bg-background hover:bg-accent'
          )}
        >
          <div class="min-w-0 flex-1">
            <div class="truncate text-[13px] font-medium">{c.title}</div>
            <!-- The separators live in expressions: Svelte trims the whitespace a block starts
               with, which glued them to the previous word ("Koji Minami· Tokyo Rain"). -->
            <div class="text-muted-foreground mt-0.5 truncate text-[11.5px]">
              {c.artist}{c.album ? ' · ' : ''}{#if c.album}<em class="italic">{c.album}</em
                >{/if}{c.year ? ` · ${c.year}` : ''}
            </div>
            <div class="text-muted-foreground mt-1.5 flex items-center gap-1.5 text-[11px]">
              <span
                class="size-1.5 shrink-0 rounded-full"
                style="background: {providerColor(c.source)}"
                aria-hidden="true"
              ></span>
              {c.source}
            </div>
          </div>
          <div class="flex shrink-0 flex-col items-end gap-0.5">
            {#if pct != null}
              <span
                class={cn(
                  'text-[14px] font-semibold tabular-nums',
                  picked ? 'text-primary' : 'text-foreground'
                )}>{pct}%</span
              >
            {/if}
            {#if picked}
              <!-- Seen, not spoken: the radio's "checked" already says it. -->
              <span
                class="text-primary flex items-center gap-1 text-[11px] font-medium"
                aria-hidden="true"
              >
                <Check class="size-3" strokeWidth={2.5} /> Picked
              </span>
            {/if}
          </div>
        </button>
      {/each}
    </div>
  </div>
{/if}

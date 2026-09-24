<script lang="ts">
  import type { QualityCategory } from '$lib/api-client';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { QUALITY_BUCKETS } from '$lib/quality-ui';
  import { cn } from '$lib/utils';
  import { TriangleAlert, Check, EarOff } from '@lucide/svelte';

  // The three workbench buckets — a category selector as much as three stats, so they stay
  // tappable and show which one is current. Two presentations of the same thing:
  //  - `cards` (the desktop split view): three compact cards in a row;
  //  - `rows` (the phone and tablet drill-down): an inset list, each row with the explanation that
  //    used to hide in a hover tooltip as its second line, the count trailing, and a checkmark on
  //    the current bucket.
  // Colour is status here and only here: red for the silent failures, the warning tone for what
  // the algorithm flagged, the tint glyph for verified — always beside the bucket's name.
  type Props = {
    flagged: number;
    silent: number;
    verified: number;
    /** Total graded, for the "x% of graded" line on the verified bucket. */
    graded: number;
    active: QualityCategory;
    onSelect: (c: QualityCategory) => void;
    layout?: 'cards' | 'rows';
  };

  const { flagged, silent, verified, graded, active, onSelect, layout = 'cards' }: Props = $props();

  const verifiedPct = $derived(graded > 0 ? Math.round((verified / graded) * 100) : 0);

  const META = {
    silent: {
      icon: EarOff,
      tone: 'bg-destructive/12 text-destructive-text',
      valueTone: 'text-destructive-text',
      sub: 'Algorithm said fine — AI disagrees'
    },
    flagged: {
      icon: TriangleAlert,
      tone: 'bg-warning/15 text-warning-text',
      valueTone: '',
      sub: 'Awaiting your review'
    },
    verified: {
      icon: Check,
      tone: 'bg-primary/12 text-primary',
      valueTone: '',
      sub: ''
    }
  } as const;

  const CARDS = $derived(
    QUALITY_BUCKETS.map((b) => ({
      ...b,
      ...META[b.id],
      value: b.id === 'silent' ? silent : b.id === 'flagged' ? flagged : verified,
      sub: b.id === 'verified' ? `Both agree · ${verifiedPct}% of graded` : META[b.id].sub
    }))
  );
</script>

{#if layout === 'rows'}
  <GroupedList.Section header="Buckets">
    {#each CARDS as card (card.id)}
      <GroupedList.Row
        onclick={() => onSelect(card.id)}
        aria-pressed={active === card.id}
        icon={card.icon}
        iconClass={card.tone}
        label={card.label}
        sublabel={card.id === 'verified' ? `${verifiedPct}% of graded · ${card.explain}` : card.explain}
      >
        {#snippet trailing()}
          <span class="flex items-center gap-2">
            <span class={cn('text-body tabular-nums', card.valueTone || 'text-muted-foreground')}
              >{card.value.toLocaleString()}</span
            >
            <span class="flex w-5 justify-end">
              {#if active === card.id}
                <Check class="text-primary size-5" strokeWidth={2.5} aria-hidden="true" />
              {/if}
            </span>
          </span>
        {/snippet}
      </GroupedList.Row>
    {/each}
  </GroupedList.Section>
{:else}
  <div class="grid gap-3 sm:grid-cols-3">
    {#each CARDS as card (card.id)}
      {@const Icon = card.icon}
      <button
        type="button"
        onclick={() => onSelect(card.id)}
        title={card.explain}
        aria-pressed={active === card.id}
        class={cn(
          'group bg-card focus-visible:ring-ring/50 flex items-center gap-3 rounded-xl p-3 text-left ring-1 ring-transparent outline-none transition-[box-shadow,transform] duration-150 active:scale-[0.99] focus-visible:ring-3',
          active === card.id ? 'ring-primary' : 'hover:ring-border'
        )}
      >
        <span class={cn('grid size-[29px] shrink-0 place-items-center rounded-[7px]', card.tone)}>
          <Icon class="size-[18px]" aria-hidden="true" />
        </span>
        <span class={cn('shrink-0 text-xl leading-none font-semibold tabular-nums', card.valueTone)}
          >{card.value.toLocaleString()}</span
        >
        <span class="min-w-0">
          <span class="text-nav block truncate font-medium">{card.label}</span>
          <span class="text-muted-foreground text-nav-xs block truncate">{card.sub}</span>
        </span>
      </button>
    {/each}
  </div>
{/if}

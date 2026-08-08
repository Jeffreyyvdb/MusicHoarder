<script lang="ts">
  import type { QualityCategory } from '$lib/api-client';
  import { cn } from '$lib/utils';
  import { TriangleAlert, Check, EarOff } from '@lucide/svelte';

  // These are the category selector as much as they are stats, so they stay
  // clickable and keep their selected treatment. What they lost is the shape:
  // three 200px cards with 40px numerals and a paragraph each, for three
  // numbers. The paragraphs live on as tooltips; the numbers do the talking.
  type Props = {
    flagged: number;
    silent: number;
    verified: number;
    /** Total graded, for the "x% of library" line on the verified card. */
    graded: number;
    active: QualityCategory;
    onSelect: (c: QualityCategory) => void;
  };

  const { flagged, silent, verified, graded, active, onSelect }: Props = $props();

  const verifiedPct = $derived(graded > 0 ? Math.round((verified / graded) * 100) : 0);

  const CARDS = $derived([
    {
      id: 'flagged' as const,
      icon: TriangleAlert,
      tone: 'bg-amber-500/12 text-amber-600 dark:text-amber-400',
      value: flagged,
      valueTone: '',
      label: 'Algorithm flagged',
      sub: 'Awaiting your review',
      title:
        "The algorithm wasn't confident enough to auto-accept. Pick a candidate or correct the values by hand."
    },
    {
      id: 'silent' as const,
      icon: EarOff,
      tone: 'bg-red-500/12 text-red-600 dark:text-red-400',
      value: silent,
      valueTone: 'text-red-600 dark:text-red-400',
      label: 'Silent failures',
      sub: 'Algorithm said fine — AI disagrees',
      title:
        "Auto-accepted, but the LLM grader rates them wrong or questionable. Your algorithm's blind spots."
    },
    {
      id: 'verified' as const,
      icon: Check,
      tone: 'bg-emerald-500/12 text-emerald-600 dark:text-emerald-400',
      value: verified,
      valueTone: '',
      label: 'Verified clean',
      sub: `Both agree · ${verifiedPct}% of graded`,
      title:
        'Auto-accepted with full provider corroboration and a top-bucket LLM grade.'
    }
  ]);
</script>

<div class="grid gap-2 sm:grid-cols-3">
  {#each CARDS as card (card.id)}
    {@const Icon = card.icon}
    <button
      type="button"
      onclick={() => onSelect(card.id)}
      title={card.title}
      aria-pressed={active === card.id}
      class={cn(
        'group bg-card flex items-center gap-3 rounded-lg border p-3 text-left transition-all hover:border-foreground/20 active:scale-[0.99]',
        active === card.id && 'ring-foreground/15 border-foreground/25 ring-1'
      )}
    >
      <span class={cn('grid size-8 shrink-0 place-items-center rounded-lg', card.tone)}>
        <Icon class="size-4" />
      </span>
      <span
        class={cn(
          'shrink-0 text-xl leading-none font-semibold tracking-tight tabular-nums',
          card.valueTone
        )}>{card.value.toLocaleString()}</span
      >
      <span class="min-w-0">
        <span class="text-nav block truncate font-medium">{card.label}</span>
        <span class="text-muted-foreground text-nav-xs block truncate">{card.sub}</span>
      </span>
    </button>
  {/each}
</div>

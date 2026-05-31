<script lang="ts">
  import type { QualityCategory } from '$lib/api-client';
  import { cn } from '$lib/utils';
  import { TriangleAlert, Check, EarOff } from '@lucide/svelte';

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
</script>

<div class="grid gap-3 sm:grid-cols-3 lg:grid-cols-[1fr_1.2fr_1fr]">
  <!-- ALGORITHM FLAGGED -->
  <button
    type="button"
    onclick={() => onSelect('flagged')}
    class={cn(
      'bg-card flex flex-col gap-1.5 rounded-lg border border-l-[3px] border-amber-500/60 p-4 text-left transition-colors',
      active === 'flagged' ? 'ring-foreground/30 ring-1' : 'hover:bg-muted/40'
    )}
  >
    <div class="flex items-center gap-2">
      <span class="grid size-[22px] place-items-center rounded-md bg-amber-500/15 text-amber-600 dark:text-amber-400">
        <TriangleAlert class="size-3.5" />
      </span>
      <span class="text-[10.5px] font-semibold tracking-[0.1em] text-amber-600 uppercase dark:text-amber-400">Algorithm flagged</span>
    </div>
    <div class="font-mono text-[40px] leading-none font-bold tracking-tight tabular-nums">{flagged.toLocaleString()}</div>
    <div class="text-[14px] font-semibold">Awaiting your review</div>
    <div class="text-muted-foreground flex-1 text-[12px] leading-relaxed">
      The algorithm wasn't confident enough to auto-accept. Pick a candidate or correct the values by hand.
    </div>
    <span class="inline-flex items-center gap-1 pt-1 text-[12px] font-medium text-amber-600 dark:text-amber-400">Review queue →</span>
  </button>

  <!-- SILENT FAILURES (highlighted) -->
  <button
    type="button"
    onclick={() => onSelect('silent')}
    class={cn(
      'flex flex-col gap-1.5 rounded-lg border border-l-[3px] border-red-500/60 bg-gradient-to-b from-red-500/[0.06] to-transparent p-4 text-left transition-colors',
      active === 'silent' ? 'ring-foreground/30 ring-1' : 'hover:from-red-500/[0.1]'
    )}
  >
    <div class="flex items-center gap-2">
      <span class="grid size-[22px] place-items-center rounded-md bg-red-500/15 text-red-600 dark:text-red-400">
        <EarOff class="size-3.5" />
      </span>
      <span class="text-[10.5px] font-semibold tracking-[0.1em] text-red-600 uppercase dark:text-red-400">Silent failures</span>
    </div>
    <div class="font-mono text-[40px] leading-none font-bold tracking-tight tabular-nums text-red-600 dark:text-red-400">{silent.toLocaleString()}</div>
    <div class="text-[14px] font-semibold">Algorithm said fine — AI disagrees</div>
    <div class="text-muted-foreground flex-1 text-[12px] leading-relaxed">
      These tracks were auto-accepted, but the LLM grader rates them <em class="text-foreground/80 not-italic">wrong</em> or
      <em class="text-foreground/80 not-italic">questionable</em>. Your algorithm's blind spots.
    </div>
    <span class="inline-flex items-center gap-1 pt-1 text-[12px] font-medium text-red-600 dark:text-red-400">Investigate →</span>
  </button>

  <!-- VERIFIED CLEAN -->
  <button
    type="button"
    onclick={() => onSelect('verified')}
    class={cn(
      'bg-card flex flex-col gap-1.5 rounded-lg border border-l-[3px] border-emerald-500/60 p-4 text-left transition-colors',
      active === 'verified' ? 'ring-foreground/30 ring-1' : 'hover:bg-muted/40'
    )}
  >
    <div class="flex items-center gap-2">
      <span class="grid size-[22px] place-items-center rounded-md bg-emerald-500/15 text-emerald-600 dark:text-emerald-400">
        <Check class="size-3.5" />
      </span>
      <span class="text-[10.5px] font-semibold tracking-[0.1em] text-emerald-600 uppercase dark:text-emerald-400">Verified clean</span>
    </div>
    <div class="font-mono text-[40px] leading-none font-bold tracking-tight tabular-nums">{verified.toLocaleString()}</div>
    <div class="text-[14px] font-semibold">Algorithm + AI both agree</div>
    <div class="text-muted-foreground flex-1 text-[12px] leading-relaxed">
      Auto-accepted with full provider corroboration and a top-bucket LLM grade. {verifiedPct}% of graded library.
    </div>
    <span class="inline-flex items-center gap-1 pt-1 text-[12px] font-medium text-emerald-600 dark:text-emerald-400">Browse →</span>
  </button>
</div>

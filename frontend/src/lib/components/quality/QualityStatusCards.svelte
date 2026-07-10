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

<div class="grid gap-4 sm:grid-cols-3">
  <!-- ALGORITHM FLAGGED -->
  <button
    type="button"
    onclick={() => onSelect('flagged')}
    class={cn(
      'group bg-card flex flex-col gap-2 rounded-xl border p-5 text-left transition-all hover:border-foreground/20 hover:shadow-sm active:scale-[0.99]',
      active === 'flagged' && 'ring-foreground/15 border-foreground/25 ring-1'
    )}
  >
    <div class="flex items-center gap-2.5">
      <span class="grid size-8 place-items-center rounded-lg bg-amber-500/12 text-amber-600 dark:text-amber-400">
        <TriangleAlert class="size-4" />
      </span>
      <span class="text-muted-foreground text-[13px] font-medium">Algorithm flagged</span>
    </div>
    <div class="text-[40px] leading-none font-semibold tracking-tight tabular-nums">{flagged.toLocaleString()}</div>
    <div class="text-[14px] font-semibold">Awaiting your review</div>
    <div class="text-muted-foreground flex-1 text-[12px] leading-relaxed">
      The algorithm wasn't confident enough to auto-accept. Pick a candidate or correct the values by hand.
    </div>
    <span class="text-muted-foreground group-hover:text-foreground inline-flex items-center gap-1 pt-1 text-[12px] font-medium transition-colors">Review queue →</span>
  </button>

  <!-- SILENT FAILURES -->
  <button
    type="button"
    onclick={() => onSelect('silent')}
    class={cn(
      'group bg-card flex flex-col gap-2 rounded-xl border p-5 text-left transition-all hover:border-foreground/20 hover:shadow-sm active:scale-[0.99]',
      active === 'silent' && 'ring-foreground/15 border-foreground/25 ring-1'
    )}
  >
    <div class="flex items-center gap-2.5">
      <span class="grid size-8 place-items-center rounded-lg bg-red-500/12 text-red-600 dark:text-red-400">
        <EarOff class="size-4" />
      </span>
      <span class="text-muted-foreground text-[13px] font-medium">Silent failures</span>
    </div>
    <div class="text-[40px] leading-none font-semibold tracking-tight tabular-nums text-red-600 dark:text-red-400">{silent.toLocaleString()}</div>
    <div class="text-[14px] font-semibold">Algorithm said fine — AI disagrees</div>
    <div class="text-muted-foreground flex-1 text-[12px] leading-relaxed">
      These tracks were auto-accepted, but the LLM grader rates them <em class="text-foreground/80 not-italic">wrong</em> or
      <em class="text-foreground/80 not-italic">questionable</em>. Your algorithm's blind spots.
    </div>
    <span class="text-muted-foreground group-hover:text-foreground inline-flex items-center gap-1 pt-1 text-[12px] font-medium transition-colors">Investigate →</span>
  </button>

  <!-- VERIFIED CLEAN -->
  <button
    type="button"
    onclick={() => onSelect('verified')}
    class={cn(
      'group bg-card flex flex-col gap-2 rounded-xl border p-5 text-left transition-all hover:border-foreground/20 hover:shadow-sm active:scale-[0.99]',
      active === 'verified' && 'ring-foreground/15 border-foreground/25 ring-1'
    )}
  >
    <div class="flex items-center gap-2.5">
      <span class="grid size-8 place-items-center rounded-lg bg-emerald-500/12 text-emerald-600 dark:text-emerald-400">
        <Check class="size-4" />
      </span>
      <span class="text-muted-foreground text-[13px] font-medium">Verified clean</span>
    </div>
    <div class="text-[40px] leading-none font-semibold tracking-tight tabular-nums">{verified.toLocaleString()}</div>
    <div class="text-[14px] font-semibold">Algorithm + AI both agree</div>
    <div class="text-muted-foreground flex-1 text-[12px] leading-relaxed">
      Auto-accepted with full provider corroboration and a top-bucket LLM grade. {verifiedPct}% of graded library.
    </div>
    <span class="text-muted-foreground group-hover:text-foreground inline-flex items-center gap-1 pt-1 text-[12px] font-medium transition-colors">Browse →</span>
  </button>
</div>

<script lang="ts">
  import { cn } from '$lib/utils';

  type Props = {
    /** Library-wide most-common issue codes, highest count first. */
    topIssues: { code: string; count: number }[];
    onSelectIssue?: (code: string) => void;
  };

  const { topIssues, onSelectIssue }: Props = $props();

  const top = $derived(topIssues.slice(0, 3));
  // Severity tint by rank — the most frequent pattern reads as the most urgent to fix.
  const RANK_BORDER = ['border-l-red-500/70', 'border-l-amber-500/70', 'border-l-teal-500/70'];
  // Turn snake_case issue codes into a readable label.
  const pretty = (code: string) => code.replace(/_/g, ' ');
</script>

{#if top.length > 0}
  <div class="bg-card grid gap-4 rounded-lg border p-4 lg:grid-cols-[280px_1fr]">
    <div class="flex flex-col gap-1.5">
      <div class="text-muted-foreground text-[10px] font-semibold tracking-[0.1em] uppercase">
        Algorithm patterns · ranked by frequency
      </div>
      <div class="text-[13.5px] leading-snug font-medium">
        The <span class="font-mono">{top.length}</span> most common issues across the graded library — fix these patterns
        to lift the most tracks at once.
      </div>
      <div class="text-muted-foreground text-[11px] leading-relaxed">
        Each pattern is the issue code the LLM grader flagged most often. Open one to see every track it affects.
      </div>
    </div>
    <div class="flex flex-col gap-1.5">
      {#each top as ins, i (ins.code)}
        <button
          type="button"
          onclick={() => onSelectIssue?.(ins.code)}
          class={cn(
            'bg-background hover:bg-muted/50 grid grid-cols-[24px_1fr_auto] items-center gap-3 rounded-md border border-l-[3px] px-3 py-2 text-left transition-colors',
            RANK_BORDER[i] ?? 'border-l-border'
          )}
        >
          <span class="text-muted-foreground font-mono text-[12px] font-semibold tabular-nums">{String(i + 1).padStart(2, '0')}</span>
          <div class="min-w-0">
            <div class="truncate text-[12.5px] font-medium">{pretty(ins.code)}</div>
            <div class="text-muted-foreground font-mono text-[11px]">
              <span class="text-foreground/80">{ins.count.toLocaleString()}</span>
              {ins.count === 1 ? 'track' : 'tracks'} affected
            </div>
          </div>
          <code class="bg-muted text-muted-foreground rounded px-1.5 py-0.5 font-mono text-[10px]">{ins.code}</code>
        </button>
      {/each}
    </div>
  </div>
{/if}

<script lang="ts">
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { issueLabel } from '$lib/quality-ui';

  // The algorithm's most common failure patterns (issue codes the AI grader flagged most). Opening
  // one filters the full graded list to it. `card` is the desktop's explanatory card; `rows` is
  // the phone's inset list, with the explanation as the section footer.
  type Props = {
    /** Library-wide most-common issue codes, highest count first. */
    topIssues: { code: string; count: number }[];
    onSelectIssue?: (code: string) => void;
    layout?: 'card' | 'rows';
  };

  const { topIssues, onSelectIssue, layout = 'card' }: Props = $props();

  const top = $derived(topIssues.slice(0, 3));
  const affected = (n: number) => `${n.toLocaleString()} ${n === 1 ? 'track' : 'tracks'}`;
</script>

{#if top.length > 0}
  {#if layout === 'rows'}
    <GroupedList.Section
      header="Most common issues"
      footer="The issues the AI grader flagged most often. Fix a pattern to lift the most tracks at once."
    >
      {#each top as ins (ins.code)}
        <GroupedList.Row
          onclick={() => onSelectIssue?.(ins.code)}
          label={issueLabel(ins.code)}
          value={affected(ins.count)}
          chevron
        />
      {/each}
    </GroupedList.Section>
  {:else}
    <div class="bg-card grid gap-4 rounded-xl p-5 lg:grid-cols-[280px_1fr]">
      <div class="flex flex-col gap-1.5">
        <div class="text-[12px] font-semibold">Algorithm patterns</div>
        <div class="text-[13.5px] leading-snug font-medium">
          The {top.length} most common issues across the graded library — fix these patterns to lift the
          most tracks at once.
        </div>
        <div class="text-muted-foreground text-[11px] leading-relaxed">
          Each pattern is an issue the AI grader flagged most often. Open one to see every track it
          affects.
        </div>
      </div>
      <div class="flex flex-col gap-1.5">
        {#each top as ins, i (ins.code)}
          <!-- The grader's raw code is the hover title, not a second label. -->
          <button
            type="button"
            title={ins.code}
            onclick={() => onSelectIssue?.(ins.code)}
            class="bg-muted hover:bg-secondary-hover focus-visible:ring-ring/50 grid grid-cols-[24px_1fr] items-center gap-3 rounded-lg px-3 py-2 text-left outline-none transition-colors focus-visible:ring-3 active:scale-[0.99]"
          >
            <span class="text-muted-foreground text-[12px] font-semibold tabular-nums">{String(i + 1).padStart(2, '0')}</span>
            <div class="min-w-0">
              <div class="truncate text-[12.5px] font-medium">{issueLabel(ins.code)}</div>
              <div class="text-muted-foreground text-[11px]">
                <span class="text-foreground tabular-nums">{ins.count.toLocaleString()}</span>
                {ins.count === 1 ? 'track' : 'tracks'} affected
              </div>
            </div>
          </button>
        {/each}
      </div>
    </div>
  {/if}
{/if}

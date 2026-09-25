<script lang="ts">
  import type { Snippet } from 'svelte';
  import { cn } from '$lib/utils';

  /**
   * A form cell inside a GroupedList section: a caption over a full-width control, then an
   * optional hint. Stacked rather than label-left/field-right because a phone is too narrow for
   * a path or a secret beside its label. Ends in the same inset hairline as a GroupedList row
   * (none after the last cell), so fields and rows share one section.
   */
  type Props = {
    /** The caption; rendered as a <label> when `for` names the control. */
    label: string;
    for?: string;
    /** Explanation under the control. */
    hint?: string | Snippet;
    children: Snippet;
    class?: string;
  };

  const { label, for: forId, hint, children, class: className }: Props = $props();
</script>

<div
  data-slot="grouped-list-row"
  class={cn(
    'relative flex flex-col gap-1.5 px-4 py-3',
    'after:bg-separator after:absolute after:right-0 after:bottom-0 after:left-4 after:h-(--hairline) last:after:hidden',
    className
  )}
>
  {#if forId}
    <label for={forId} class="text-subheadline text-muted-foreground md:text-xs">{label}</label>
  {:else}
    <span class="text-subheadline text-muted-foreground md:text-xs">{label}</span>
  {/if}
  {@render children()}
  {#if hint}
    <p class="text-footnote text-muted-foreground md:text-xs">
      {#if typeof hint === 'string'}{hint}{:else}{@render hint()}{/if}
    </p>
  {/if}
</div>

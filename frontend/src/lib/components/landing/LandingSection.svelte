<script lang="ts">
  import type { Snippet } from 'svelte';
  import { cn } from '$lib/utils';

  /**
   * One band of the landing page: a centred header (eyebrow, headline, lead) over its content.
   * Bands alternate `plain` (the page colour) and `grouped` (#F2F2F7 in light mode), like the
   * app's plain and grouped pages; the eyebrow is a label, not a link, so it takes no tint.
   */
  type Props = {
    id?: string;
    eyebrow?: string;
    title: string;
    lead?: string;
    surface?: 'plain' | 'grouped';
    class?: string;
    children?: Snippet;
  };
  const {
    id,
    eyebrow,
    title,
    lead,
    surface = 'plain',
    class: className = '',
    children
  }: Props = $props();
</script>

<section
  {id}
  class={cn(
    'scroll-mt-14 px-4 py-20 md:px-10 md:py-28',
    surface === 'grouped' ? 'bg-background-grouped' : 'bg-background',
    className
  )}
>
  <div class="mx-auto max-w-[1200px]">
    <header class="mx-auto max-w-[820px] text-center">
      {#if eyebrow}
        <p class="text-muted-foreground text-[17px] leading-[22px] font-semibold md:text-[19px]">
          {eyebrow}
        </p>
      {/if}
      <h2
        class="mt-2 text-[clamp(32px,5vw,56px)] leading-[1.07] font-bold tracking-[-0.03em] text-balance"
      >
        {title}
      </h2>
      {#if lead}
        <p
          class="text-muted-foreground mx-auto mt-5 max-w-[680px] text-[17px] leading-[1.5] text-pretty md:text-[21px]"
        >
          {lead}
        </p>
      {/if}
    </header>
    {@render children?.()}
  </div>
</section>

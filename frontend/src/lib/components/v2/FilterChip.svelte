<script lang="ts">
  import type { Component, Snippet } from 'svelte';
  import { cn } from '$lib/utils';

  // The one filter-pill idiom. Every page toolbar's `filters` region is built
  // from these, so a chip can't drift in height (h-8, matching the search input
  // and the toolbar's action buttons) or in its pressed treatment.
  type Props = {
    pressed: boolean;
    onclick: () => void;
    /** Optional leading glyph. */
    icon?: Component;
    /** Trailing count; null/undefined hides it. */
    count?: number | null;
    title?: string;
    children: Snippet;
  };

  const { pressed, onclick, icon: Icon, count = null, title, children }: Props = $props();
</script>

<button
  type="button"
  {onclick}
  {title}
  aria-pressed={pressed}
  class={cn(
    'focus-visible:ring-ring text-nav-sm flex h-8 shrink-0 items-center gap-1.5 rounded-full border px-3 whitespace-nowrap transition-colors outline-none focus-visible:ring-2',
    pressed
      ? 'border-primary bg-primary/10 text-primary font-medium'
      : 'border-border bg-card text-muted-foreground hover:text-foreground'
  )}
>
  {#if Icon}<Icon class="size-3.5 shrink-0" aria-hidden="true" />{/if}
  {@render children()}
  {#if count != null}
    <span class="tabular-nums opacity-60">{count.toLocaleString()}</span>
  {/if}
</button>

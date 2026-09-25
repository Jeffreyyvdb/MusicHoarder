<script lang="ts">
  import type { Component, Snippet } from 'svelte';
  import { cn } from '$lib/utils';

  // The one filter-pill idiom. Every page's chip band (PageToolbarV2 `filters` / `filterRow`) is
  // built from these, so a chip can't drift in height or in its pressed treatment.
  //
  // iOS filter-chip look: a borderless gray capsule that turns into a tint capsule when pressed —
  // the fill is the state, so there is no stroke to keep in step. 32px visual everywhere (it sits
  // beside the 32px desktop search field); on touch the hit area grows to 44pt with an `after:`
  // pseudo rather than a taller pill. The chip band gives it the 6px of vertical room that
  // needs, since a horizontal scroller clips anything that pokes out of it.
  //
  // Counts are the muted text token, never an opacity: a 60% count on a grey fill fell to
  // 2.5–2.9:1. Pressed, the count inherits the label colour (white on the tint in light, black on
  // the dark-mode tint) instead of a muted tone that would drown in the fill.
  type Props = {
    pressed: boolean;
    onclick: () => void;
    /** Optional leading glyph. */
    icon?: Component;
    /** Trailing count; null/undefined hides it. */
    count?: number | null;
    title?: string;
    class?: string;
    children: Snippet;
  };

  const {
    pressed,
    onclick,
    icon: Icon,
    count = null,
    title,
    class: className,
    children
  }: Props = $props();
</script>

<button
  type="button"
  {onclick}
  {title}
  aria-pressed={pressed}
  class={cn(
    'relative flex h-8 shrink-0 items-center gap-1.5 rounded-full px-3.5 whitespace-nowrap outline-none select-none',
    'text-subheadline md:text-nav-sm font-medium transition-[background-color,color,transform] duration-150 ease-[cubic-bezier(0.23,1,0.32,1)] active:scale-[0.97]',
    'focus-visible:ring-ring focus-visible:ring-2',
    'after:absolute after:inset-x-0 after:-inset-y-1.5 md:after:inset-y-0',
    pressed
      ? 'bg-primary text-primary-foreground hover:bg-primary/90'
      : 'bg-secondary text-foreground hover:bg-secondary-hover',
    className
  )}
>
  {#if Icon}<Icon class="size-4 shrink-0 md:size-3.5" aria-hidden="true" />{/if}
  {@render children()}
  {#if count != null}
    <span class={cn('font-normal tabular-nums', !pressed && 'text-muted-foreground')}
      >{count.toLocaleString()}</span
    >
  {/if}
</button>

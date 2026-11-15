<script lang="ts">
  // A gray reflective LCD, like a calculator's: a recessed window of gray glass with dark print and
  // an optional tag printed on the glass.
  import type { Snippet } from 'svelte';
  import type { HTMLAttributes } from 'svelte/elements';

  let {
    tag,
    size = 'md',
    children,
    ...rest
  }: {
    tag?: string;
    size?: 'sm' | 'md' | 'lg';
    children: Snippet;
  } & HTMLAttributes<HTMLDivElement> = $props();
</script>

<div data-kit="lcd" data-size={size} {...rest}>
  {#if tag}<span data-kit="lcd-tag">{tag}</span>{/if}
  <div data-kit="lcd-body">{@render children()}</div>
</div>

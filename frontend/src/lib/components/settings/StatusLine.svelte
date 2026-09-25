<script lang="ts">
  import type { Snippet } from 'svelte';
  import { CircleAlert, CircleCheck } from '@lucide/svelte';
  import { cn } from '$lib/utils';

  /**
   * The outcome of a settings action, as a footnote line under its section rather than a tinted
   * banner: an error in the destructive text token with an alert glyph; a success in ordinary
   * text with a tint check (success is a word plus a glyph, never green text). Announced
   * politely, or assertively for an error.
   */
  type Props = { tone: 'success' | 'error'; children: Snippet; class?: string };
  const { tone, children, class: className }: Props = $props();
</script>

<p
  role={tone === 'error' ? 'alert' : 'status'}
  class={cn(
    'text-footnote flex items-start gap-1.5 md:text-xs',
    tone === 'error' ? 'text-destructive-text' : 'text-foreground',
    className
  )}
>
  {#if tone === 'error'}
    <CircleAlert class="mt-px size-4 shrink-0" aria-hidden="true" />
  {:else}
    <CircleCheck class="text-primary mt-px size-4 shrink-0" aria-hidden="true" />
  {/if}
  <span class="min-w-0 break-words">{@render children()}</span>
</p>

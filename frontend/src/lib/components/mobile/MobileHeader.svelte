<script lang="ts">
  import type { Snippet } from 'svelte';
  import { ChevronLeft } from '@lucide/svelte';

  type Props = {
    title?: string;
    sub?: string;
    /** When set, shows a back button with this label. */
    back?: string;
    onback?: () => void;
    /** Optional content rendered on the right side of the header. */
    right?: Snippet;
    /** Transparent header (used over a tinted hero). */
    transparent?: boolean;
    class?: string;
  };

  const { title, sub, back, onback, right, transparent = false, class: className }: Props = $props();
</script>

<div
  class="mob-header {className ?? ''}"
  style={transparent ? 'background: transparent; border-bottom: 0;' : ''}
>
  {#if back}
    <button class="mob-h-back" onclick={() => onback?.()}>
      <ChevronLeft size={18} strokeWidth={2} />
      {back}
    </button>
  {/if}
  <div class="mob-h-title">
    {#if title}<div>{title}</div>{/if}
    {#if sub}<div class="mob-h-sub">{sub}</div>{/if}
  </div>
  {#if right}{@render right()}{/if}
</div>

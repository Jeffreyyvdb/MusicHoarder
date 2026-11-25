<script lang="ts">
  import { CircleAlert, TriangleAlert } from '@lucide/svelte';
  import type { BackgroundTone } from '#lib/background-work/types.ts';

  type Props = { tone: BackgroundTone; class?: string };
  let { tone, class: className = '' }: Props = $props();
</script>

{#if tone === 'bad'}
  <CircleAlert
    class="text-destructive-text size-3 shrink-0 {className}"
    aria-hidden="true"
    strokeWidth={2.5}
  />
  <span class="sr-only">Problem:</span>
{:else if tone === 'warning'}
  <TriangleAlert
    class="text-warning-text size-3 shrink-0 {className}"
    aria-hidden="true"
    strokeWidth={2.5}
  />
  <span class="sr-only">Needs attention:</span>
{/if}

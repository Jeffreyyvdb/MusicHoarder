<script lang="ts">
  import { CircleAlert, TriangleAlert } from '@lucide/svelte';
  import { toneIsTrouble } from '#lib/background-work/tone.ts';
  import type { BackgroundTone } from '#lib/background-work/types.ts';

  /**
   * A legend key: the mark's own fill, drawn the shape the mark is. A tone that means trouble also
   * carries a glyph, so a failure is never told by colour alone.
   */
  type Props = {
    fill: string;
    tone: BackgroundTone;
    shape?: 'box' | 'dash';
  };
  let { fill, tone, shape = 'box' }: Props = $props();
</script>

<span class="inline-flex shrink-0 items-center gap-1" aria-hidden="true">
  {#if shape === 'dash'}
    <span class="block h-0 w-4 border-t-2 border-dashed" style:border-color={fill}></span>
  {:else}
    <span class="block size-3 rounded-[3px]" style:background-color={fill}></span>
  {/if}
  {#if toneIsTrouble(tone)}
    {#if tone === 'bad'}
      <CircleAlert class="text-destructive-text size-3" strokeWidth={2.5} />
    {:else}
      <TriangleAlert class="text-warning-text size-3" strokeWidth={2.5} />
    {/if}
  {/if}
</span>

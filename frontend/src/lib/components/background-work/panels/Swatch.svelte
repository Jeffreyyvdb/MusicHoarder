<script lang="ts">
  import { toneIsTrouble } from '#lib/background-work/tone.ts';
  import type { BackgroundTone } from '#lib/background-work/types.ts';
  import TroubleGlyph from './TroubleGlyph.svelte';

  /**
   * A legend key: the mark's own fill, drawn the shape the mark is. A tone that means trouble also
   * carries a glyph, so a failure is never told by colour alone.
   */
  type Props = {
    fill: string;
    tone: BackgroundTone;
    shape?: 'box' | 'dash';
    /** False where the row prints the glyph itself, beside its label. */
    glyph?: boolean;
  };
  let { fill, tone, shape = 'box', glyph = true }: Props = $props();
</script>

<span class="inline-flex shrink-0 items-center gap-1" aria-hidden="true">
  {#if shape === 'dash'}
    <span class="block h-0 w-4 border-t-2 border-dashed" style:border-color={fill}></span>
  {:else}
    <span class="block size-3 rounded-[3px]" style:background-color={fill}></span>
  {/if}
  {#if glyph && toneIsTrouble(tone)}
    <TroubleGlyph {tone} />
  {/if}
</span>

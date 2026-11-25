<script lang="ts">
  import type { BackgroundJobState } from '#lib/background-work/types.ts';
  import { STATE_TONE, STATE_WORD } from '#lib/background-work/tone.ts';
  import { toneColor } from '#lib/background-work/tone.ts';

  type Props = { state: BackgroundJobState; class?: string };
  let { state, class: className = '' }: Props = $props();

  const hollow = $derived(state === 'off' || state === 'unknown');
  const color = $derived(toneColor(STATE_TONE[state]));
</script>

<span class="inline-flex items-center gap-2 {className}">
  <span class="relative inline-flex size-2 shrink-0" aria-hidden="true">
    {#if state === 'working'}
      <span
        class="absolute -inset-[3px] rounded-full opacity-30 motion-safe:animate-pulse"
        style:background-color={color}
      ></span>
    {/if}
    <span
      class="relative size-2 rounded-full"
      style:background-color={hollow ? 'transparent' : color}
      style:box-shadow={hollow ? `inset 0 0 0 1.5px ${color}` : undefined}
    ></span>
  </span>
  <span class="text-footnote text-muted-foreground md:text-xs">{STATE_WORD[state]}</span>
</span>

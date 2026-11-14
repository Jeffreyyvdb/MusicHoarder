<script lang="ts">
  // A flat knob over a tick ring: 28 ticks over a 270° sweep, every ninth heavier, those up to the
  // value lit. Dragging the dial up or down scrubs the value (UI SDK's number input scrub area); the
  // readout is a real number field for keyboards and screen readers.
  import {
    NumberInput,
    NumberInputControl,
    NumberInputScrubArea
  } from '@ui-sdk/svelte/number-input';
  import { haptic } from './behaviors/haptics.ts';

  const knobTicks = 27;
  const litTicks = (value: number) => Math.floor((value * knobTicks) / 100);
  const ticks = Array.from({ length: knobTicks + 1 }, (_, i) => {
    const angle = ((-135 + (i / knobTicks) * 270 - 90) * Math.PI) / 180;
    return {
      i,
      x1: Math.cos(angle) * 40,
      y1: Math.sin(angle) * 40,
      x2: Math.cos(angle) * 50,
      y2: Math.sin(angle) * 50
    };
  });

  let {
    label,
    color,
    value,
    onValueChange
  }: {
    label: string;
    color: string;
    /** 0–100. */
    value: number;
    onValueChange: (value: number) => void;
  } = $props();

  const lit = $derived(litTicks(value));
  const share = $derived(value / 100);

  function change(next: number | null) {
    if (next === null) return;
    // A detent each time another tick lights or goes out, like an encoder's clicks.
    if (litTicks(value) !== litTicks(next)) haptic('selection');
    onValueChange(next);
  }
</script>

<NumberInput min={0} max={100} {value} onValueChange={change}>
  <div data-kit="knob" data-color={color} style:--turn="{share * 270 - 135}deg" style:--lit={share}>
    <NumberInputScrubArea data-kit="knob-dial" orientation="vertical" pixelsPerStep={2}>
      <svg data-kit="knob-ticks" viewBox="-50 -50 100 100" aria-hidden="true">
        {#each ticks as t (t.i)}
          <line
            x1={t.x1}
            y1={t.y1}
            x2={t.x2}
            y2={t.y2}
            data-major={t.i % 9 === 0 || undefined}
            data-lit={t.i <= lit || undefined}
          />
        {/each}
      </svg>
      <span data-kit="knob-cap"></span>
    </NumberInputScrubArea>
    <NumberInputControl data-kit="knob-readout" aria-label={label} />
    <span data-kit="label">{label}</span>
  </div>
</NumberInput>

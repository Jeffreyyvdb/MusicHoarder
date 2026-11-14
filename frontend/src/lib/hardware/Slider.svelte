<script lang="ts">
  // A fader on a printed scale. The value follows `value` until a drag or a key moves it; the move
  // reports through onValueChange on every step and onValueCommit once it ends.
  import { Field, FieldLabel } from '@ui-sdk/svelte/field';
  import {
    Slider,
    SliderControl,
    SliderIndicator,
    SliderThumb,
    SliderTrack,
    SliderValue
  } from '@ui-sdk/svelte/slider';
  import { crosses, haptic } from './behaviors/haptics.ts';

  let {
    label,
    value,
    min = 0,
    max = 100,
    step,
    disabled,
    format,
    onValueChange,
    onValueCommit
  }: {
    label: string;
    value: number;
    min?: number;
    max?: number;
    step?: number;
    disabled?: boolean;
    format?: (value: number) => string;
    onValueChange?: (value: number) => void;
    onValueCommit?: (value: number) => void;
  } = $props();

  // A tick each time the thumb passes a twentieth of the range.
  function change(next: number) {
    if (crosses(value - min, next - min, (max - min) / 20)) haptic('selection');
    onValueChange?.(next);
  }
</script>

<Field data-kit="field">
  <Slider
    data-kit="slider"
    {value}
    {min}
    {max}
    {step}
    {disabled}
    format={format ? (v: number) => format(v) : undefined}
    onValueChange={(next) => change(next as number)}
    onValueCommit={(next) => onValueCommit?.(next as number)}
  >
    <div data-kit="row">
      <FieldLabel data-kit="label">{label}</FieldLabel>
      <SliderValue data-kit="value" />
    </div>
    <SliderControl data-kit="slider-control">
      <span data-kit="slider-scale" aria-hidden="true"></span>
      <SliderTrack data-kit="slider-track">
        <SliderIndicator data-kit="slider-indicator" />
        <SliderThumb data-kit="slider-thumb" />
      </SliderTrack>
    </SliderControl>
  </Slider>
</Field>

<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Loader2 } from '@lucide/svelte';
  import { Switch } from '$lib/components/ui/switch';
  import { cn } from '$lib/utils';

  /**
   * A GroupedList row whose control is a switch, rendered as a `<label>` so a tap anywhere on the
   * row flips it (the settings invariant PeopleCard's capability rows share) — GroupedList.Row
   * renders a `<div>` for a switch row, where only the switch itself answers. Same grid, heights
   * and inset hairline as GroupedList.Row, so the two mix in one section. Keep any second control
   * out of it: a label forwards every click to its first control.
   *
   * Fully controlled: the switch always shows `checked`, and a flip only asks the parent to
   * change it. (Passing `checked` down one-way let the Switch keep its own flipped copy, so a
   * refused save left it showing a state the server had rejected.)
   */
  type Props = {
    label: string;
    /** A second line: what turning it on does (text-subheadline, the row grows to 56px+). */
    sublabel?: string | Snippet;
    checked: boolean;
    disabled?: boolean;
    /** A save is in flight: a spinner sits before the switch (the switch stays where it was put). */
    busy?: boolean;
    onCheckedChange: (checked: boolean) => void;
    /** Defaults to the label. */
    ariaLabel?: string;
    /** Custom leading content (a status dot, a tile). */
    leading?: Snippet;
    class?: string;
  };

  const {
    label,
    sublabel,
    checked,
    disabled = false,
    busy = false,
    onCheckedChange,
    ariaLabel,
    leading,
    class: className
  }: Props = $props();

  // The sublabel is the switch's description: its aria-label (the row's label, or a longer
  // spoken name) replaces the <label>'s text as the name, so without this VoiceOver would drop
  // "Free · iTunes catalog search" or the missing-API-key warning.
  const id = $props.id();
</script>

<label
  data-slot="grouped-list-row"
  class={cn(
    'relative grid w-full grid-cols-[auto_minmax(0,1fr)_auto_auto] items-center px-4 py-2 text-left select-none',
    sublabel ? 'min-h-14' : 'min-h-11',
    'after:bg-separator after:col-start-2 after:col-end-5 after:row-start-1 after:-mr-4 after:-mb-2 after:h-(--hairline) after:self-end last:after:hidden',
    disabled ? 'cursor-not-allowed' : 'cursor-pointer',
    className
  )}
>
  {#if leading}
    <span class="col-start-1 row-start-1 mr-3 flex items-center">{@render leading()}</span>
  {/if}
  <span class="col-start-2 row-start-1 flex min-w-0 flex-col justify-center py-0.5">
    <span
      class={cn('text-body md:text-sm', disabled ? 'text-muted-foreground-dim' : 'text-foreground')}
    >
      {label}
    </span>
    {#if sublabel}
      <span id="{id}-desc" class="text-subheadline text-muted-foreground md:text-xs">
        {#if typeof sublabel === 'string'}{sublabel}{:else}{@render sublabel()}{/if}
      </span>
    {/if}
  </span>
  {#if busy}
    <!-- Only while saving: an empty cell would still push the switch 12px left. -->
    <span class="col-start-3 row-start-1 ml-3 flex items-center">
      <Loader2 class="text-muted-foreground size-4 animate-spin" aria-hidden="true" />
      <span class="sr-only">Saving</span>
    </span>
  {/if}
  <span class="col-start-4 row-start-1 ml-3 flex items-center">
    <Switch
      bind:checked={() => checked, (v) => onCheckedChange(v)}
      {disabled}
      aria-label={ariaLabel ?? label}
      aria-describedby={sublabel ? `${id}-desc` : undefined}
      aria-busy={busy || undefined}
    />
  </span>
</label>

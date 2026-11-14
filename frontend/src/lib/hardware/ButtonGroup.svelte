<script lang="ts" module>
  import type { IconName } from './Icon.svelte';

  export interface ButtonGroupPart {
    label: string;
    icon: IconName;
    onPress: () => void;
  }
</script>

<script lang="ts">
  // Several buttons on one physical cap, like the + and − of a remote: each zone is its own UI SDK
  // button, divided by an engraved line. Pressing an end tilts the whole cap toward it.
  import { Button } from '@ui-sdk/svelte/button';
  import Icon from './Icon.svelte';

  let {
    label,
    parts,
    orientation = 'horizontal',
    tone = 'secondary'
  }: {
    label: string;
    parts: ButtonGroupPart[];
    orientation?: 'horizontal' | 'vertical';
    tone?: 'primary' | 'secondary';
  } = $props();

  let root: HTMLDivElement;

  // The tilt is worked out from the cap's width, so every cap's ends travel the same distance.
  $effect(() => {
    const el = root;
    const measure = () => el.style.setProperty('--cap-w', `${el.offsetWidth}px`);
    measure();
    const observer = new ResizeObserver(measure);
    observer.observe(el);
    return () => observer.disconnect();
  });
</script>

<div
  bind:this={root}
  data-kit="rocker"
  data-orientation={orientation}
  data-tone={tone}
  role="group"
  aria-label={label}
>
  <div data-kit="rocker-cap">
    {#each parts as part, i (i)}
      <Button data-kit="rocker-part" aria-label={part.label} onclick={() => part.onPress()}>
        <Icon name={part.icon} />
      </Button>
    {/each}
  </div>
</div>

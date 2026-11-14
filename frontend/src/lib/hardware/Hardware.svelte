<script lang="ts">
  // The root of the SPEC-01 hardware skin (vendored in ./skin). Everything inside it is styled by the
  // skin, and the page-wide behaviors it relies on (haptics, opt-in sounds, keyboard-only focus
  // rings, the block caret) are installed once. The key colour is an OKLCH lightness, chroma and
  // hue; without it the keys are black.
  import type { Snippet } from 'svelte';
  import './skin/skin.css';
  import './musichoarder.css';
  import { installHardware } from './behaviors/install.ts';

  let {
    keyColor,
    bare = false,
    class: className,
    children
  }: {
    keyColor?: { l: number; c: number; h: number };
    /** No dotted desk behind it, and its content centred: for use on an app page. */
    bare?: boolean;
    class?: string;
    children: Snippet;
  } = $props();

  $effect(() => {
    installHardware();
  });
</script>

<div
  data-skin="hardware"
  data-mh-bare={bare || undefined}
  class={className}
  style:--theme-l={keyColor?.l}
  style:--theme-c={keyColor?.c}
  style:--theme-h={keyColor?.h}
>
  {@render children()}
</div>

<script lang="ts">
  // A screen set in a device: a thick bezel around bright content.
  import type { Snippet } from 'svelte';

  let {
    label,
    aspect = '16 / 10',
    children
  }: { label?: string; aspect?: string; children: Snippet } = $props();
</script>

<figure data-kit="display">
  <div data-kit="display-bezel">
    <div data-kit="display-screen" style:aspect-ratio={aspect === 'auto' ? undefined : aspect}>
      {@render children()}
    </div>
  </div>
  {#if label}<figcaption data-kit="display-label">{label}</figcaption>{/if}
</figure>

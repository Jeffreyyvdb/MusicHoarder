<script lang="ts">
  import { cn } from '$lib/utils';

  /**
   * The vinyl-disc mark — the same drawing as static/icon.svg, which is the favicon and (via the
   * PNGs rendered from it) the Home Screen icon. It is the one element carried from the landing
   * nav through sign-in, invite and error screens into the installed app, so a visitor meets the
   * same mark at every door.
   *
   * icon.svg swaps its colours with prefers-color-scheme; this uses the theme tokens instead, so it
   * follows the in-app theme toggle as well as the system setting. Always decorative: the product
   * name is next to it or in the page title.
   */
  let { class: className }: { class?: string } = $props();
</script>

<svg
  viewBox="0 0 180 180"
  fill="none"
  aria-hidden="true"
  focusable="false"
  class={cn('block size-7 shrink-0', className)}
>
  <rect width="180" height="180" rx="36" class="fill-foreground" />
  <g transform="translate(90 90)">
    <circle r="70" class="fill-background" />
    <circle r="58" class="stroke-muted-foreground" stroke-width="1" opacity="0.5" />
    <circle r="48" class="stroke-muted-foreground" stroke-width="0.75" opacity="0.35" />
    <circle r="38" class="stroke-muted-foreground" stroke-width="0.5" opacity="0.25" />
    <circle r="22" class="fill-foreground" />
    <circle r="8" class="fill-background" />
  </g>
</svg>

<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Maximize2 } from '@lucide/svelte';

  /**
   * Mobile lyrics preview card (Apple Music / Spotify style): a compact live
   * karaoke preview with a bottom fade, expandable to the fullscreen lyrics
   * overlay. The parent renders the (keyed) LyricsPanel as children so each
   * surface keeps its own lyric-source and mounting logic — e.g. the public
   * share page only mounts the panel once anonymously-fetched text arrived.
   */
  type Props = {
    /** Whether tapping the card opens the fullscreen overlay. */
    expandable?: boolean;
    onExpand?: () => void;
    children: Snippet;
  };
  const { expandable = false, onExpand, children }: Props = $props();
</script>

<button
  type="button"
  class="bg-foreground/5 relative block w-full overflow-hidden rounded-2xl text-left"
  onclick={() => {
    if (expandable) onExpand?.();
  }}
  aria-label="Show fullscreen lyrics"
>
  <div class="flex items-center justify-between px-4 pt-3.5 pb-1">
    <h2 class="text-muted-foreground text-xs font-semibold tracking-widest uppercase">Lyrics</h2>
    {#if expandable}
      <Maximize2 class="text-muted-foreground size-4" />
    {/if}
  </div>
  <div class="pointer-events-none relative h-72 px-3 pb-3">
    {@render children()}
    {#if expandable}
      <!-- Bottom fade hinting there's more to see fullscreen -->
      <div
        class="from-background/0 via-background/0 to-background/25 absolute inset-x-0 bottom-0 h-12 rounded-b-2xl bg-gradient-to-b"
      ></div>
    {/if}
  </div>
</button>

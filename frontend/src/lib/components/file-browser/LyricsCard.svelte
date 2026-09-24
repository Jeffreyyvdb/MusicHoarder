<script lang="ts">
  import type { Snippet } from 'svelte';
  import { Maximize2 } from '@lucide/svelte';

  /**
   * The share page's lyrics preview card (Apple Music / Spotify style): a compact live karaoke
   * preview with a bottom fade, expandable to the fullscreen lyrics overlay. The parent renders the
   * (keyed) LyricsPanel as children so each surface keeps its own lyric-source and mounting logic —
   * the public share page only mounts the panel once anonymously-fetched text arrived.
   *
   * It is a card, not a button: the panel inside can carry real controls (Retry, "Check LRCLIB
   * again"), which a button-wrapped, pointer-events-none body made untappable and invalid. The
   * explicit Expand button is the accessible way in; a tap on the lyrics themselves expands too.
   */
  type Props = {
    /** Whether the card offers the fullscreen overlay. */
    expandable?: boolean;
    onExpand?: () => void;
    children: Snippet;
  };
  const { expandable = false, onExpand, children }: Props = $props();

  function onBodyClick(event: MouseEvent) {
    if (!expandable) return;
    // A control inside the panel keeps its own click.
    if (event.target instanceof Element && event.target.closest('button, a, [role="button"]'))
      return;
    onExpand?.();
  }
</script>

<section class="bg-muted relative w-full overflow-hidden rounded-2xl" aria-label="Lyrics">
  <div class="flex min-h-11 items-center justify-between pr-1 pl-4">
    <h2 class="text-footnote text-muted-foreground font-semibold">Lyrics</h2>
    {#if expandable}
      <button
        type="button"
        class="text-muted-foreground hover:text-foreground focus-visible:ring-ring flex size-11 items-center justify-center rounded-full outline-none focus-visible:ring-2"
        aria-label="Show fullscreen lyrics"
        onclick={onExpand}
      >
        <Maximize2 class="size-4" />
      </button>
    {/if}
  </div>
  <!-- A pointer convenience only; the Expand button above is the keyboard/VoiceOver path. -->
  <div
    role="presentation"
    class="relative h-72 px-3 pb-3 {expandable ? 'cursor-pointer' : ''}"
    onclick={onBodyClick}
  >
    {@render children()}
    {#if expandable}
      <!-- Bottom fade hinting there's more to see fullscreen -->
      <div
        class="from-background/0 to-background/25 pointer-events-none absolute inset-x-0 bottom-0 h-12 bg-gradient-to-b"
      ></div>
    {/if}
  </div>
</section>

<script lang="ts">
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { cn } from '$lib/utils';

  /**
   * Now Playing's artwork. Given `playing`, it is the hero: it fills its box, and settles back to
   * 85% with a lighter shadow while playback is paused, the way Apple Music signals pause without
   * another glyph (400ms on the presentation curve; the global Reduce Motion clamp makes it a
   * cut). Without `playing` it is a plain thumbnail (the condensed header).
   */
  type Props = {
    artist: string;
    title: string;
    coverUrl: string | null;
    /** Pixel size the thumbnail is requested at (the box itself is sized by `class`). */
    size: number;
    corner: number;
    /** Hero mode: true while the shown song is playing. Omit for a thumbnail. */
    playing?: boolean;
    class?: string;
  };
  const { artist, title, coverUrl, size, corner, playing, class: className }: Props = $props();

  const hero = $derived(playing !== undefined);
</script>

{#if hero}
  <div
    class={cn(
      'aspect-square transition-transform duration-[400ms] ease-[cubic-bezier(0.32,0.72,0,1)] will-change-transform',
      playing ? 'scale-100' : 'scale-[0.85]',
      className
    )}
  >
    <Cover
      {artist}
      {title}
      {coverUrl}
      {size}
      {corner}
      caption={false}
      class={cn(
        '!h-full !w-full transition-shadow duration-[400ms]',
        playing ? '!shadow-[0_24px_60px_rgb(0_0_0/0.5)]' : '!shadow-[0_10px_28px_rgb(0_0_0/0.35)]'
      )}
    />
  </div>
{:else}
  <Cover {artist} {title} {coverUrl} {size} {corner} caption={false} class={className} />
{/if}

<script lang="ts">
  import type { Snippet } from 'svelte';
  import { fly } from 'svelte/transition';
  import { ChevronDown } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import Cover from './Cover.svelte';
  import SongTransport from './SongTransport.svelte';
  import { motionFly } from '$lib/motion';

  /**
   * The share page's fullscreen lyrics (Apple Music / Spotify style): just the lyrics over an
   * ambient-artwork backdrop, with a mini header and a scrubber + play/pause bottom bar. (Now
   * Playing no longer uses it — lyrics are a mode of the player there.) The parent renders the
   * (keyed) LyricsPanel as children so it keeps its own lyric-source logic.
   *
   * Mount conditionally ({#if expanded}) — this component handles the enter/exit
   * transition, body scroll lock, and Escape-to-close while mounted.
   */
  type Props = {
    title: string;
    artist: string;
    /** Album (or track) title used for the cover thumb's fallback tile. */
    coverTitle: string;
    coverUrl: string | null;
    ambientUrl: string | null;
    isActive: boolean;
    isPlaying: boolean;
    fallbackDuration: number;
    onPlayToggle: () => void;
    onClose: () => void;
    children: Snippet;
  };
  const {
    title,
    artist,
    coverTitle,
    coverUrl,
    ambientUrl,
    isActive,
    isPlaying,
    fallbackDuration,
    onPlayToggle,
    onClose,
    children
  }: Props = $props();

  // Lock body scroll while the overlay is open. Harmless when a host dialog
  // already locks it — the previous value is restored on unmount.
  $effect(() => {
    const prev = document.body.style.overflow;
    document.body.style.overflow = 'hidden';
    return () => {
      document.body.style.overflow = prev;
    };
  });
</script>

<!-- Capture phase + stopPropagation so Escape closes only this overlay, not a host
     dialog (the in-app track panel lives inside a bits-ui Dialog that also listens
     for Escape at the document level). -->
<svelte:window
  onkeydowncapture={(e) => {
    if (e.key === 'Escape') {
      e.stopPropagation();
      onClose();
    }
  }}
/>

<div
  class="bg-background fixed inset-0 z-50 flex flex-col lg:hidden"
  transition:fly={motionFly({ y: 32, duration: 220 })}
>
  {#if ambientUrl}
    <img
      src={ambientUrl}
      alt=""
      aria-hidden="true"
      class="pointer-events-none absolute inset-0 size-full scale-110 object-cover opacity-50 blur-3xl"
    />
  {/if}
  <div class="bg-background/85 pointer-events-none absolute inset-0"></div>

  <div
    class="relative z-10 flex min-h-0 flex-1 flex-col px-5 pt-[max(1rem,env(safe-area-inset-top))]"
  >
    <div class="flex shrink-0 items-center gap-3 pb-2">
      <Cover
        {artist}
        title={coverTitle}
        {coverUrl}
        size={44}
        corner={8}
        caption={false}
        class="shrink-0 !shadow-md"
      />
      <div class="min-w-0 flex-1">
        <h2 class="text-headline truncate">{title}</h2>
        <p class="text-subheadline text-muted-foreground truncate">{artist}</p>
      </div>
      <!-- 44pt target around a 32px circle. -->
      <Button
        variant="ghost"
        size="icon"
        class="group/close -mr-1.5 size-11 shrink-0 rounded-full hover:bg-transparent"
        onclick={onClose}
        aria-label="Close fullscreen lyrics"
      >
        <span
          class="bg-secondary group-hover/close:bg-secondary-hover flex size-8 items-center justify-center rounded-full"
        >
          <ChevronDown class="size-5" />
        </span>
      </Button>
    </div>

    <div class="flex min-h-0 flex-1 flex-col">
      {@render children()}
    </div>

    <div class="shrink-0 pt-2 pb-[max(1.25rem,env(safe-area-inset-bottom))]">
      <SongTransport minimal speedAlways {isActive} {isPlaying} {fallbackDuration} {onPlayToggle} />
    </div>
  </div>
</div>

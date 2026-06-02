<script lang="ts">
  import { albumTint } from '$lib/album-tint';
  import { computeInitials } from '$lib/formatters';
  import { cn } from '$lib/utils';

  type Props = {
    artist: string;
    title: string;
    size?: number;
    corner?: number;
    coverUrl?: string | null;
    /** Show the artist caption strip across the bottom of the cover. Only renders at size ≥ 120. */
    caption?: boolean;
    /** Show subtle hover-elevation. */
    interactive?: boolean;
    class?: string;
  };

  const {
    artist,
    title,
    size = 176,
    corner = 6,
    coverUrl = null,
    caption = true,
    interactive = false,
    class: className
  }: Props = $props();

  const tint = $derived(albumTint(artist || 'Unknown', title || 'Unknown'));
  const initials = $derived(computeInitials(title));
  const showCaption = $derived(caption && size >= 120);
  let imgFailed = $state(false);
  let imgLoaded = $state(false);

  // A real cover is painted — hide the initials/caption fallback. Stays false while the image
  // lazy-loads and after an error, so the letters remain the graceful fallback in both cases.
  const showArt = $derived(!!coverUrl && imgLoaded && !imgFailed);

  // The single Cover in the player/timeline is reused across songs; reset load state when the
  // source changes so a new cover doesn't inherit the previous image's loaded/failed flags
  // (a stale imgFailed would otherwise suppress the next valid image entirely).
  $effect(() => {
    // eslint-disable-next-line @typescript-eslint/no-unused-expressions -- read to track the dep
    coverUrl;
    imgFailed = false;
    imgLoaded = false;
  });

  function hideOnError() {
    imgFailed = true;
  }
</script>

<div
  class={cn(
    'mh-cover relative grid place-items-center overflow-hidden shadow-sm',
    interactive && 'transition-shadow hover:shadow-md',
    className
  )}
  style="width: {size}px; height: {size}px; border-radius: {corner}px; background: linear-gradient(135deg, {tint.from} 0%, {tint.to} 100%);"
>
  <div class="mh-cover-grain pointer-events-none absolute inset-0"></div>

  {#if coverUrl && !imgFailed}
    <img
      src={coverUrl}
      alt=""
      loading="lazy"
      onload={() => (imgLoaded = true)}
      onerror={hideOnError}
      class="absolute inset-0 block size-full object-cover"
    />
  {/if}

  {#if !showArt}
    <div
      class="relative z-[2] font-bold tracking-[-0.04em] text-white/95 [text-shadow:_0_1px_2px_rgba(0,0,0,0.2)]"
      style="font-size: {size / 3.6}px;"
    >
      {initials}
    </div>
  {/if}

  {#if showCaption && !showArt}
    <div
      class="absolute right-[8%] bottom-[7%] left-[8%] z-[2] truncate text-center font-mono font-medium tracking-[0.08em] text-white/75 uppercase"
      style="font-size: {Math.max(8, size / 22)}px;"
    >
      {artist}
    </div>
  {/if}
</div>

<style>
  .mh-cover-grain {
    background:
      radial-gradient(circle at 30% 20%, rgba(255, 255, 255, 0.25), transparent 50%),
      radial-gradient(circle at 70% 80%, rgba(0, 0, 0, 0.2), transparent 50%);
  }
</style>

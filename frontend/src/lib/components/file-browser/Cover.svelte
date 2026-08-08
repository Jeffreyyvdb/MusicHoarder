<script lang="ts">
  import { albumTint } from '$lib/album-tint';
  import { coverThumbUrl } from '$lib/api-client';
  import { computeInitials } from '$lib/formatters';
  import { cn } from '$lib/utils';

  type Props = {
    artist: string;
    title: string;
    size?: number;
    corner?: number;
    coverUrl?: string | null;
    /** Second-chance image when `coverUrl` errors (e.g. artist portrait 404 → album cover). */
    fallbackUrl?: string | null;
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
    fallbackUrl = null,
    caption = true,
    interactive = false,
    class: className
  }: Props = $props();

  const tint = $derived(albumTint(artist || 'Unknown', title || 'Unknown'));
  const initials = $derived(computeInitials(title));
  const showCaption = $derived(caption && size >= 120);

  let primaryFailed = $state(false);
  let fallbackFailed = $state(false);

  // The image currently on offer: the primary URL until it errors, then the fallback until it
  // errors too, then nothing (initials tile).
  const activeUrl = $derived(
    !primaryFailed && coverUrl ? coverUrl : !fallbackFailed ? fallbackUrl : null
  );

  // Request a thumbnail sized for the display box × device pixel ratio (capped at 2× — enough for
  // retina, far smaller than the multi-MB original). External cover URLs pass through unchanged.
  const dpr = typeof window !== 'undefined' ? Math.min(window.devicePixelRatio || 1, 2) : 1;
  const resolvedCoverUrl = $derived(coverThumbUrl(activeUrl, Math.round(size * dpr)));

  // When a cover is expected we show only the tinted tile (no initials/caption) while it loads and
  // fade the image in over it — so scrolling a virtualized grid doesn't flash the big letters before
  // each cover paints. The initials/caption are the fallback only when there's no cover (or it errors).
  // Driven off the URLs (known synchronously), not load events, so it's robust to recycled cards.
  const hasCover = $derived(!!activeUrl);

  // Clear the failure flags when the sources change so a reused card doesn't suppress the next image.
  $effect(() => {
    // eslint-disable-next-line @typescript-eslint/no-unused-expressions -- read to track the deps
    void coverUrl, void fallbackUrl;
    primaryFailed = false;
    fallbackFailed = false;
  });
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

  {#if !hasCover}
    <div
      class="relative z-[2] font-bold tracking-[-0.04em] text-white/95 [text-shadow:_0_1px_2px_rgba(0,0,0,0.2)]"
      style="font-size: {size / 3.6}px;"
    >
      {initials}
    </div>

    {#if showCaption}
      <div
        class="absolute right-[8%] bottom-[7%] left-[8%] z-[2] truncate text-center font-mono font-medium tracking-[0.08em] text-white/75 uppercase"
        style="font-size: {Math.max(8, size / 22)}px;"
      >
        {artist}
      </div>
    {/if}
  {/if}

  <!-- Top layer: fades in over the tinted tile so covers appear smoothly instead of snapping in. -->
  {#if resolvedCoverUrl}
    <img
      src={resolvedCoverUrl}
      alt=""
      width={size}
      height={size}
      loading="lazy"
      decoding="async"
      onerror={() => {
        // Attribute the error to whichever URL is currently showing (mirrors activeUrl).
        if (!primaryFailed && coverUrl) primaryFailed = true;
        else fallbackFailed = true;
      }}
      class="mh-cover-img absolute inset-0 z-[3] block size-full object-cover"
    />
  {/if}
</div>

<style>
  .mh-cover-grain {
    background:
      radial-gradient(circle at 30% 20%, rgba(255, 255, 255, 0.25), transparent 50%),
      radial-gradient(circle at 70% 80%, rgba(0, 0, 0, 0.2), transparent 50%);
  }

  /* Ease the cover in over the tinted tile so scrolling doesn't snap-flash each image. */
  .mh-cover-img {
    animation: mh-cover-fade 240ms ease-out;
  }

  @keyframes mh-cover-fade {
    from {
      opacity: 0;
    }
    to {
      opacity: 1;
    }
  }

  @media (prefers-reduced-motion: reduce) {
    .mh-cover-img {
      animation: none;
    }
  }
</style>

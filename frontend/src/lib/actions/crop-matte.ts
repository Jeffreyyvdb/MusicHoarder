/**
 * `use:cropMatte` — crops the black bars baked into a music video out of an `object-cover` fill.
 *
 * Plenty of music videos are films mastered into a 16:9 upload: a 2.39:1 picture with black bars
 * top and bottom (a letterbox), or a 4:3 one with bars at the sides (a pillarbox). `object-cover`
 * fills the box with the whole frame, bars included, so on a phone the letterbox lands as two
 * solid black bands across Now Playing whose edge cuts straight through whatever sits there. The
 * server measures the bars once per video (`letterbox` / `pillarbox` on the video info: the share
 * of the frame each bar of the pair covers), and this scales the video up about its centre until
 * the picture alone covers the box.
 *
 * The rule is shared with Android's `videoChildSize` (PlayerVideo.kt); keep the two in step.
 *
 * SSR-safe: actions only run in the browser.
 */

export type Matte = {
  /** Height of each bar at the top and bottom, as a share of the frame; null when unmeasured. */
  letterbox?: number | null;
  /** Width of each bar at the sides, as a share of the frame; null when unmeasured. */
  pillarbox?: number | null;
};

/** A bar pair has to leave some picture between it; anything else is not a measurement. */
function barShare(value: number | null | undefined): number {
  return typeof value === 'number' && Number.isFinite(value) && value > 0 && value < 0.5
    ? value
    : 0;
}

/**
 * How much to scale an `object-cover` video, about its centre, so its picture — the frame less
 * its bars — covers a `boxWidth` × `boxHeight` box. 1 when there is nothing to crop, the sizes are
 * not known yet, or the bars already hang outside the box (a pillarbox on a portrait phone).
 */
export function matteScale(
  boxWidth: number,
  boxHeight: number,
  frameWidth: number,
  frameHeight: number,
  matte: Matte
): number {
  if (!(boxWidth > 0 && boxHeight > 0 && frameWidth > 0 && frameHeight > 0)) return 1;
  const letterbox = barShare(matte.letterbox);
  const pillarbox = barShare(matte.pillarbox);
  if (letterbox === 0 && pillarbox === 0) return 1;

  const cover = Math.max(boxWidth / frameWidth, boxHeight / frameHeight);
  const pictureCover = Math.max(
    boxWidth / (frameWidth * (1 - 2 * pillarbox)),
    boxHeight / (frameHeight * (1 - 2 * letterbox))
  );
  return pictureCover / cover;
}

/**
 * Keeps the video's scale in step with its box (a ResizeObserver: rotation, a window resize),
 * its frame size (known from `loadedmetadata`, and a stream may change it mid-play) and the
 * measurement. The transform leaves layout alone, so the element's own box is the box to cover;
 * whatever it scales past is clipped by the backdrop's `overflow-hidden` container.
 */
export function cropMatte(
  node: HTMLVideoElement,
  matte: Matte
): { update(next: Matte): void; destroy(): void } {
  let current = matte;

  function apply() {
    const scale = matteScale(
      node.clientWidth,
      node.clientHeight,
      node.videoWidth,
      node.videoHeight,
      current
    );
    node.style.transform = scale > 1.001 ? `scale(${scale})` : '';
  }

  const observer = typeof ResizeObserver === 'undefined' ? null : new ResizeObserver(apply);
  observer?.observe(node);
  node.addEventListener('loadedmetadata', apply);
  node.addEventListener('resize', apply);
  apply();

  return {
    update(next: Matte) {
      current = next;
      apply();
    },
    destroy() {
      observer?.disconnect();
      node.removeEventListener('loadedmetadata', apply);
      node.removeEventListener('resize', apply);
      node.style.transform = '';
    }
  };
}

/**
 * Now Playing's legibility rule. Its text is white over a blurred wash of the cover, so a dim
 * layer (`rgb(0 0 0 / a)`) sits between them, sized per cover: just enough to bring the
 * BRIGHTEST part of the wash down to about 70/255 grey. At that level white text clears 9.4:1,
 * the 72% secondary tone 5.8:1 and the 55% inactive lyric lines 4.2:1 (24px bold, which needs 3:1)
 * — everywhere on the screen, not just on average.
 *
 *   a = clamp(0.35, 1 − 70/g, 0.75)   with g = the brightest cell of an 8×8 sample
 *
 * A first cut used the cover's AVERAGE grey. A cover with one bright region (a pale disc
 * on a dark field) averages low, gets a light dim, and leaves the wash bright exactly where a
 * lyric line lands: 2.4:1 was measured on a test cover. The brightest cell is what text can
 * actually sit on, so that is what sizes the dim. Brightness is measured as WCAG does — relative
 * luminance, then back to the sRGB grey with the same luminance — because a saturated green is far
 * brighter than its Rec. 601 luma says. Dimming with black scales every channel alike, and the
 * sRGB curve is close to a power law, so "equivalent grey × (1 − a)" tracks the dimmed luminance.
 *
 * A dark cover still gets the 0.35 floor, so the wash never looks like an unlit screen; a white
 * cover lands at ≈0.73, under the 0.75 ceiling, still reading as that cover. A cover that cannot
 * be measured (no art, a cross-origin URL without CORS) gets the white-cover dim: with nothing
 * known, only the worst case keeps the promise.
 */

export const DIM_TARGET_GREY = 70;
export const DIM_MIN = 0.35;
export const DIM_MAX = 0.75;
export const DIM_UNKNOWN = 0.73;

/** The dim alpha for a wash whose brightest sRGB-equivalent grey (0–255) is `grey`; null → unknown. */
export function dimAlphaForGrey(grey: number | null): number {
  if (grey === null || !Number.isFinite(grey)) return DIM_UNKNOWN;
  if (grey <= 0) return DIM_MIN;
  return Math.min(DIM_MAX, Math.max(DIM_MIN, 1 - DIM_TARGET_GREY / grey));
}

function toLinear(channel: number): number {
  const c = channel / 255;
  return c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
}

function toSrgb(linear: number): number {
  const c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * linear ** (1 / 2.4) - 0.055;
  return Math.min(255, Math.max(0, c * 255));
}

/** The sRGB grey (0–255) with the same WCAG relative luminance as this colour. */
export function equivalentGrey(r: number, g: number, b: number): number {
  return toSrgb(0.2126 * toLinear(r) + 0.7152 * toLinear(g) + 0.0722 * toLinear(b));
}

/**
 * The brightest pixel of RGBA data (each pixel of the 8×8 sample is already the average of an
 * eighth of the cover each way), as an equivalent grey. Translucent pixels are composited over
 * the overlay's black, which is what sits under the wash. Null for empty data.
 */
export function brightestGrey(data: ArrayLike<number>): number | null {
  let max: number | null = null;
  for (let i = 0; i + 3 < data.length; i += 4) {
    const alpha = data[i + 3] / 255;
    const grey = equivalentGrey(data[i] * alpha, data[i + 1] * alpha, data[i + 2] * alpha);
    if (max === null || grey > max) max = grey;
  }
  return max;
}

const cache = new Map<string, number>();

function isCrossOrigin(url: string): boolean {
  try {
    return new URL(url, location.href).origin !== location.origin;
  } catch {
    return false;
  }
}

/**
 * Sample the cover once, from an 8×8 canvas of the thumbnail the wash already loads, and resolve
 * to its dim alpha. Never rejects: a cover that fails to load, or taints the canvas, resolves to
 * {@link DIM_UNKNOWN}. A cross-origin cover is requested with CORS, so a CDN that allows it can be
 * measured too. Browser-only.
 */
export function coverDimAlpha(url: string | null): Promise<number> {
  if (!url || typeof document === 'undefined') return Promise.resolve(DIM_UNKNOWN);
  const known = cache.get(url);
  if (known !== undefined) return Promise.resolve(known);
  return new Promise((resolve) => {
    const img = new Image();
    img.decoding = 'async';
    if (isCrossOrigin(url)) img.crossOrigin = 'anonymous';
    img.onload = () => {
      let alpha = DIM_UNKNOWN;
      try {
        const canvas = document.createElement('canvas');
        canvas.width = 8;
        canvas.height = 8;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        if (ctx) {
          ctx.imageSmoothingQuality = 'high';
          ctx.drawImage(img, 0, 0, 8, 8);
          alpha = dimAlphaForGrey(brightestGrey(ctx.getImageData(0, 0, 8, 8).data));
        }
      } catch {
        // SecurityError: a cross-origin cover tainted the canvas.
      }
      cache.set(url, alpha);
      resolve(alpha);
    };
    img.onerror = () => resolve(DIM_UNKNOWN);
    img.src = url;
  });
}

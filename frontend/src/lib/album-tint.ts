/**
 * Deterministic per-album color tint used for cover gradients and the
 * Spotify-style hero on the album page. Same (artist, title) tuple always
 * yields the same tint so albums look visually distinct without per-album
 * configuration.
 *
 * Lightness is clamped to a "dark enough to render white text on" range so
 * the hero stays legible regardless of the hashed hue.
 */

export type AlbumTint = {
  /** Top-left / start of the gradient — darker. */
  from: string;
  /** Bottom-right / end of the gradient — lighter complement. */
  to: string;
  /** Hash-derived hue in degrees, useful for accents. */
  hue: number;
};

const FROM_L_MIN = 0.3;
const FROM_L_MAX = 0.45;
const TO_L_MIN = 0.55;
const TO_L_MAX = 0.7;
const C_MIN = 0.1;
const C_MAX = 0.18;

/** 32-bit string hash. Compact + collision-resistant enough for this use. */
function cyrb53(str: string, seed = 0): number {
  let h1 = 0xdeadbeef ^ seed;
  let h2 = 0x41c6ce57 ^ seed;
  for (let i = 0, ch; i < str.length; i++) {
    ch = str.charCodeAt(i);
    h1 = Math.imul(h1 ^ ch, 2654435761);
    h2 = Math.imul(h2 ^ ch, 1597334677);
  }
  h1 = Math.imul(h1 ^ (h1 >>> 16), 2246822507) ^ Math.imul(h2 ^ (h2 >>> 13), 3266489909);
  h2 = Math.imul(h2 ^ (h2 >>> 16), 2246822507) ^ Math.imul(h1 ^ (h1 >>> 13), 3266489909);
  return 4294967296 * (2097151 & h2) + (h1 >>> 0);
}

function lerp(min: number, max: number, t: number): number {
  return min + (max - min) * t;
}

export function albumTint(artist: string, title: string): AlbumTint {
  const key = `${artist.trim().toLowerCase()}::${title.trim().toLowerCase()}`;
  const hash = cyrb53(key);
  // Spread three independent values out of the hash.
  const hue = hash % 360;
  const t1 = ((hash >>> 4) % 1000) / 1000;
  const t2 = ((hash >>> 11) % 1000) / 1000;
  const chroma = lerp(C_MIN, C_MAX, t1);
  const lightnessFrom = lerp(FROM_L_MIN, FROM_L_MAX, t2);
  const lightnessTo = lerp(TO_L_MIN, TO_L_MAX, t1);
  const hueTo = (hue + 40) % 360;
  return {
    from: `oklch(${lightnessFrom.toFixed(3)} ${chroma.toFixed(3)} ${hue})`,
    to: `oklch(${lightnessTo.toFixed(3)} ${chroma.toFixed(3)} ${hueTo})`,
    hue
  };
}

/**
 * Convenience: derive both gradient stops at once for backgrounds that use
 * separate from/to references. Suitable for inline `style="--mh-tint-from: …"`.
 */
export function albumTintCssVars(artist: string, title: string): string {
  const t = albumTint(artist, title);
  return `--mh-tint-from: ${t.from}; --mh-tint-to: ${t.to};`;
}

// ── cover-derived tint ─────────────────────────────────────────────────────────
// The hashed tint above is stable but arbitrary: behind a purple record it can wash the album page
// blue. `coverTint` reads the colour off the artwork itself: the same
// small thumbnail every row already loads, averaged in an 8×8 canvas. The hash stays the fallback
// whenever the image cannot be read (a cross-origin cover taints the canvas, a 404, no DOM).

/** sRGB channel (0–255) to linear light. */
function toLinear(c: number): number {
  const v = c / 255;
  return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
}

/** Linear-light sRGB (0–1) to OKLCH (Björn Ottosson's OKLab, then polar). */
function linearToOklch(r: number, g: number, b: number): { l: number; c: number; h: number } {
  const l = Math.cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
  const m = Math.cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
  const s = Math.cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
  const L = 0.2104542553 * l + 0.793617785 * m - 0.0040720468 * s;
  const A = 1.9779984951 * l - 2.428592205 * m + 0.4505937099 * s;
  const B = 0.0259040371 * l + 0.7827717662 * m - 0.808675766 * s;
  const h = (Math.atan2(B, A) * 180) / Math.PI;
  return { l: L, c: Math.hypot(A, B), h: h < 0 ? h + 360 : h };
}

/** A wash never gets louder than the hashed tints do. */
const WASH_C_MAX = C_MAX;

/**
 * The wash colour for a set of RGBA pixels (a canvas's `data`): their average, in linear light,
 * with lightness clamped to the hashed tint's dark range (so the page's text stays legible over it)
 * and chroma capped. A grey record gives a grey wash — chroma is never invented. Transparent pixels
 * are skipped; null when nothing opaque was sampled.
 */
export function washFromPixels(data: ArrayLike<number>): string | null {
  let r = 0;
  let g = 0;
  let b = 0;
  let n = 0;
  for (let i = 0; i + 3 < data.length; i += 4) {
    if (data[i + 3] < 128) continue;
    r += toLinear(data[i]);
    g += toLinear(data[i + 1]);
    b += toLinear(data[i + 2]);
    n += 1;
  }
  if (n === 0) return null;
  const { l, c, h } = linearToOklch(r / n, g / n, b / n);
  const lightness = Math.min(FROM_L_MAX, Math.max(FROM_L_MIN, l));
  const chroma = Math.min(WASH_C_MAX, c);
  return `oklch(${lightness.toFixed(3)} ${chroma.toFixed(3)} ${Math.round(h)})`;
}

const coverTints = new Map<string, Promise<string | null>>();

/**
 * The wash colour of a cover image, or null when it cannot be read. Browser-only (call it from an
 * effect); cached per URL, so revisiting an album costs nothing.
 */
export function coverTint(url: string): Promise<string | null> {
  const cached = coverTints.get(url);
  if (cached) return cached;
  const pending = new Promise<string | null>((resolve) => {
    if (typeof document === 'undefined') return resolve(null);
    const img = new Image();
    img.decoding = 'async';
    img.onload = () => {
      try {
        const canvas = document.createElement('canvas');
        canvas.width = 8;
        canvas.height = 8;
        const ctx = canvas.getContext('2d', { willReadFrequently: true });
        if (!ctx) return resolve(null);
        ctx.drawImage(img, 0, 0, 8, 8);
        resolve(washFromPixels(ctx.getImageData(0, 0, 8, 8).data));
      } catch {
        // A cross-origin cover taints the canvas: getImageData throws. The hash tint stands in.
        resolve(null);
      }
    };
    img.onerror = () => resolve(null);
    img.src = url;
  });
  coverTints.set(url, pending);
  return pending;
}

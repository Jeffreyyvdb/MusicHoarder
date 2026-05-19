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

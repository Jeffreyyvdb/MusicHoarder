#!/usr/bin/env bun
import { deflateSync } from 'node:zlib';
import { writeFileSync } from 'node:fs';
import { join } from 'node:path';

const STATIC_DIR = join(import.meta.dir, '..', 'static');

type RGBA = readonly [number, number, number, number];
const BG: RGBA = [10, 10, 10, 255];
const FG: RGBA = [250, 250, 250, 255];
const GROOVE: RGBA = [82, 82, 82, 255];

function makeCrcTable() {
  const table = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    table[n] = c >>> 0;
  }
  return table;
}
const CRC_TABLE = makeCrcTable();
function crc32(buf: Uint8Array): number {
  let c = 0xffffffff;
  for (let i = 0; i < buf.length; i++) c = CRC_TABLE[(c ^ buf[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

function chunk(type: string, data: Uint8Array): Uint8Array {
  const typeBytes = new Uint8Array(4);
  for (let i = 0; i < 4; i++) typeBytes[i] = type.charCodeAt(i);
  const lenBytes = new Uint8Array(4);
  new DataView(lenBytes.buffer).setUint32(0, data.length, false);
  const crcInput = new Uint8Array(typeBytes.length + data.length);
  crcInput.set(typeBytes, 0);
  crcInput.set(data, typeBytes.length);
  const crc = crc32(crcInput);
  const crcBytes = new Uint8Array(4);
  new DataView(crcBytes.buffer).setUint32(0, crc, false);
  return new Uint8Array([...lenBytes, ...typeBytes, ...data, ...crcBytes]);
}

function encodePng(width: number, height: number, pixels: Uint8Array): Uint8Array {
  const sig = new Uint8Array([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const ihdr = new Uint8Array(13);
  const v = new DataView(ihdr.buffer);
  v.setUint32(0, width, false);
  v.setUint32(4, height, false);
  ihdr[8] = 8; // bit depth
  ihdr[9] = 6; // color type RGBA
  ihdr[10] = 0;
  ihdr[11] = 0;
  ihdr[12] = 0;

  const stride = width * 4;
  const filtered = new Uint8Array((stride + 1) * height);
  for (let y = 0; y < height; y++) {
    filtered[y * (stride + 1)] = 0;
    filtered.set(pixels.subarray(y * stride, (y + 1) * stride), y * (stride + 1) + 1);
  }
  const idat = deflateSync(filtered, { level: 9 });

  const parts = [sig, chunk('IHDR', ihdr), chunk('IDAT', idat), chunk('IEND', new Uint8Array(0))];
  const total = parts.reduce((s, p) => s + p.length, 0);
  const out = new Uint8Array(total);
  let off = 0;
  for (const p of parts) {
    out.set(p, off);
    off += p.length;
  }
  return out;
}

function smoothstep(edge0: number, edge1: number, x: number): number {
  const t = Math.max(0, Math.min(1, (x - edge0) / (edge1 - edge0)));
  return t * t * (3 - 2 * t);
}

function over(dst: RGBA, src: RGBA, srcA: number): RGBA {
  const a = (src[3] / 255) * srcA;
  const ra = 1 - a;
  return [
    Math.round(dst[0] * ra + src[0] * a),
    Math.round(dst[1] * ra + src[1] * a),
    Math.round(dst[2] * ra + src[2] * a),
    Math.min(255, Math.round(dst[3] * ra + 255 * a))
  ];
}

function renderIcon(size: number, opts: { dark: boolean }): Uint8Array {
  // Supersample 4x for anti-aliasing
  const ss = 4;
  const W = size * ss;
  const H = size * ss;
  const bigBuf = new Uint8Array(W * H * 4);

  const cx = W / 2;
  const cy = H / 2;
  const scale = W / 180;

  const cornerRadius = 36 * scale;
  const discR = 70 * scale;
  const groove1R = 58 * scale;
  const groove2R = 48 * scale;
  const groove3R = 38 * scale;
  const labelR = 22 * scale;
  const spindleR = 8 * scale;

  const groove1Width = 1 * scale;
  const groove2Width = 0.75 * scale;
  const groove3Width = 0.5 * scale;

  const bg = opts.dark ? FG : BG;
  const fg = opts.dark ? BG : FG;
  const groove: RGBA = opts.dark ? [163, 163, 163, 255] : GROOVE;

  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      let px: RGBA = [0, 0, 0, 0];

      // Rounded rect background (computed via SDF)
      const halfW = W / 2;
      const halfH = H / 2;
      const dx = Math.abs(x + 0.5 - cx) - (halfW - cornerRadius);
      const dy = Math.abs(y + 0.5 - cy) - (halfH - cornerRadius);
      const ax = Math.max(dx, 0);
      const ay = Math.max(dy, 0);
      const sdRect = Math.sqrt(ax * ax + ay * ay) + Math.min(Math.max(dx, dy), 0) - cornerRadius;
      const rectCoverage = 1 - smoothstep(-1, 1, sdRect);
      px = over(px, bg, rectCoverage);

      // Distance from disc center
      const ddx = x + 0.5 - cx;
      const ddy = y + 0.5 - cy;
      const dist = Math.sqrt(ddx * ddx + ddy * ddy);

      // Vinyl disc (filled)
      const discCoverage = 1 - smoothstep(discR - 1, discR + 1, dist);
      px = over(px, fg, discCoverage);

      // Grooves: ring strokes (only inside the disc; opacity per ring)
      function ring(r: number, width: number, alpha: number) {
        const inner = r - width / 2;
        const outer = r + width / 2;
        const cov =
          smoothstep(inner - 1, inner + 1, dist) - smoothstep(outer - 1, outer + 1, dist);
        if (cov > 0) {
          px = over(px, groove, cov * alpha);
        }
      }
      ring(groove1R, groove1Width, 0.5);
      ring(groove2R, groove2Width, 0.35);
      ring(groove3R, groove3Width, 0.25);

      // Center label (background colored disc)
      const labelCoverage = 1 - smoothstep(labelR - 1, labelR + 1, dist);
      px = over(px, bg, labelCoverage);

      // Spindle hole (foreground colored small disc)
      const spindleCoverage = 1 - smoothstep(spindleR - 1, spindleR + 1, dist);
      px = over(px, fg, spindleCoverage);

      const idx = (y * W + x) * 4;
      bigBuf[idx] = px[0];
      bigBuf[idx + 1] = px[1];
      bigBuf[idx + 2] = px[2];
      bigBuf[idx + 3] = px[3];
    }
  }

  // Downsample by averaging ss x ss blocks
  const out = new Uint8Array(size * size * 4);
  for (let y = 0; y < size; y++) {
    for (let x = 0; x < size; x++) {
      let r = 0,
        g = 0,
        b = 0,
        a = 0;
      for (let oy = 0; oy < ss; oy++) {
        for (let ox = 0; ox < ss; ox++) {
          const i = ((y * ss + oy) * W + (x * ss + ox)) * 4;
          r += bigBuf[i];
          g += bigBuf[i + 1];
          b += bigBuf[i + 2];
          a += bigBuf[i + 3];
        }
      }
      const n = ss * ss;
      const oi = (y * size + x) * 4;
      out[oi] = Math.round(r / n);
      out[oi + 1] = Math.round(g / n);
      out[oi + 2] = Math.round(b / n);
      out[oi + 3] = Math.round(a / n);
    }
  }

  return encodePng(size, size, out);
}

const targets: Array<{ name: string; size: number; dark: boolean }> = [
  { name: 'apple-icon.png', size: 180, dark: false },
  { name: 'icon-light-32x32.png', size: 32, dark: false },
  { name: 'icon-dark-32x32.png', size: 32, dark: true },
  { name: 'og-image.png', size: 1200, dark: false }
];

for (const t of targets) {
  const png = renderIcon(t.size, { dark: t.dark });
  const path = join(STATIC_DIR, t.name);
  writeFileSync(path, png);
  console.log(`wrote ${path} (${png.length} bytes)`);
}

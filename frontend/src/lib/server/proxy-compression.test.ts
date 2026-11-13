import { describe, expect, it } from 'vitest';
import { brotliCompressSync, brotliDecompressSync, constants, gunzipSync } from 'node:zlib';
import { BROTLI_BLOCK_BYTES, compressBody, pickProxyEncoding } from './proxy-compression';

const req = (acceptEncoding: string | null) => new Headers(acceptEncoding === null ? {} : { 'accept-encoding': acceptEncoding });
const json = (extra: Record<string, string> = {}) => new Headers({ 'content-type': 'application/json; charset=utf-8', ...extra });

describe('pickProxyEncoding', () => {
  it('prefers brotli, then gzip, then nothing', () => {
    expect(pickProxyEncoding(req('gzip, deflate, br, zstd'), 200, json())).toBe('br');
    expect(pickProxyEncoding(req('gzip, deflate'), 200, json())).toBe('gzip');
    expect(pickProxyEncoding(req('identity'), 200, json())).toBeNull();
    expect(pickProxyEncoding(req(null), 200, json())).toBeNull();
  });

  it('honours q=0', () => {
    expect(pickProxyEncoding(req('br;q=0, gzip'), 200, json())).toBe('gzip');
    expect(pickProxyEncoding(req('br;q=0, gzip;q=0'), 200, json())).toBeNull();
  });

  it('leaves media, event streams and byte ranges alone', () => {
    expect(pickProxyEncoding(req('br'), 200, new Headers({ 'content-type': 'audio/flac' }))).toBeNull();
    expect(pickProxyEncoding(req('br'), 200, new Headers({ 'content-type': 'text/event-stream' }))).toBeNull();
    expect(pickProxyEncoding(req('br'), 206, json())).toBeNull();
    expect(pickProxyEncoding(req('br'), 304, json())).toBeNull();
  });

  it('skips small, already-encoded and no-transform bodies', () => {
    expect(pickProxyEncoding(req('br'), 200, json({ 'content-length': '212' }))).toBeNull();
    expect(pickProxyEncoding(req('br'), 200, json({ 'content-length': '4096' }))).toBe('br');
    expect(pickProxyEncoding(req('br'), 200, json({ 'content-encoding': 'gzip' }))).toBeNull();
    expect(pickProxyEncoding(req('br'), 200, json({ 'cache-control': 'no-store, no-transform' }))).toBeNull();
  });
});

describe('compressBody', () => {
  const payload = JSON.stringify({ songs: Array.from({ length: 500 }, (_, id) => ({ id, title: `Track ${id}` })) });

  it.each(['br', 'gzip'] as const)('round-trips as %s', async (encoding) => {
    const compressed = Buffer.from(await new Response(compressBody(new Response(payload).body!, encoding)).arrayBuffer());
    expect(compressed.byteLength).toBeLessThan(payload.length / 2);
    const decoded = encoding === 'br' ? brotliDecompressSync(compressed) : gunzipSync(compressed);
    expect(decoded.toString()).toBe(payload);
  });

  /** A body handed over in pieces of `chunkBytes`, the way the API's answer reaches the proxy. */
  const inPieces = (bytes: Uint8Array, chunkBytes: number) =>
    new ReadableStream<Uint8Array>({
      start(controller) {
        for (let at = 0; at < bytes.byteLength; at += chunkBytes) controller.enqueue(bytes.subarray(at, at + chunkBytes));
        controller.close();
      }
    });

  /** Rows shaped like the song list's: mostly repeated keys, with values that differ per row. */
  const rows = (bytes: number) => {
    const out: string[] = [];
    for (let id = 0, size = 0; size < bytes; id++) {
      const row = JSON.stringify({ id, title: `Track ${id}`, artist: `Artist ${id % 97}`, album: `Album ${id % 931}`, durationSeconds: 150 + (id % 240), isBuilt: true, playCount: id % 13 });
      out.push(row);
      size += row.length + 1;
    }
    return Buffer.from(`[${out.join(',')}]`);
  };

  const compressedAs = async (encoding: 'br' | 'gzip', body: ReadableStream<Uint8Array>) =>
    Buffer.from(await new Response(compressBody(body, encoding)).arrayBuffer());

  it.each([
    ['smaller than a block', Math.floor(BROTLI_BLOCK_BYTES / 3)],
    ['exactly a block', BROTLI_BLOCK_BYTES],
    ['several blocks and a remainder', BROTLI_BLOCK_BYTES * 3 + 4321]
  ])('loses nothing from a body %s that arrives in small pieces', async (_name, bytes) => {
    const body = rows(bytes).subarray(0, bytes);
    for (const encoding of ['br', 'gzip'] as const) {
      const compressed = await compressedAs(encoding, inPieces(body, 1500));
      const decoded = encoding === 'br' ? brotliDecompressSync(compressed) : gunzipSync(compressed);
      expect(decoded.equals(body)).toBe(true);
    }
  });

  it('compresses a body that arrives in small pieces as well as one that arrives whole', async () => {
    // Brotli's fast encoder treats each write as a block of its own: fed the API's ~15 kB pieces
    // directly, the song list went out up to 60% larger than the same bytes compressed in one go.
    const body = rows(4 * BROTLI_BLOCK_BYTES);
    const whole = brotliCompressSync(body, { params: { [constants.BROTLI_PARAM_QUALITY]: 1 } }).byteLength;
    const pieces = (await compressedAs('br', inPieces(body, 15_000))).byteLength;
    expect(pieces).toBeLessThan(whole * 1.05);
  });

  it('ends the compressed stream when the upstream read fails', async () => {
    const failing = () =>
      new ReadableStream<Uint8Array>({
        start(controller) {
          controller.enqueue(new TextEncoder().encode(payload));
        },
        pull(controller) {
          controller.error(new Error('upstream went away'));
        }
      });
    for (const encoding of ['br', 'gzip'] as const) {
      await expect(new Response(compressBody(failing(), encoding)).arrayBuffer()).rejects.toThrow();
    }
  });
});

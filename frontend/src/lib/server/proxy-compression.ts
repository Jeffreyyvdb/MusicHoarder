import { Readable, Transform } from 'node:stream';
import { constants, createBrotliCompress, createGzip } from 'node:zlib';

export type ProxyEncoding = 'br' | 'gzip';

/** JSON only: it is what the API's large answers are, and it never needs to arrive piece by piece. */
const COMPRESSIBLE = /^application\/(json|problem\+json)\b/i;

/** Below this a compressed body saves less than its own framing costs. */
const MIN_BYTES = 1024;

/**
 * Which encoding, if any, the proxy should apply to an API response on its way to the client.
 *
 * The API sends JSON uncompressed, and where no CDN sits in front that is what crossed the wire:
 * megabytes for the song list. Media is left alone (already compressed, and a player needs its
 * `content-length` and byte ranges), and so is anything streamed (`text/event-stream`), which a
 * compressor would hold back until its buffer filled.
 */
export function pickProxyEncoding(request: Headers, status: number, response: Headers): ProxyEncoding | null {
  if (status !== 200) return null; // a 206 is a byte range of the original; a 204/304 has no body
  if (response.has('content-encoding')) return null;
  if (!COMPRESSIBLE.test(response.get('content-type') ?? '')) return null;
  if (/\bno-transform\b/i.test(response.get('cache-control') ?? '')) return null;
  const length = response.get('content-length');
  if (length !== null && Number(length) < MIN_BYTES) return null;

  const accepted = new Map<string, number>();
  for (const part of (request.get('accept-encoding') ?? '').split(',')) {
    const [name, ...params] = part.trim().toLowerCase().split(';');
    if (!name) continue;
    const q = params.map((p) => p.trim()).find((p) => p.startsWith('q='));
    accepted.set(name, q ? Number(q.slice(2)) : 1);
  }
  if ((accepted.get('br') ?? 0) > 0) return 'br';
  if ((accepted.get('gzip') ?? 0) > 0) return 'gzip';
  return null;
}

/**
 * How much of a body the brotli encoder is handed at a time. At quality 1 it compresses each write
 * as a block of its own, with almost no memory of the one before, so the ratio follows the size of
 * the writes — and the API's JSON arrives in pieces of about 15 kB. Measured on the 21.8 MB song
 * list: 2.6 to 3.5 MB as it arrives (larger than gzip's 2.3 MB, and different on every request),
 * 2.16 MB in 1 MB blocks, for less compressor time.
 */
export const BROTLI_BLOCK_BYTES = 1024 * 1024;

/**
 * Gathers the pieces a body arrives in into blocks of at least `size` bytes, and passes on what is
 * left when the body ends. Only ever in front of a compressor for a JSON document (see
 * {@link pickProxyEncoding}), which no client can use before its last byte anyway.
 */
function inBlocksOf(size: number): Transform {
  let held: Buffer[] = [];
  let heldBytes = 0;
  return new Transform({
    transform(chunk: Buffer, _encoding, done) {
      held.push(chunk);
      heldBytes += chunk.length;
      if (heldBytes < size) return done();
      const block = Buffer.concat(held, heldBytes);
      held = [];
      heldBytes = 0;
      done(null, block);
    },
    flush(done) {
      if (heldBytes === 0) return done();
      done(null, Buffer.concat(held, heldBytes));
    }
  });
}

/**
 * Compress a response body as it streams. The levels favour speed: the song list is tens of
 * megabytes of JSON, and a slow compressor would cost more than the smaller transfer saves.
 * Measured on a 21 MB list: brotli 1 gives 2.1 MB in a third of the time brotli 4 takes for 1.9 MB
 * — given whole blocks to work on, which is what {@link BROTLI_BLOCK_BYTES} is for.
 */
export function compressBody(body: ReadableStream<Uint8Array>, encoding: ProxyEncoding): ReadableStream<Uint8Array> {
  const compressor =
    encoding === 'br'
      ? createBrotliCompress({ params: { [constants.BROTLI_PARAM_QUALITY]: 1 } })
      : createGzip({ level: 4 });
  const source = Readable.fromWeb(body as Parameters<typeof Readable.fromWeb>[0]);
  let input: Readable = source;
  if (encoding === 'br') {
    const blocks = inBlocksOf(BROTLI_BLOCK_BYTES);
    source.on('error', (error) => blocks.destroy(error));
    input = source.pipe(blocks);
  }
  // A failed upstream read must end the compressed stream too, or the client waits on it forever.
  input.on('error', (error) => compressor.destroy(error));
  return Readable.toWeb(input.pipe(compressor)) as ReadableStream<Uint8Array>;
}

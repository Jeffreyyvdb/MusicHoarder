import { env } from '$env/dynamic/private';
import { assetlinksJson, parseFingerprints } from '$lib/server/assetlinks';
import type { RequestHandler } from './$types';

export const prerender = false;

// /.well-known/assetlinks.json — Android App Links verification. The directory name uses
// SvelteKit's hex escape ([x+2e] = "."): route dirs can't start with a literal dot. With no
// fingerprints configured the endpoint 404s and share/invite links simply open in the browser.
export const GET: RequestHandler = () => {
  const fingerprints = parseFingerprints(env.ANDROID_ASSETLINKS_FINGERPRINTS);
  if (!fingerprints) {
    return new Response('assetlinks not configured', {
      status: 404,
      headers: { 'Content-Type': 'text/plain; charset=utf-8' }
    });
  }

  return new Response(assetlinksJson(fingerprints), {
    headers: {
      'Content-Type': 'application/json',
      'Cache-Control': 'public, max-age=3600'
    }
  });
};

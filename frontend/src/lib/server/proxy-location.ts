/**
 * Where a redirect from the API sends the browser. An absolute URL elsewhere (a CDN portrait) is
 * passed on as it is. A location on the API itself — relative, or naming the API's own origin,
 * which the browser cannot reach — is mapped back under the same-origin proxy (`/api/mh`).
 */
export function browserLocation(location: string, target: string): string {
  const api = new URL(target);
  const resolved = new URL(location, api);
  if (resolved.origin !== api.origin) return resolved.href;
  return `/api/mh${resolved.pathname}${resolved.search}${resolved.hash}`;
}

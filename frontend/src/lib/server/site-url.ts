import { DEFAULT_SITE_URL } from '$lib/content/site-markdown';

/**
 * The site's public origin, for absolute URLs in Markdown bodies and the sitemap.
 *
 * Read straight from `process.env` rather than `$env/dynamic/public` so the modules that use it stay
 * importable in the plain-node unit test environment; on the server the two resolve to the same
 * value. Callers are server-only — never import this from a component.
 */
export function resolveSiteUrl(env: NodeJS.ProcessEnv = process.env): string {
  const configured = env.PUBLIC_SITE_URL?.trim();
  return (configured && configured.length > 0 ? configured : DEFAULT_SITE_URL).replace(/\/$/, '');
}

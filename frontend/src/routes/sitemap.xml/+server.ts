import { renderSitemap, sitemapEntries } from '$lib/content/sitemap';
import { resolveSiteUrl } from '$lib/server/site-url';
import type { RequestHandler } from './$types';

export const prerender = false;

export const GET: RequestHandler = async () => {
  const body = renderSitemap(resolveSiteUrl(), sitemapEntries());

  return new Response(body, {
    headers: {
      'Content-Type': 'application/xml; charset=utf-8',
      'Cache-Control': 'public, max-age=3600'
    }
  });
};

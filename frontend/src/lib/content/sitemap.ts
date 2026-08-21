import { prosePages } from './prose-pages';

/**
 * Last meaningful content change to the landing page and `/login`. Bump it when their copy changes;
 * a build timestamp would churn `lastmod` on every deploy and teach crawlers to ignore the field.
 */
export const HOME_LASTMOD = '2026-08-21';

export interface SitemapEntry {
  path: string;
  /** ISO `YYYY-MM-DD`. */
  lastmod: string;
  changefreq: 'daily' | 'weekly' | 'monthly' | 'yearly';
  priority: string;
}

/**
 * Public, indexable URLs. The application itself (`/library`, `/inbox`, …) is behind auth and
 * redirects anonymous visitors to `/login`, so listing it would only feed crawlers a redirect.
 *
 * `lastmod` for the prose pages comes from the page data, so editing a page's copy and forgetting to
 * touch the sitemap is not possible.
 */
export function sitemapEntries(homeLastmod: string = HOME_LASTMOD): SitemapEntry[] {
  return [
    { path: '/', lastmod: homeLastmod, changefreq: 'weekly', priority: '1.0' },
    ...prosePages.map((page): SitemapEntry => ({
      path: page.path,
      lastmod: page.updated,
      changefreq: 'monthly',
      priority: '0.6'
    })),
    { path: '/login', lastmod: homeLastmod, changefreq: 'yearly', priority: '0.3' }
  ];
}

/** Serializes entries to the sitemaps.org 0.9 schema. */
export function renderSitemap(siteUrl: string, entries: SitemapEntry[]): string {
  const base = siteUrl.replace(/\/$/, '');
  const urls = entries
    .map((entry) =>
      [
        '  <url>',
        `    <loc>${escapeXml(`${base}${entry.path}`)}</loc>`,
        `    <lastmod>${entry.lastmod}</lastmod>`,
        `    <changefreq>${entry.changefreq}</changefreq>`,
        `    <priority>${entry.priority}</priority>`,
        '  </url>'
      ].join('\n')
    )
    .join('\n');

  return `<?xml version="1.0" encoding="UTF-8"?>
<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
${urls}
</urlset>
`;
}

function escapeXml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
    .replace(/'/g, '&apos;');
}

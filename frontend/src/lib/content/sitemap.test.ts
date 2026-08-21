import { describe, expect, it } from 'vitest';
import { HOME_LASTMOD, renderSitemap, sitemapEntries } from './sitemap';
import { prosePages } from './prose-pages';

const SITE = 'https://musichoarder.app';

describe('sitemapEntries', () => {
  it('lists the public pages, home first', () => {
    expect(sitemapEntries().map((entry) => entry.path)).toEqual([
      '/',
      '/about',
      '/contact',
      '/privacy',
      '/login'
    ]);
  });

  it('takes each prose page lastmod from the page itself, so the two cannot drift', () => {
    const entries = sitemapEntries();
    for (const page of prosePages) {
      expect(entries.find((entry) => entry.path === page.path)?.lastmod).toBe(page.updated);
    }
    expect(entries[0].lastmod).toBe(HOME_LASTMOD);
  });

  it('omits the authenticated application routes', () => {
    const paths = sitemapEntries().map((entry) => entry.path);
    expect(paths).not.toContain('/library');
    expect(paths).not.toContain('/inbox');
  });
});

describe('renderSitemap', () => {
  const xml = renderSitemap(SITE, sitemapEntries());

  it('emits a sitemaps.org 0.9 urlset', () => {
    expect(xml.startsWith('<?xml version="1.0" encoding="UTF-8"?>\n')).toBe(true);
    expect(xml).toContain('<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">');
    expect(xml.trimEnd().endsWith('</urlset>')).toBe(true);
  });

  it('writes absolute locs with ISO lastmod dates', () => {
    expect(xml).toContain(`<loc>${SITE}/</loc>`);
    expect(xml).toContain(`<loc>${SITE}/privacy</loc>`);
    for (const lastmod of xml.matchAll(/<lastmod>([^<]+)<\/lastmod>/g)) {
      expect(lastmod[1]).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    }
  });

  it('opens and closes one <url> per entry', () => {
    const count = sitemapEntries().length;
    expect(xml.match(/<url>/g)).toHaveLength(count);
    expect(xml.match(/<\/url>/g)).toHaveLength(count);
  });

  it('normalizes a trailing slash on the configured site URL', () => {
    expect(renderSitemap(`${SITE}/`, sitemapEntries())).toContain(`<loc>${SITE}/about</loc>`);
  });

  it('escapes XML-significant characters in a loc', () => {
    const escaped = renderSitemap(SITE, [
      { path: '/x?a=1&b=2', lastmod: '2026-08-21', changefreq: 'monthly', priority: '0.5' }
    ]);
    expect(escaped).toContain('<loc>https://musichoarder.app/x?a=1&amp;b=2</loc>');
  });
});

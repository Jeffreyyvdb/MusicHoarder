import { describe, expect, it } from 'vitest';
import { absoluteUrl, renderProseMarkdown } from './render-markdown';
import { prosePages } from './prose-pages';
import type { ProsePage } from './types';

const SITE = 'https://musichoarder.app';

const sample: ProsePage = {
  path: '/sample',
  title: 'Sample',
  description: 'A short summary.',
  updated: '2026-08-21',
  sections: [
    {
      heading: 'First',
      blocks: [
        { kind: 'paragraph', text: 'A paragraph.' },
        { kind: 'list', items: ['one', 'two'] },
        {
          kind: 'links',
          items: [
            { label: 'Internal', href: '/contact', note: 'relative' },
            { label: 'External', href: 'https://example.com' }
          ]
        }
      ]
    }
  ]
};

describe('renderProseMarkdown', () => {
  it('serializes headings, prose, lists, and links', () => {
    expect(renderProseMarkdown(sample, SITE)).toBe(
      [
        '# Sample',
        '',
        '> A short summary.',
        '',
        '## First',
        '',
        'A paragraph.',
        '',
        '- one',
        '- two',
        '',
        `- [Internal](${SITE}/contact): relative`,
        '- [External](https://example.com)',
        '',
        '_Last updated: 2026-08-21._',
        ''
      ].join('\n')
    );
  });
});

describe('absoluteUrl', () => {
  it('expands site-relative links and leaves absolute ones untouched', () => {
    expect(absoluteUrl('/about', SITE)).toBe(`${SITE}/about`);
    expect(absoluteUrl('/about', `${SITE}/`)).toBe(`${SITE}/about`);
    expect(absoluteUrl('https://github.com/x', SITE)).toBe('https://github.com/x');
  });
});

describe('trust anchor pages', () => {
  it('covers about, contact, privacy, and delete-account', () => {
    expect(prosePages.map((page) => page.path)).toEqual([
      '/about',
      '/contact',
      '/privacy',
      '/delete-account'
    ]);
  });

  it.each(prosePages.map((page) => [page.path, page] as const))(
    '%s carries enough substance to read as a real page',
    (path, page) => {
      const markdown = renderProseMarkdown(page, SITE);
      // Agents (and the readiness audits that mimic them) treat a thin trust page as no page at
      // all; 500 characters of body text is the usual floor.
      expect(markdown.length, path).toBeGreaterThan(1500);
      expect(page.sections.length, path).toBeGreaterThanOrEqual(3);
      expect(page.description.length, path).toBeGreaterThan(80);
      expect(page.updated, path).toMatch(/^\d{4}-\d{2}-\d{2}$/);
    }
  );

  it('gives the contact page a way to actually reach someone', () => {
    const contact = prosePages.find((page) => page.path === '/contact');
    const markdown = renderProseMarkdown(contact!, SITE);
    expect(markdown).toContain('https://github.com/Jeffreyyvdb/MusicHoarder/issues');
    expect(markdown).toContain('https://github.com/Jeffreyyvdb/MusicHoarder/security');
  });

  it('discloses the analytics and the session cookie on the privacy page', () => {
    const privacy = prosePages.find((page) => page.path === '/privacy');
    const markdown = renderProseMarkdown(privacy!, SITE).toLowerCase();
    expect(markdown).toContain('umami');
    expect(markdown).toContain('mh_session');
    expect(markdown).toContain('cookie');
  });

  it('gives the delete-account page what Google Play asks of it', () => {
    const page = prosePages.find((candidate) => candidate.path === '/delete-account');
    const markdown = renderProseMarkdown(page!, SITE);
    // The app name as listed, the steps, a private channel, the deadline, and what is kept.
    expect(markdown).toContain('MusicHoarder');
    expect(markdown).toContain('## How to ask');
    expect(markdown).toContain('mailto:');
    expect(markdown).toContain('within 30 days');
    expect(markdown).toContain('## What is deleted');
    expect(markdown).toContain('## What may be kept');
    expect(markdown).toContain('## Self-hosted instances');
  });

  it('links the delete-account page from privacy and contact', () => {
    for (const path of ['/privacy', '/contact']) {
      const page = prosePages.find((candidate) => candidate.path === path);
      expect(renderProseMarkdown(page!, SITE), path).toContain(`${SITE}/delete-account`);
    }
  });
});

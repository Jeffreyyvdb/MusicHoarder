import { describe, expect, it } from 'vitest';
import {
  MARKDOWN_PATHS,
  canonicalizePath,
  homeMarkdown,
  isMarkdownUrl,
  markdownFor,
  markdownSiblingPath,
  notFoundMarkdown
} from './site-markdown';
import { prosePages } from './prose-pages';
import { renderProseMarkdown } from './render-markdown';

const SITE = 'https://musichoarder.app';

describe('canonicalizePath', () => {
  it('strips trailing slashes and the .md extension', () => {
    expect(canonicalizePath('/about')).toBe('/about');
    expect(canonicalizePath('/about/')).toBe('/about');
    expect(canonicalizePath('/about.md')).toBe('/about');
    expect(canonicalizePath('/about.md/')).toBe('/about');
  });

  it('maps the root and its /index.md sibling to /', () => {
    expect(canonicalizePath('/')).toBe('/');
    expect(canonicalizePath('/index.md')).toBe('/');
  });

  it('leaves unrelated paths alone', () => {
    expect(canonicalizePath('/api/mh/songs')).toBe('/api/mh/songs');
    expect(canonicalizePath('/pricing.md')).toBe('/pricing');
  });
});

describe('markdownSiblingPath', () => {
  it('uses /index.md for the root and <path>.md elsewhere', () => {
    expect(markdownSiblingPath('/')).toBe('/index.md');
    expect(markdownSiblingPath('/about')).toBe('/about.md');
  });
});

describe('isMarkdownUrl', () => {
  it('detects the explicit .md sibling', () => {
    expect(isMarkdownUrl('/about.md')).toBe(true);
    expect(isMarkdownUrl('/about.md/')).toBe(true);
    expect(isMarkdownUrl('/about')).toBe(false);
    expect(isMarkdownUrl('/')).toBe(false);
  });
});

describe('markdownFor', () => {
  it('serves a body for every negotiable path, by canonical and .md URL alike', () => {
    for (const path of MARKDOWN_PATHS) {
      expect(markdownFor(path, SITE), path).toBeTruthy();
      expect(markdownFor(markdownSiblingPath(path), SITE), path).toBe(markdownFor(path, SITE));
    }
  });

  it('returns null for anything else, which is what keeps the 406 off the API proxy', () => {
    expect(markdownFor('/api/mh/songs', SITE)).toBeNull();
    expect(markdownFor('/library', SITE)).toBeNull();
    expect(markdownFor('/nope', SITE)).toBeNull();
    expect(markdownFor('/pricing.md', SITE)).toBeNull();
  });

  it('renders each prose page from its single source of truth', () => {
    for (const page of prosePages) {
      expect(markdownFor(page.path, SITE)).toBe(renderProseMarkdown(page, SITE));
    }
  });
});

describe('homeMarkdown', () => {
  it('leads with an H1 and a summary blockquote, per the llms.txt convention', () => {
    const body = homeMarkdown(SITE);
    expect(body.startsWith('# MusicHoarder\n')).toBe(true);
    expect(body).toContain('\n> Free, MIT-licensed');
  });

  it('points at the sitemap, the agent guide, and the trust pages', () => {
    const body = homeMarkdown(SITE);
    for (const path of ['/sitemap.xml', '/llms.txt', '/about', '/contact', '/privacy']) {
      expect(body, path).toContain(`${SITE}${path}`);
    }
  });
});

describe('notFoundMarkdown', () => {
  it('names the path that missed and links the recovery routes', () => {
    const body = notFoundMarkdown(SITE, '/does-not-exist');
    expect(body).toContain('# 404');
    expect(body).toContain('/does-not-exist');
    expect(body).toContain(`${SITE}/sitemap.xml`);
    expect(body).toContain(`${SITE}/llms.txt`);
    expect(body).toContain(`${SITE}/about`);
  });
});

import { describe, expect, it } from 'vitest';
import {
  docLinkHref,
  docPathForSlug,
  inlineText,
  resolveDocLink,
  slugForDocPath
} from './slugs.ts';

describe('slugForDocPath', () => {
  it('maps the top-level README to the empty index slug', () => {
    expect(slugForDocPath('README.md')).toBe('');
  });

  it('maps a directory README to the directory slug', () => {
    expect(slugForDocPath('adr/README.md')).toBe('adr');
  });

  it('lowercases and folds underscores to dashes', () => {
    expect(slugForDocPath('SELF_HOSTING.md')).toBe('self-hosting');
    expect(slugForDocPath('adr/ADR-001.md')).toBe('adr/adr-001');
  });
});

describe('docPathForSlug', () => {
  const known = ['README.md', 'adr/README.md', 'SELF_HOSTING.md', 'adr/ADR-001.md', 'backend.md'];

  it('inverts slugForDocPath over the known set', () => {
    for (const path of known) {
      expect(docPathForSlug(slugForDocPath(path), known)).toBe(path);
    }
  });

  it('returns null for an unknown slug', () => {
    expect(docPathForSlug('nope', known)).toBeNull();
  });
});

describe('resolveDocLink', () => {
  it('rewrites a sibling doc link to its /docs route', () => {
    expect(resolveDocLink('architecture.md', 'backend.md')).toEqual({
      kind: 'doc',
      slug: 'backend',
      anchor: ''
    });
  });

  it('keeps anchors when rewriting', () => {
    expect(resolveDocLink('README.md', 'architecture.md#system-context')).toEqual({
      kind: 'doc',
      slug: 'architecture',
      anchor: 'system-context'
    });
  });

  it('resolves directory-relative and parent links from nested docs', () => {
    expect(resolveDocLink('adr/ADR-001.md', '../architecture.md')).toEqual({
      kind: 'doc',
      slug: 'architecture',
      anchor: ''
    });
    expect(resolveDocLink('adr/README.md', 'ADR-001.md')).toEqual({
      kind: 'doc',
      slug: 'adr/adr-001',
      anchor: ''
    });
  });

  it('sends links that leave docs/ to GitHub', () => {
    const resolved = resolveDocLink('architecture.md', '../CONTRIBUTING.md');
    expect(resolved).toEqual({
      kind: 'external',
      href: 'https://github.com/Jeffreyyvdb/MusicHoarder/blob/main/CONTRIBUTING.md'
    });
  });

  it('passes through external links and pure anchors', () => {
    expect(resolveDocLink('backend.md', 'https://example.com')).toEqual({
      kind: 'external',
      href: 'https://example.com'
    });
    expect(resolveDocLink('backend.md', '#section')).toEqual({
      kind: 'external',
      href: '#section'
    });
  });
});

describe('docLinkHref', () => {
  it('renders the index slug as /docs and others as /docs/<slug>', () => {
    expect(docLinkHref({ kind: 'doc', slug: '', anchor: '' })).toBe('/docs');
    expect(docLinkHref({ kind: 'doc', slug: 'adr/adr-001', anchor: 'context' })).toBe(
      '/docs/adr/adr-001#context'
    );
  });
});

describe('inlineText', () => {
  it('strips inline markdown markers', () => {
    expect(inlineText('**Use** `marked` for [docs](https://example.com)')).toBe(
      'Use marked for docs(https://example.com)'
    );
  });
});

describe('inlineText sanitization', () => {
  it('cannot carry markup — no angle brackets, ampersands or double quotes survive', () => {
    expect(inlineText('Hello <script>alert(1)</script>')).toBe('Hello scriptalert(1)/script');
    expect(inlineText('a <script b')).toBe('a script b');
    expect(inlineText('Tom & Jerry <3')).toBe('Tom  Jerry 3');
    expect(inlineText('He said "hi" <b>bold</b>')).toBe('He said hi bbold/b');
  });

  it('keeps readable text, including non-ASCII letters and code-span underscores', () => {
    expect(inlineText('**Use** `marked` for [docs](https://example.com)')).toBe(
      'Use marked for docs(https://example.com)'
    );
    expect(inlineText('`FOO_BAR`')).toBe('FOO_BAR');
    expect(inlineText('Café del Mar — café')).toBe('Café del Mar  café');
  });
});

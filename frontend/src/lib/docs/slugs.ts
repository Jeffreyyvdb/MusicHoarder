/**
 * Slug and link mapping for the documentation site.
 *
 * The docs live as Markdown in the repository's `docs/` directory and are rendered at
 * `/docs/<slug>`. A doc's slug is its path relative to `docs/`, lowercased, without the `.md`
 * extension and with underscores folded to dashes — except the two README files, which are the
 * index of their directory (`docs/README.md` → `/docs`, `docs/adr/README.md` → `/docs/adr`).
 *
 * Links inside the Markdown are relative to the file that contains them, exactly as they work on
 * GitHub. Rendering rewrites them so they keep working here: a link that stays inside `docs/`
 * becomes a `/docs/…` route, and one that points outside the directory (the root README,
 * CONTRIBUTING, the license) becomes a GitHub source link, since those pages are not part of the
 * docs site.
 */

/** The GitHub repo the docs ship with — the fallback target for links outside `docs/`. */
export const DOCS_REPO_BLOB_URL = 'https://github.com/Jeffreyyvdb/MusicHoarder/blob/main';

/** A Markdown file under `docs/`, relative to `docs/`, e.g. `adr/README.md`. */
export type DocPath = string;

/** The URL slug of a doc, e.g. `adr/adr-001` — the `/docs/<slug>` route without the prefix. */
export type DocSlug = string;

/** Turns `adr/ADR-001.md` into `adr/adr-001`, and `README.md` into the directory index slug. */
export function slugForDocPath(docPath: DocPath): DocSlug {
  const withoutExtension = docPath.replace(/\.md$/i, '');
  const parts = withoutExtension.split('/');
  const last = parts[parts.length - 1];
  if (last.toLowerCase() === 'readme') {
    parts.pop();
  }
  return parts.map((part) => part.toLowerCase().replaceAll('_', '-')).join('/');
}

/** Inverse of {@link slugForDocPath} over the known set of doc files. */
export function docPathForSlug(slug: DocSlug, knownPaths: readonly DocPath[]): DocPath | null {
  const bySlug = new Map(knownPaths.map((path) => [slugForDocPath(path), path]));
  return bySlug.get(slug) ?? null;
}

const EXTERNAL_HREF = /^(https?:|mailto:|#)/;

/** Joins and normalizes a relative href against the directory of the file containing it. */
function resolveRelative(sourceDocPath: DocPath, href: string): string {
  // Paths resolve inside `docs/`, so the base always carries that root prefix.
  const baseParts = ['docs', ...sourceDocPath.split('/').slice(0, -1)];
  const segments = href.split('/');
  const stack = [...baseParts];
  for (const segment of segments) {
    if (segment === '' || segment === '.') continue;
    if (segment === '..') {
      stack.pop();
    } else {
      stack.push(segment);
    }
  }
  return stack.join('/');
}

export type ResolvedLink =
  | { kind: 'doc'; slug: DocSlug; anchor: string }
  | { kind: 'external'; href: string };

/**
 * Resolves a link found in the Markdown file `sourceDocPath` (relative to `docs/`).
 * Links within `docs/` become `/docs/<slug>` routes; links that leave the directory become
 * GitHub blob links. The returned href is always safe to put in an `href` attribute as-is.
 */
export function resolveDocLink(sourceDocPath: DocPath, href: string): ResolvedLink {
  if (EXTERNAL_HREF.test(href)) return { kind: 'external', href };
  const [path, anchor = ''] = href.split('#');
  if (path === '') return { kind: 'external', href }; // a pure in-page anchor
  const resolved = resolveRelative(sourceDocPath, path);
  if (resolved.startsWith('docs/')) {
    return {
      kind: 'doc',
      slug: slugForDocPath(resolved.slice('docs/'.length)),
      anchor
    };
  }
  const anchorSuffix = anchor ? `#${anchor}` : '';
  return {
    kind: 'external',
    href: `${DOCS_REPO_BLOB_URL}/${resolved}${anchorSuffix}`
  };
}

/** Reassembles a resolved doc link into an `href` for the `/docs` site. */
export function docLinkHref(link: Extract<ResolvedLink, { kind: 'doc' }>): string {
  const base = `/docs${link.slug === '' ? '' : `/${link.slug}`}`;
  return link.anchor ? `${base}#${link.anchor}` : base;
}

export interface TocEntry {
  id: string;
  text: string;
  depth: 2 | 3;
}

/**
 * Pulls the visible text out of inline Markdown, for heading ids and TOC labels.
 *
 * The output ends up inside HTML attributes and rendered text, so it must never carry markup.
 * Rather than stripping tags or dangerous characters selectively (a bare `<script` with no
 * closing `>` survives a tag strip — the pattern CodeQL flags as incomplete multi-character
 * sanitization), keep only an allowlist of word characters, whitespace and harmless punctuation.
 */
export function inlineText(markdown: string): string {
  // Underscore stays: code spans in headings (`FOO_BAR`) must slug to GitHub's `foo_bar`, and
  // GithubSlugger preserves underscores the way GitHub does.
  return markdown.replace(/[^\p{L}\p{N}\s.,:;!?'’()/+#@%=_-]/gu, '').trim();
}

/** Escapes a string for safe inclusion in a double-quoted HTML attribute. */
export function escapeHtmlAttribute(value: string): string {
  return value
    .replaceAll('&', '&amp;')
    .replaceAll('"', '&quot;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;');
}

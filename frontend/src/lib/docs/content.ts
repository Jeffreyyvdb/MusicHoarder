import { Marked } from 'marked';
import GithubSlugger from 'github-slugger';
import {
  docLinkHref,
  escapeHtmlAttribute,
  inlineText,
  resolveDocLink,
  slugForDocPath,
  type DocPath,
  type DocSlug,
  type TocEntry
} from './slugs.ts';

/**
 * The documentation source of truth is the repository's `docs/` directory — the same Markdown
 * files a reader sees on GitHub. Vite inlines them as raw strings at build time, so the rendered
 * pages can be prerendered with no filesystem or network access at request time.
 */
const rawDocs = import.meta.glob('../../../../docs/**/*.md', {
  query: '?raw',
  import: 'default',
  eager: true
}) as Record<string, string>;

function docPathOf(globKey: string): DocPath | null {
  const marker = '/docs/';
  const start = globKey.indexOf(marker);
  if (start === -1) return null;
  return globKey.slice(start + marker.length);
}

const sources = new Map<DocPath, string>();
for (const [key, source] of Object.entries(rawDocs)) {
  const docPath = docPathOf(key);
  if (docPath) sources.set(docPath, source);
}

export const docPaths: readonly DocPath[] = [...sources.keys()].sort();

export interface DocPage {
  slug: DocSlug;
  docPath: DocPath;
  title: string;
  /** The first prose paragraph — the meta description and sidebar subtitle. */
  description: string;
  source: string;
}

/** First `# ` heading of a Markdown file, or a prettified fallback built from its path. */
export function titleOf(docPath: DocPath, source: string): string {
  const heading = /^#\s+(.+)$/m.exec(source);
  if (heading) return inlineText(heading[1]);
  const file = docPath.replace(/\.md$/, '').split('/').pop() ?? docPath;
  return file.replaceAll('_', ' ').replace(/\b\w/g, (c) => c.toUpperCase());
}

/** First non-heading, non-table, non-fence paragraph of a Markdown file. */
export function descriptionOf(source: string): string {
  const lines = source.split('\n');
  let inFence = false;
  for (const line of lines) {
    if (line.trimStart().startsWith('```')) {
      inFence = !inFence;
      continue;
    }
    if (inFence) continue;
    const text = line.trim();
    if (text === '' || text.startsWith('#') || text.startsWith('|') || text.startsWith('```')) {
      continue;
    }
    return inlineText(text).slice(0, 180);
  }
  return '';
}

export function getDocPage(slug: DocSlug): DocPage | null {
  const docPath = [...sources.keys()].find((path) => slugForDocPath(path) === slug);
  if (!docPath) return null;
  const source = sources.get(docPath) as string;
  return {
    slug,
    docPath,
    title: titleOf(docPath, source),
    description: descriptionOf(source),
    source
  };
}

export interface RenderedDoc {
  html: string;
  toc: TocEntry[];
}

/** Sidebar grouping, mirroring docs/README.md's "Understand / Run / Decide" sections. */
const SIDEBAR_GROUPS: ReadonlyArray<{ label: string; slugs: readonly DocSlug[] }> = [
  { label: 'Start here', slugs: [''] },
  {
    label: 'Understand the system',
    slugs: ['architecture', 'backend', 'data-model', 'integrations', 'frontend']
  },
  { label: 'Run and operate it', slugs: ['runbook', 'deployment', 'self-hosting'] },
  {
    label: 'Decide',
    slugs: [
      'adr',
      'adr/adr-001',
      'adr/adr-002',
      'adr/adr-003',
      'adr/adr-004',
      'adr/adr-005',
      'adr/adr-006'
    ]
  }
];

/** Short sidebar label: ADR pages drop their "ADR-00N: " prefix. */
export function sidebarLabel(page: DocPage): string {
  const match = /^ADR-\d+:\s*(.+)$/.exec(page.title);
  return match ? match[1] : page.title;
}

export interface SidebarGroup {
  label: string;
  pages: Array<{ slug: DocSlug; label: string }>;
}

/** The sidebar, ordered by {@link SIDEBAR_GROUPS}; any doc not listed is appended ungrouped. */
export function sidebar(): SidebarGroup[] {
  const pages = new Map<DocSlug, string>();
  for (const path of docPaths) {
    const page = getDocPage(slugForDocPath(path));
    if (page) pages.set(slugForDocPath(path), sidebarLabel(page));
  }
  const groups: SidebarGroup[] = [];
  const grouped = new Set<DocSlug>();
  for (const group of SIDEBAR_GROUPS) {
    const entries = group.slugs
      .filter((slug) => pages.has(slug))
      .map((slug) => ({ slug, label: pages.get(slug) as string }));
    if (entries.length === 0) continue;
    entries.forEach((entry) => grouped.add(entry.slug));
    groups.push({ label: group.label, pages: entries });
  }
  const rest = [...pages.keys()].filter((slug) => !grouped.has(slug));
  if (rest.length > 0) {
    groups.push({
      label: 'More',
      pages: rest.map((slug) => ({ slug, label: pages.get(slug) as string }))
    });
  }
  return groups;
}

/** Prev/next pages in sidebar order, for the footer pager. */
export function neighbors(slug: DocSlug): { prev: DocPage | null; next: DocPage | null } {
  const order = sidebar().flatMap((group) => group.pages.map((page) => page.slug));
  const index = order.indexOf(slug);
  const at = (offset: number) => {
    const neighborSlug = order[index + offset];
    return neighborSlug === undefined ? null : getDocPage(neighborSlug);
  };
  return { prev: at(-1), next: at(1) };
}

/**
 * Renders a documentation Markdown file to HTML with GitHub-style heading ids, rewritten links,
 * and the heading tree for the "On this page" table of contents.
 */
export function renderDoc(source: string, docPath: DocPath): RenderedDoc {
  const slugger = new GithubSlugger();
  const toc: TocEntry[] = [];
  const marked = new Marked({ gfm: true, breaks: false });

  marked.use({
    renderer: {
      heading({ tokens, depth, text }) {
        const html = this.parser.parseInline(tokens);
        // The id comes from the plain text (not the parsed HTML, which may carry <code> tags), so
        // anchors authored the GitHub way (`#some-heading`) resolve to the same id both here and
        // on github.com.
        const plain = inlineText(text);
        const id = slugger.slug(plain);
        if (depth === 2 || depth === 3) toc.push({ id, text: plain, depth });
        return `<h${depth} id="${escapeHtmlAttribute(id)}">${html}</h${depth}>`;
      },
      link({ href, title, tokens }) {
        const text = this.parser.parseInline(tokens);
        const resolved = resolveDocLink(docPath, href);
        const target = resolved.kind === 'doc' ? docLinkHref(resolved) : resolved.href;
        const external = resolved.kind === 'external' && /^https?:/.test(target);
        const attributes = [
          `href="${escapeHtmlAttribute(target)}"`,
          title ? `title="${escapeHtmlAttribute(title)}"` : '',
          external ? 'target="_blank" rel="noopener noreferrer"' : ''
        ]
          .filter(Boolean)
          .join(' ');
        return `<a ${attributes}>${text}</a>`;
      }
    }
  });

  const html = marked.parse(source) as string;
  return { html, toc };
}

/** True when the doc contains mermaid fences, so the page knows to hydrate diagrams. */
export function hasMermaid(source: string): boolean {
  return source.includes('```mermaid');
}

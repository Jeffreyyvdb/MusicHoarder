import type { ProseBlock, ProsePage, ProseSection } from './types';

/**
 * Serializes a {@link ProsePage} to CommonMark. This is the Markdown representation served from the
 * page's canonical URL when a client sends `Accept: text/markdown` (and from the `.md` sibling),
 * so it must carry the same content the HTML page shows — headings, prose, lists and links.
 */
export function renderProseMarkdown(page: ProsePage, siteUrl: string): string {
  const parts: string[] = [
    `# ${page.title}`,
    `> ${page.description}`,
    ...page.sections.map((section) => renderSection(section, siteUrl)),
    `_Last updated: ${page.updated}._`
  ];
  return `${parts.join('\n\n')}\n`;
}

function renderSection(section: ProseSection, siteUrl: string): string {
  return [
    `## ${section.heading}`,
    ...section.blocks.map((block) => renderBlock(block, siteUrl))
  ].join('\n\n');
}

function renderBlock(block: ProseBlock, siteUrl: string): string {
  switch (block.kind) {
    case 'paragraph':
      return block.text;
    case 'list':
      return block.items.map((item) => `- ${item}`).join('\n');
    case 'links':
      return block.items
        .map((link) => {
          const href = absoluteUrl(link.href, siteUrl);
          return `- [${link.label}](${href})${link.note ? `: ${link.note}` : ''}`;
        })
        .join('\n');
  }
}

/**
 * Markdown is read out of context — an agent may hand the body to another tool with no memory of
 * which host it came from — so relative links are expanded to absolute ones.
 */
export function absoluteUrl(href: string, siteUrl: string): string {
  if (!href.startsWith('/')) return href;
  return `${siteUrl.replace(/\/$/, '')}${href}`;
}

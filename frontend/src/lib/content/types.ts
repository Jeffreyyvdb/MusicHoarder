/**
 * The public prose pages (About / Contact / Privacy) are authored once as data and rendered twice:
 * as a styled Svelte page for people, and as Markdown for agents that ask for `text/markdown`.
 *
 * Keeping one source of truth is the point — a hand-maintained Markdown twin of each page would
 * drift the moment either copy changed, and the whole reason an agent asks for Markdown is that it
 * expects the same content the browser gets.
 */
export type ProseBlock =
  | { kind: 'paragraph'; text: string }
  | { kind: 'list'; items: string[] }
  | { kind: 'links'; items: ProseLink[] };

export interface ProseLink {
  label: string;
  href: string;
  /** Short trailing explanation, rendered after an em dash in both HTML and Markdown. */
  note?: string;
}

export interface ProseSection {
  heading: string;
  blocks: ProseBlock[];
}

export interface ProsePage {
  /** Canonical path, no trailing slash. Doubles as the Markdown-negotiation key. */
  path: string;
  /** Page `<title>` is derived from this; it is also the Markdown H1. */
  title: string;
  /** Meta description, and the Markdown blockquote under the H1. */
  description: string;
  /** ISO `YYYY-MM-DD`. Shown on the page and used as the sitemap's `lastmod`. */
  updated: string;
  sections: ProseSection[];
}

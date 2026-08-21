import { prosePages } from './prose-pages';
import { renderProseMarkdown } from './render-markdown';

export const DEFAULT_SITE_URL = 'https://musichoarder.app';

const REPO_URL = 'https://github.com/Jeffreyyvdb/MusicHoarder';

/** Canonical paths that have a Markdown representation, in sitemap order. */
export const MARKDOWN_PATHS = ['/', ...prosePages.map((page) => page.path)] as const;

function abs(siteUrl: string, path: string): string {
  return `${siteUrl.replace(/\/$/, '')}${path}`;
}

/**
 * Markdown twin of the landing page. The landing page is a designed, component-built surface with
 * no prose source to serialize, so its Markdown representation is written here — deliberately
 * short, because an agent asking for Markdown wants the facts, not the marketing rhythm.
 */
export function homeMarkdown(siteUrl: string): string {
  return `# MusicHoarder

> Free, MIT-licensed, self-hosted pipeline that fingerprints, identifies, enriches and reorganizes a messy music library into clean, correctly tagged files on your own disk.

Point MusicHoarder at a folder of badly named audio files and a destination folder. It fingerprints
every track with Chromaprint/AcoustID, reaches a consensus across several metadata providers, grades
each match with an LLM, deduplicates by fingerprint, and writes a tidy copy to the destination as
plain files (\`Artist / Year - Album / NN - Track\`). The source folder is mounted read-only and is
never modified. Uncertain matches go to a human review Inbox instead of being guessed into your
library.

## Pipeline

- **Scan** — index the source directory, and rescan on a timer so newly copied files are ingested.
- **Fingerprint** — Chromaprint acoustic fingerprint plus exact duration for every track.
- **Enrich** — AcoustID, MusicBrainz, Spotify, Deezer, Apple Music and community trackers in parallel, reconciled into one consensus answer.
- **Grade** — an LLM scores each proposed match so a confident-looking wrong answer still gets flagged.
- **Dedupe** — duplicates found by fingerprint; the highest-quality copy wins.
- **Build** — clean tagged copies written to the destination, with cover art and synced lyrics.

## Facts

- Price: free. MIT licensed. No hosted tier, no paid plan, no account required.
- Deployment: Docker Compose (prebuilt images on GHCR) or build from source; PostgreSQL for state.
- Stack: ASP.NET Core minimal API, SvelteKit 5 frontend, .NET Aspire orchestration, Chromaprint/fpcalc.
- Clients: web UI, plus a native Android client that pairs by QR code.
- Telemetry: none. Installed instances send nothing back to the maintainer.

## Links

- [Site map](${abs(siteUrl, '/sitemap.xml')}): every public URL.
- [Agent guide](${abs(siteUrl, '/llms.txt')}): when to use MusicHoarder and how to call it.
- [About](${abs(siteUrl, '/about')}): what the project is and who maintains it.
- [Contact](${abs(siteUrl, '/contact')}): issues, security reports, contributions.
- [Privacy](${abs(siteUrl, '/privacy')}): what this site and a self-hosted instance process.
- [Pricing](${abs(siteUrl, '/pricing.md')}): machine-readable pricing (it is free).
- [Source code](${REPO_URL}): issues, releases, self-hosting quickstart.
`;
}

/**
 * Body served for a 404. Agents that wander onto a dead URL get a real 404 status *and* a short map
 * of where to look instead, which is the difference between a recoverable miss and a dead end.
 */
export function notFoundMarkdown(siteUrl: string, pathname: string): string {
  return `# 404 — page not found

> \`${pathname}\` does not exist on ${siteUrl.replace(/^https?:\/\//, '')}. This is a real 404: every
> path that is not listed below returns this status, so you can trust it.

## Where to look instead

- [Home](${abs(siteUrl, '/')}): what MusicHoarder is and how the pipeline works.
- [Site map](${abs(siteUrl, '/sitemap.xml')}): every public URL, with last-modified dates.
- [Agent guide](${abs(siteUrl, '/llms.txt')}): when to use MusicHoarder, and how an agent should call it.
- [About](${abs(siteUrl, '/about')}) · [Contact](${abs(siteUrl, '/contact')}) · [Privacy](${abs(siteUrl, '/privacy')}).
- [Pricing](${abs(siteUrl, '/pricing.md')}): machine-readable pricing.
- [Source code and documentation](${REPO_URL}#readme): the README is the self-hosting manual.

Application pages (\`/library\`, \`/inbox\`, \`/wishlist\`, …) exist but require a signed-in session;
they redirect to [/login](${abs(siteUrl, '/login')}) when you are anonymous, and they are not part of
the public documentation surface.

Every canonical page above also serves \`text/markdown\` via content negotiation: send
\`Accept: text/markdown\`, or append \`.md\` to the path.
`;
}

/**
 * Markdown body for a canonical path, or `null` when the path has no Markdown representation.
 * `null` is what keeps strict negotiation (and its `406`) off the API proxy and the app routes.
 */
export function markdownFor(pathname: string, siteUrl: string): string | null {
  const canonical = canonicalizePath(pathname);
  if (canonical === '/') return homeMarkdown(siteUrl);
  const page = prosePages.find((candidate) => candidate.path === canonical);
  return page ? renderProseMarkdown(page, siteUrl) : null;
}

/**
 * Normalizes `/about/`, `/about.md` and `/about` to the canonical `/about`. The home page's
 * Markdown sibling is `/index.md`, because `/.md` is not a URL anyone would guess or type.
 */
export function canonicalizePath(pathname: string): string {
  const withoutSlash = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
  const base = withoutSlash.endsWith('.md') ? withoutSlash.slice(0, -3) : withoutSlash;
  if (base === '' || base === '/index') return '/';
  return base;
}

/** The `.md` sibling URL advertised via `Link: rel="alternate"` for a canonical path. */
export function markdownSiblingPath(canonical: string): string {
  return canonical === '/' ? '/index.md' : `${canonical}.md`;
}

/** True for the explicit `.md` sibling URL (`/about.md`), which serves Markdown regardless of Accept. */
export function isMarkdownUrl(pathname: string): boolean {
  const withoutSlash = pathname.length > 1 ? pathname.replace(/\/+$/, '') : pathname;
  return withoutSlash.endsWith('.md');
}

import { isAdmin, isDemo } from '$lib/auth/capabilities';
import type { SessionUser } from '$lib/auth/session-types';

/**
 * Settings' sections and which one a URL shows.
 *
 * The section lives in the URL (`?tab=`), never in component state: on a phone a section is a
 * page pushed on top of the Settings root, so Back has to be able to take it away again, and
 * `?tab=people|sources|account|updates` are live deep links from elsewhere in the app (the Share
 * dialog, the storage sheet, the account panel, the update banner).
 */
export type SectionId = 'sources' | 'account' | 'providers' | 'output' | 'people' | 'updates';

export type Section = { id: SectionId; label: string };

// The order a bare /settings falls back through on a desktop (Sources first, as it always has:
// the Spotify page and the account panel's admin link open /settings to reach the source paths
// and credentials). The lists themselves are laid out by the view, not by this order.
export const SECTIONS: readonly Section[] = [
  { id: 'sources', label: 'Sources' },
  { id: 'account', label: 'Account' },
  { id: 'providers', label: 'Providers' },
  { id: 'output', label: 'Library output' },
  { id: 'people', label: 'People' },
  { id: 'updates', label: 'Updates' }
];

// Sections that were retired but may still be bookmarked, and where their content went. Filename
// rules was a page holding one "coming soon" row (a dead end on a phone); that note now sits in
// the Providers footer, so an old link lands there.
const RETIRED: ReadonlyMap<string, SectionId> = new Map([['rules', 'providers']]);

type Audience = Pick<SessionUser, 'role' | 'isAdmin'> | null | undefined;

/**
 * An admin sees every section. The demo sees all but People (inviting and sharing are
 * administration; its view of the rest is read-only). A member gets Settings for their own
 * account only — sign-out, passkeys, phone pairing — since every other section is instance
 * configuration.
 */
export function visibleSections(user: Audience): Section[] {
  if (isAdmin(user)) return [...SECTIONS];
  if (isDemo(user)) return SECTIONS.filter((s) => s.id !== 'people');
  return SECTIONS.filter((s) => s.id === 'account');
}

/**
 * The section a URL asks for: `?tab=`, else a `#hash` naming one (older links, the update
 * banner's `/settings#updates` among them, used a hash). Null when it names nothing.
 */
export function requestedSection(url: URL): string | null {
  const tab = url.searchParams.get('tab');
  if (tab) return tab;
  const hash = url.hash.replace(/^#/, '');
  return SECTIONS.some((s) => s.id === hash) ? hash : null;
}

export type SettingsView = {
  /** The section to show; null shows the root list (stacked admin/demo only). */
  section: SectionId | null;
  /**
   * The person a People URL drills into (`?tab=people&person=<id>`): a detail page pushed over
   * the People list at every width. Null everywhere else.
   */
  person: string | null;
  /** A URL to replace the current one with (an invalid or disallowed section), else null. */
  redirect: string | null;
};

/**
 * What /settings shows, and whether the URL needs correcting.
 *
 * - One visible section (a member): it shows directly at every width, with no root and no URL
 *   write; a URL naming anything else is folded back to plain /settings.
 * - A valid section: shown (with `person` when a People URL names one).
 * - A retired section: the section its content moved to, with the URL corrected to match.
 * - No section: the root list when stacked; beside the pane list, the first section (no URL
 *   write — the pane list highlights it).
 * - An invalid or disallowed section: folded to the root when stacked, to the first section
 *   beside the pane list.
 *
 * `stacked` is a phone, or a window too narrow for two panes (SettingsV2 measures it): the root
 * list with each section pushed over it.
 */
export function resolveSettingsView(url: URL, user: Audience, stacked: boolean): SettingsView {
  const visible = visibleSections(user);
  const asked = requestedSection(url);
  const retired = asked !== null ? RETIRED.get(asked) : undefined;
  const requested = retired ?? asked;
  const valid = visible.find((s) => s.id === requested);
  const view = (section: SectionId | null, redirect: string | null): SettingsView => ({
    section,
    person: section === 'people' && !redirect ? url.searchParams.get('person') || null : null,
    redirect
  });

  if (visible.length === 1) {
    const only = visible[0].id;
    return view(only, requested !== null && requested !== only ? '/settings' : null);
  }
  // A retired section shows where its content went, and the URL is corrected to say so.
  if (valid) return view(valid.id, retired ? sectionHref(valid.id) : null);

  const first = visible[0]?.id ?? null;
  if (requested === null) return view(stacked ? null : first, null);
  return stacked ? view(null, '/settings') : view(first, first ? sectionHref(first) : '/settings');
}

/** The URL of one person's page under People. */
export function personHref(id: string): string {
  return `/settings?tab=people&person=${encodeURIComponent(id)}`;
}

/** The URL of a section (what a row links to and what a desktop selection writes). */
export function sectionHref(id: SectionId): string {
  return `/settings?tab=${id}`;
}

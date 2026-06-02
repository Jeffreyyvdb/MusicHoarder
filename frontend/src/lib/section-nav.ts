import type { Component } from 'svelte';
import { Disc, Disc3, FolderTree, Gauge, TrendingUp } from '@lucide/svelte';
import { LIBRARY_SUBNAV } from '$lib/library-subnav';

// A single source of truth for the section-level tab bars (the secondary nav
// that sits pinned above page content). Rendered once by SectionSubNav in the
// app shell, never per-page, so switching tabs within a section produces zero
// layout shift. Counts are intentionally omitted here — the sidebar carries the
// live counts; this bar stays count-less so its dimensions never change.

export type SectionTab = {
  id: string;
  label: string;
  href: string;
  icon?: Component;
  /** Show a pulse dot when the pipeline is running (the live conveyor). */
  live?: boolean;
};

// Order + labels + hrefs + icons for the Pipeline sub-nav. Mirrors the Pipeline
// section's sub-items in AppSidebarV2 — the two must stay in step.
export const PIPELINE_SUBNAV: SectionTab[] = [
  { id: 'conveyor', label: 'Conveyor', href: '/pipeline', icon: Disc3, live: true },
  { id: 'folders', label: 'By folder', href: '/directories', icon: FolderTree },
  { id: 'quality', label: 'AI quality', href: '/quality', icon: Gauge },
  { id: 'album-quality', label: 'Album matches', href: '/album-quality', icon: Disc },
  { id: 'performance', label: 'Performance over time', href: '/performance', icon: TrendingUp }
];

// Maps a route path to its section tab bar + which tab is active. Returns null
// for routes that don't belong to a tabbed section (settings, inbox, track
// detail, …) so the bar renders nothing there.
type Resolved = { tabs: SectionTab[]; active: string };

const ROUTE_MAP: Record<string, Resolved> = {
  '/pipeline': { tabs: PIPELINE_SUBNAV, active: 'conveyor' },
  '/directories': { tabs: PIPELINE_SUBNAV, active: 'folders' },
  '/quality': { tabs: PIPELINE_SUBNAV, active: 'quality' },
  '/album-quality': { tabs: PIPELINE_SUBNAV, active: 'album-quality' },
  '/performance': { tabs: PIPELINE_SUBNAV, active: 'performance' },
  '/library': { tabs: [...LIBRARY_SUBNAV], active: 'albums' },
  '/artists': { tabs: [...LIBRARY_SUBNAV], active: 'artists' },
  '/tracks': { tabs: [...LIBRARY_SUBNAV], active: 'tracks' },
  '/spotify': { tabs: [...LIBRARY_SUBNAV], active: 'spotify' }
};

export function resolveSectionSubNav(pathname: string): Resolved | null {
  // Normalise a possible trailing slash so '/library/' still matches.
  const path = pathname.length > 1 && pathname.endsWith('/') ? pathname.slice(0, -1) : pathname;
  return ROUTE_MAP[path] ?? null;
}

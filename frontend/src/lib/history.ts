import type { Component } from 'svelte';
import {
  Activity,
  AudioLines,
  BadgeCheck,
  CalendarClock,
  Captions,
  CircleArrowUp,
  CircleQuestionMark,
  CircleX,
  Clapperboard,
  CloudOff,
  Copy,
  Disc3,
  Download,
  FileCheck,
  FileX,
  FolderSearch,
  Gauge,
  Heart,
  Image,
  ImageOff,
  Languages,
  ListMusic,
  ListPlus,
  Merge,
  MicVocal,
  PackagePlus,
  RefreshCw,
  Rocket,
  ScanLine,
  SlidersHorizontal,
  Split,
  Tags,
  TrendingUp,
  TriangleAlert,
  UserCheck,
  Users,
  Wrench
} from '@lucide/svelte';
import type { HistoryCategory, HistoryTint } from '$lib/api-client';

/**
 * The History feed's presentation layer, in one place.
 *
 * The feed carries two axes and they answer different questions. `category` is the subsystem a change
 * came from — it is what the chips filter on, and it is how the owner of a self-hosted pipeline
 * actually thinks about their own machine ("did the lyrics sweep do anything last night?"). `tint` is
 * severity, and it answers "did something break?" without a category filter at all: the Problems
 * toggle is just tint, so a failure anywhere is one press away.
 */
export type HistoryCategoryMeta = {
  id: HistoryCategory;
  label: string;
  icon: Component;
  /** Shown when the category is selected and has nothing in the window. */
  blurb: string;
};

export const HISTORY_CATEGORIES: HistoryCategoryMeta[] = [
  {
    id: 'acquired',
    label: 'Acquired',
    icon: Download,
    blurb: 'Downloads, wishlist additions, quality upgrades and albums filled in.'
  },
  {
    id: 'written',
    label: 'Library',
    icon: FileCheck,
    blurb: 'Tracks reaching the destination library, and the tags written onto them.'
  },
  {
    id: 'enriched',
    label: 'Identified',
    icon: BadgeCheck,
    blurb: 'Tracks matched against the metadata providers, and the ones needing a decision.'
  },
  {
    id: 'lyrics',
    label: 'Lyrics',
    icon: Captions,
    blurb: 'LRCLIB lookups, timing repairs, AI transcription and translations.'
  },
  {
    id: 'video',
    label: 'Videos',
    icon: Clapperboard,
    blurb: 'Music videos fetched for your tracks, and how each was lined up with the audio.'
  },
  {
    id: 'artwork',
    label: 'Artwork',
    icon: Image,
    blurb: 'Cover art written into album folders, and the albums nothing could be found for.'
  },
  {
    id: 'listening',
    label: 'Listening',
    icon: Heart,
    blurb: 'Likes, and playlists written out to the library.'
  },
  {
    id: 'curation',
    label: 'Curation',
    icon: Wrench,
    blurb: 'Merges, duplicates, quality grades and tracks that left the source library.'
  },
  {
    id: 'sync',
    label: 'Sync',
    icon: RefreshCw,
    blurb: 'Tracks pushed to your other MusicHoarder instance.'
  },
  {
    id: 'pipeline',
    label: 'Pipeline',
    icon: Activity,
    blurb: 'Scans, and the updates and setting changes that alter how the pipeline behaves.'
  }
];

const CATEGORY_ICON = new Map<HistoryCategory, Component>(
  HISTORY_CATEGORIES.map((c) => [c.id, c.icon])
);

/**
 * Per-kind glyphs. Deliberately not exhaustive — a kind the API adds and this map does not carry
 * falls back to its category's icon, which is always right if unspecific.
 */
const KIND_ICON: Record<string, Component> = {
  // Acquired
  downloaded: Download,
  'scanned-in': FolderSearch,
  'album-filled': PackagePlus,
  'album-completion': PackagePlus,
  'wishlist-added': ListPlus,
  'download-failed': CloudOff,
  'download-not-found': CloudOff,
  'upgrade-applied': CircleArrowUp,
  'upgrade-failed': TrendingUp,
  'upgrade-not-found': TrendingUp,
  // Library writes
  built: FileCheck,
  'build-failed': TriangleAlert,
  tags: Tags,
  consolidation: Disc3,
  'artist-rename': Users,
  'year-correction': CalendarClock,
  // Enrichment
  matched: BadgeCheck,
  'needs-review': CircleQuestionMark,
  'enrich-failed': CircleX,
  'review-approved': UserCheck,
  // Lyrics
  'lyrics-added': Captions,
  'lyrics-missing': Captions,
  'lyrics-instrumental': AudioLines,
  'lyrics-failed': Captions,
  'lyrics-timing-fixed': AudioLines,
  'lyrics-timing-suspect': AudioLines,
  'lyrics-transcribed': MicVocal,
  'lyrics-realigned': MicVocal,
  'lyrics-transcription-failed': MicVocal,
  'lyrics-translated': Languages,
  // Video
  'video-added': Clapperboard,
  'video-failed': Clapperboard,
  // Artwork
  cover: Image,
  'cover-not-found': ImageOff,
  'cover-fetch-failed': ImageOff,
  // Listening
  liked: Heart,
  'playlist-exported': ListMusic,
  // Sync
  synced: RefreshCw,
  'sync-skipped': RefreshCw,
  'sync-failed': RefreshCw,
  // Curation
  'artists-merged': Merge,
  'albums-merged': Merge,
  'credit-split': Split,
  'album-healed': Wrench,
  'duplicates-found': Copy,
  'duplicates-dismissed': Copy,
  'track-removed': FileX,
  graded: Gauge,
  'graded-poorly': Gauge,
  // Pipeline
  'scan-completed': ScanLine,
  'scan-running': ScanLine,
  'scan-cancelled': ScanLine,
  'scan-failed': TriangleAlert,
  'version-changed': Rocket,
  'settings-changed': SlidersHorizontal
};

export function historyIcon(kind: string, category: HistoryCategory): Component {
  return KIND_ICON[kind] ?? CATEGORY_ICON.get(category) ?? Activity;
}

/**
 * Icon-tile treatment per severity. Colour only for status: a routine entry gets the neutral tile
 * every grouped list uses, a success keeps its glyph in the tint, and the two problem tints use the
 * contrast-checked warning/destructive text tokens on their own light wash (the Badge's recipe).
 */
export const HISTORY_TINT_BADGE: Record<HistoryTint, string> = {
  ok: 'bg-primary/12 text-primary',
  info: 'bg-muted text-foreground',
  warn: 'bg-warning/15 text-warning-text',
  err: 'bg-destructive/12 text-destructive-text'
};

/** A coloured leading edge on a problem row, so it is findable while scrolling past forty rows. */
export const HISTORY_TINT_EDGE: Record<HistoryTint, string> = {
  ok: '',
  info: '',
  warn: 'bg-warning',
  err: 'bg-destructive'
};

export function isProblem(tint: HistoryTint): boolean {
  return tint === 'warn' || tint === 'err';
}

export type HistoryRangeKey = '1' | '7' | '30' | 'custom';

export const HISTORY_RANGES: { key: HistoryRangeKey; label: string }[] = [
  { key: '1', label: 'Today' },
  { key: '7', label: '7 days' },
  { key: '30', label: '30 days' },
  { key: 'custom', label: 'Custom' }
];

export type HistoryFilterState = {
  range: HistoryRangeKey;
  customFrom: string;
  customTo: string;
  problemsOnly: boolean;
  categories: ReadonlySet<HistoryCategory> | readonly HistoryCategory[];
};

/** The filters a fresh visit starts with: the last 7 days, everything, every severity. */
export function isDefaultHistoryFilter(f: HistoryFilterState): boolean {
  return f.range === '7' && !f.problemsOnly && [...f.categories].length === 0;
}

/** "12 Aug" for a yyyy-mm-dd date input value, in the viewer's own locale. */
function shortDate(value: string): string {
  const d = new Date(`${value}T00:00:00`);
  if (Number.isNaN(d.getTime())) return value;
  return d.toLocaleDateString([], { day: 'numeric', month: 'short' });
}

/**
 * The active filters in a few words, for the line under the page title — on a phone the filters
 * live in a sheet, so this line is how the page says what it is showing. The range always leads;
 * Problems and the chosen categories follow only when set; more than two categories collapse to
 * a count so the line stays one line.
 */
export function historyFilterSummary(f: HistoryFilterState): string {
  const parts: string[] = [];
  if (f.range === 'custom') {
    parts.push(
      f.customFrom && f.customTo
        ? `${shortDate(f.customFrom)} – ${shortDate(f.customTo)}`
        : 'Custom range'
    );
  } else {
    parts.push(f.range === '1' ? 'Today' : `Last ${f.range} days`);
  }
  if (f.problemsOnly) parts.push('Problems');
  const picked = [...f.categories];
  if (picked.length > 0) {
    const labels = picked.map((id) => HISTORY_CATEGORIES.find((c) => c.id === id)?.label ?? id);
    parts.push(labels.length <= 2 ? labels.join(', ') : `${labels.length} categories`);
  }
  return parts.join(' · ');
}

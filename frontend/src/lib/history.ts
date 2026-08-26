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

/** Icon-chip treatment per severity. Same vocabulary as TimelineList, so the two read as one system. */
export const HISTORY_TINT_BADGE: Record<HistoryTint, string> = {
  ok: 'bg-primary/12 text-primary',
  info: 'bg-[#6a89cc]/15 text-[#4a6abc] dark:text-[#9ab0e0]',
  warn: 'bg-amber-500/15 text-amber-600 dark:text-amber-400',
  err: 'bg-red-500/15 text-red-600 dark:text-red-400'
};

/** A coloured left edge on the row itself, so a problem is findable while scrolling past forty rows. */
export const HISTORY_TINT_EDGE: Record<HistoryTint, string> = {
  ok: 'border-l-transparent',
  info: 'border-l-transparent',
  warn: 'border-l-amber-500/70',
  err: 'border-l-red-500/70'
};

export function isProblem(tint: HistoryTint): boolean {
  return tint === 'warn' || tint === 'err';
}

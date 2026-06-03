/**
 * Shared demo data + copy for the marketing landing page (`/`, SSR, public).
 *
 * The landing reuses REAL app components (LibraryAlbumsGridV2, PipelineStageCard) and REAL
 * helpers (review-helpers: buildTimeline/contributedProviders/providerLabel/providerColor) fed
 * the static, type-checked demo data below — so what a Google visitor sees is the actual app
 * surface, not a throwaway mock. Everything here is fictional sample content but every fact about
 * the *system* (providers, ports, pipeline, layout) is real; see CLAUDE.md / docker-compose.yml.
 *
 * ── Design contract for every landing section (keep them visually coherent) ───────────────────
 *  • Width + gutters:   `mx-auto max-w-[1280px] px-6 md:px-14`
 *  • Section rhythm:    vertical `py-14`; anchored sections add `scroll-mt-8` and an `id`
 *  • Section header:    mono kicker `text-muted-foreground font-mono text-[11px] font-semibold
 *                       tracking-[0.12em] uppercase`; h2 `text-[32px] font-bold tracking-[-0.025em]`;
 *                       sub `text-muted-foreground text-[14px] leading-[1.6] text-pretty`
 *  • Surfaces:          cards `bg-card border-border rounded-lg border`; sunken `bg-surface-sunken`
 *  • Tokens ONLY:       bg-background / bg-card / text-foreground / text-muted-foreground /
 *                       text-primary / border-border (never raw --bg/--accent). Provider brand
 *                       colors come from `providerColor()` (literal oklch/hex, theme-independent).
 *  • Mono:              `font-mono` for paths/commands/counters
 *  • No `app.css` edits — scope any keyframe in a component-local <style>.
 */

import type { Component } from 'svelte';
import {
  AudioWaveform,
  Copy,
  FolderTree,
  Library,
  ScanLine,
  Sparkles,
  Tag
} from '@lucide/svelte';
import type {
  AlbumSummary,
  ApiSong,
  EnrichmentDetail,
  SongQualityGradeView
} from '$lib/api-client';

const GITHUB_URL = 'https://github.com/Jeffreyyvdb/MusicHoarder';
export const githubUrl = GITHUB_URL;

/* ──────────────────────────────────────────────────────────────────────────────
 * Self-host: the REAL homelab flow (root README.md + docker-compose.yml + .env.example).
 * Two GHCR images (api + frontend) + Postgres — no single container, no `:8420`.
 * ────────────────────────────────────────────────────────────────────────────── */

/** The exact homelab quickstart, copy-paste ready. Pulls prebuilt GHCR images — no checkout, no build. */
export const installCommand = `curl -fsSLO https://raw.githubusercontent.com/Jeffreyyvdb/MusicHoarder/main/docker-compose.yml
curl -fsSL https://raw.githubusercontent.com/Jeffreyyvdb/MusicHoarder/main/.env.example -o .env
# edit .env — set your two folders, a Postgres password, owner email + public URL
docker compose up -d`;

/** Abridged-but-accurate excerpt of the repo's docker-compose.yml (real images, ports, mounts). */
export const composeSnippet = `services:
  postgres:                       # metadata + cache
    image: postgres:17

  musichoarder:                   # API + the whole pipeline
    image: ghcr.io/jeffreyyvdb/musichoarder/api:latest
    ports: ["5050:8080"]
    volumes:
      - \${MUSIC_SOURCE_PATH}:/music/source:ro        # read-only
      - \${MUSIC_DESTINATION_PATH}:/music/destination # clean library
    environment:
      - MusicEnricher__SourceDirectory=/music/source
      - MusicEnricher__DestinationDirectory=/music/destination
      - MusicEnricher__AcoustIdApiKey=\${ACOUSTID_API_KEY:-}
    depends_on: [postgres]

  frontend:                       # the web UI you're looking at
    image: ghcr.io/jeffreyyvdb/musichoarder/frontend:latest
    ports: ["3000:3000"]
    depends_on: [musichoarder]`;

/** Three-step quickstart, derived from the README self-hosting section. */
export const quickstartSteps: ReadonlyArray<{ title: string; body: string }> = [
  {
    title: 'Download and configure',
    body: 'Grab docker-compose.yml and .env.example — no repo clone needed. Point MUSIC_SOURCE_PATH at your messy folder and MUSIC_DESTINATION_PATH at an empty one for the clean library, then set a POSTGRES_PASSWORD, OWNER_EMAIL, and PUBLIC_BASE_URL.'
  },
  {
    title: 'Bring it up',
    body: 'Run docker compose up -d. It pulls the prebuilt API + frontend images from GHCR, starts PostgreSQL, applies migrations, and the scanner begins walking your source tree immediately.'
  },
  {
    title: 'Open the dashboard',
    body: 'Visit the frontend on :3000 (the API is on :5050), watch the conveyor fill, and clear anything that lands in your Inbox. An AcoustID key is optional but makes matching far more confident.'
  }
];

/* ──────────────────────────────────────────────────────────────────────────────
 * Hero: the live-pipeline log card. Stages/providers are all REAL.
 * ────────────────────────────────────────────────────────────────────────────── */

export type LogLevel = 'ok' | 'warn';
export const demoHeroLog: ReadonlyArray<readonly [stage: string, msg: string, level: LogLevel]> = [
  ['scan', 'discovered 47 new audio files', 'ok'],
  ['fp', 'AcoustID match (0.94) → 9c1ecd05…', 'ok'],
  ['match', '7 providers queried · 240ms avg', 'ok'],
  ['decide', 'consensus: Radiohead — In Rainbows', 'ok'],
  ['grade', 'quality LLM scored "Nude" → 94/100', 'ok'],
  ['dedupe', 'duplicate → keeping FLAC over 320', 'warn'],
  ['write', '→ /music/destination/Radiohead/2007 - In Rainbows/03 - Nude.flac', 'ok'],
  ['decide', 'low consensus (0.62) → sent to Inbox', 'warn']
];

/* ──────────────────────────────────────────────────────────────────────────────
 * Pipeline conveyor: the REAL 7-stage pipeline (Scanner → Fingerprint → Enrichment →
 * AI grade → Duplicate detection → LibraryBuilder). Status 'Running' drives the live
 * highlight inside the reused PipelineStageCard.
 * ────────────────────────────────────────────────────────────────────────────── */

export interface DemoStage {
  id: string;
  icon: Component<{ class?: string }>;
  label: string;
  /** One-line descriptor shown under the conveyor (what this stage does). */
  sub: string;
  /** 'Running' highlights the card as live; anything else renders dimmed/done. */
  status: string;
  count: number;
  total: number;
  perSec: number;
}

export const demoStages: ReadonlyArray<DemoStage> = [
  { id: 'scan', icon: ScanLine, label: 'Scan', sub: 'walk the source tree, sniff real formats', status: 'Done', count: 12847, total: 12847, perSec: 0 },
  { id: 'fingerprint', icon: AudioWaveform, label: 'Fingerprint', sub: 'Chromaprint / fpcalc acoustic hash', status: 'Running', count: 38, total: 12847, perSec: 142 },
  { id: 'match', icon: Tag, label: 'Match', sub: 'query 7 providers, reach consensus', status: 'Running', count: 26, total: 12809, perSec: 31 },
  { id: 'grade', icon: Sparkles, label: 'AI grade', sub: 'quality LLM scores the match 0–100', status: 'Running', count: 9, total: 12783, perSec: 4 },
  { id: 'dedupe', icon: Copy, label: 'Dedupe', sub: 'group by fingerprint, keep the best copy', status: 'Done', count: 2, total: 12774, perSec: 1 },
  { id: 'build', icon: FolderTree, label: 'Build', sub: 'copy, tag, sort into Artist / Year - Album', status: 'Running', count: 5, total: 12772, perSec: 8 },
  { id: 'library', icon: Library, label: 'Library', sub: 'clean files on your own disk', status: 'Done', count: 8955, total: 8955, perSec: 0 }
];

/**
 * The seven REAL enrichment providers (enum `EnrichmentProvider`). `provider` strings match the
 * backend names so the conveyor can resolve friendly labels + brand colors via review-helpers'
 * `providerLabel()` / `providerColor()`. `Tracker` / `YeTracker` are community trackers.
 */
export interface DemoMatchProvider {
  provider: string;
  kind: 'fingerprint' | 'metadata' | 'community';
  status: 'Matched' | 'NoMatch';
  /** 0–1 confidence for a match; null when the provider returned nothing. */
  confidence: number | null;
}

export const demoMatchProviders: ReadonlyArray<DemoMatchProvider> = [
  { provider: 'AcoustID', kind: 'fingerprint', status: 'Matched', confidence: 0.99 },
  { provider: 'MusicBrainzWeb', kind: 'metadata', status: 'Matched', confidence: 0.96 },
  { provider: 'SpotifyAPI', kind: 'metadata', status: 'Matched', confidence: 0.94 },
  { provider: 'Deezer', kind: 'metadata', status: 'Matched', confidence: 0.88 },
  { provider: 'AppleMusic', kind: 'metadata', status: 'NoMatch', confidence: null },
  { provider: 'Tracker', kind: 'community', status: 'NoMatch', confidence: null },
  { provider: 'YeTracker', kind: 'community', status: 'NoMatch', confidence: null }
];

/** In-flight item chips shown when a non-Match stage is selected in the conveyor. */
export const demoInFlight: Record<string, ReadonlyArray<{ name: string; meta: string; warn?: boolean }>> = {
  scan: [
    { name: 'WhatsApp Audio 2024-…opus', meta: '2.1 MB' },
    { name: 'IMG_2014_audio.m4a', meta: '14 MB' }
  ],
  fingerprint: [
    { name: '01 - Intro.flac', meta: 'computing fp' },
    { name: 'track_047.flac', meta: 'computing fp' }
  ],
  grade: [
    { name: 'Nude.flac', meta: 'scoring…' },
    { name: 'Pyramids.flac', meta: 'scoring…' }
  ],
  dedupe: [{ name: 'Get Lucky.mp3 vs .flac', meta: 'keep flac' }],
  build: [{ name: '03 - Nude.flac', meta: 'tagging + sorting' }],
  library: []
};

/* ──────────────────────────────────────────────────────────────────────────────
 * Inbox: the three human-review buckets that map to the real InboxV2 tabs.
 * ────────────────────────────────────────────────────────────────────────────── */

export interface DemoInboxCard {
  kind: 'tag' | 'duplicate' | 'ai';
  count: number;
  label: string;
  desc: string;
  cta: string;
}

export const demoInboxCards: ReadonlyArray<DemoInboxCard> = [
  {
    kind: 'tag',
    count: 12,
    label: 'Tag reviews',
    desc: 'Providers couldn’t agree, or none cleared the confidence threshold. You pick the right candidate or override the fields by hand.',
    cta: 'Review'
  },
  {
    kind: 'duplicate',
    count: 4,
    label: 'Ambiguous duplicates',
    desc: 'Same acoustic fingerprint, no obvious winner — usually a live vs. studio cut or two near-identical bitrates. You choose what to keep.',
    cta: 'Compare'
  },
  {
    kind: 'ai',
    count: 3,
    label: 'AI flagged',
    desc: 'The quality LLM graded a match Questionable or Wrong — a year drift, a rewritten title, a path that doesn’t fit. Worth a second look.',
    cta: 'Inspect'
  }
];

/* ──────────────────────────────────────────────────────────────────────────────
 * Library showcase: real AlbumSummary[] for the reused LibraryAlbumsGridV2.
 * `songs: []` makes the play button a safe no-op; `coverUrl: null` renders the
 * deterministic tinted-initials tile (no network) via Cover.
 * ────────────────────────────────────────────────────────────────────────────── */

function demoAlbum(
  artist: string,
  title: string,
  year: number,
  trackCount: number,
  genre: string,
  addedAtUtc: string
): AlbumSummary {
  return {
    key: `${artist.toLowerCase()}::${title.toLowerCase()}`,
    title,
    artist,
    year,
    trackCount,
    durationSeconds: trackCount * 230,
    byteSize: trackCount * 34_000_000,
    genre,
    musicBrainzReleaseId: null,
    coverUrl: null,
    addedAtUtc,
    songs: []
  };
}

export const demoAlbums: AlbumSummary[] = [
  demoAlbum('Radiohead', 'In Rainbows', 2007, 10, 'Alternative', '2026-05-01T12:00:00Z'),
  demoAlbum('Frank Ocean', 'Blonde', 2016, 17, 'R&B', '2026-04-30T12:00:00Z'),
  demoAlbum('Daft Punk', 'Random Access Memories', 2013, 13, 'Electronic', '2026-04-29T12:00:00Z'),
  demoAlbum('Kendrick Lamar', 'To Pimp a Butterfly', 2015, 16, 'Hip-Hop', '2026-04-28T12:00:00Z'),
  demoAlbum('Tame Impala', 'Currents', 2015, 13, 'Psychedelic', '2026-04-27T12:00:00Z'),
  demoAlbum('Radiohead', 'Kid A', 2000, 10, 'Alternative', '2026-04-26T12:00:00Z'),
  demoAlbum('Daft Punk', 'Discovery', 2001, 14, 'Electronic', '2026-04-25T12:00:00Z'),
  demoAlbum('Frank Ocean', 'Channel Orange', 2012, 17, 'R&B', '2026-04-24T12:00:00Z'),
  demoAlbum('Kendrick Lamar', 'DAMN.', 2017, 14, 'Hip-Hop', '2026-04-23T12:00:00Z'),
  demoAlbum('Radiohead', 'OK Computer', 1997, 12, 'Alternative', '2026-04-22T12:00:00Z'),
  demoAlbum('Bonobo', 'Migration', 2017, 12, 'Electronic', '2026-04-21T12:00:00Z'),
  demoAlbum('Sufjan Stevens', 'Illinois', 2005, 22, 'Indie Folk', '2026-04-20T12:00:00Z')
];

/** Library KPI strip, derived from the demo albums but shown as real-looking totals. */
export const demoLibraryStats = {
  tracks: 8955,
  albums: 412,
  artists: 168,
  enrichedPct: 90.4
};

/* ──────────────────────────────────────────────────────────────────────────────
 * Provenance: a real ApiSong + EnrichmentDetail. ProvenanceShowcase feeds these to
 * the REAL buildTimeline() / contributedProviders() helpers, so the timeline is
 * reconstructed by the same code the app uses on real tracks.
 * ────────────────────────────────────────────────────────────────────────────── */

export const demoProvenanceSong: ApiSong = {
  id: 1,
  sourcePath: '/music/source/Downloads/music_dump_2024/track_047.flac',
  destinationPath: '/music/destination/Radiohead/2007 - In Rainbows/03 - Nude.flac',
  fileName: 'track_047.flac',
  extension: '.flac',
  fileSizeBytes: 31_800_000,
  artist: 'Radiohead',
  albumArtist: 'Radiohead',
  album: 'In Rainbows',
  title: 'Nude',
  year: 2007,
  trackNumber: 3,
  durationSeconds: 257,
  fingerprint: 'AQADtGmYhEkSRZEGzEfx48jx40jx4_jx4_jxF8mPo8mP',
  isrc: 'GBAYE0601498',
  musicBrainzId: '9c1ecd05-3f2e-4f3b-9a1c-0e2d4b6a8c10',
  musicBrainzReleaseId: '1b022e01-4da6-387b-8658-8c7e0a9f8b6e',
  spotifyId: '3SVAN3BRByDmHOhKyIDxfC',
  acoustIdTrackId: 'b6a8c10e-2d4b-4f3b-9a1c-9c1ecd053f2e',
  lrclibId: '8841422',
  enrichmentStatus: 'Matched',
  indexedAtUtc: '2026-05-01T14:32:01.842Z',
  libraryBuiltAtUtc: '2026-05-01T14:32:02.521Z',
  libraryBuildStatus: 'Done',
  matchedBy: 'AcoustID',
  matchConfidence: 0.94,
  matchWarnings: null,
  lyricsStatus: 'Fetched',
  hasSyncedLyrics: true,
  sampleRate: 44100,
  bitRate: 1024,
  hasCoverArt: true
};

export const demoProvenanceDetail: EnrichmentDetail = {
  id: 1,
  fileName: 'track_047.flac',
  sourcePath: demoProvenanceSong.sourcePath,
  destinationPath: demoProvenanceSong.destinationPath,
  enrichmentStatus: 'Matched',
  isManuallyApproved: false,
  matchedBy: 'AcoustID',
  matchConfidence: 0.94,
  matchWarnings: null,
  originalMetadataCaptured: true,
  // The file arrived with placeholder tags — no usable title/artist.
  source: {
    title: null,
    artist: null,
    album: null,
    year: null,
    trackNumber: 3
  },
  current: {
    title: 'Nude',
    artist: 'Radiohead',
    albumArtist: 'Radiohead',
    album: 'In Rainbows',
    year: 2007,
    trackNumber: 3,
    isrc: 'GBAYE0601498',
    musicBrainzId: '9c1ecd05-3f2e-4f3b-9a1c-0e2d4b6a8c10',
    spotifyId: '3SVAN3BRByDmHOhKyIDxfC',
    matchedBy: 'AcoustID',
    matchConfidence: 0.94
  },
  diff: [
    { field: 'title', source: 'Track 03', current: 'Nude' },
    { field: 'artist', source: '', current: 'Radiohead' },
    { field: 'album', source: '', current: 'In Rainbows' },
    { field: 'year', source: '', current: 2007 }
  ],
  providerAttempts: [
    {
      provider: 'AcoustID',
      status: 'Matched',
      attemptedAtUtc: '2026-05-01T14:32:01.903Z',
      searchQuery: null,
      candidate: {
        title: 'Nude',
        artist: 'Radiohead',
        album: 'In Rainbows',
        year: 2007,
        trackNumber: 3,
        acoustIdTrackId: 'b6a8c10e-2d4b-4f3b-9a1c-9c1ecd053f2e',
        matchedBy: 'AcoustID',
        matchConfidence: 0.94
      }
    },
    {
      provider: 'MusicBrainzWeb',
      status: 'Matched',
      attemptedAtUtc: '2026-05-01T14:32:02.046Z',
      searchQuery: 'Radiohead Nude',
      candidate: {
        title: 'Nude',
        artist: 'Radiohead',
        albumArtist: 'Radiohead',
        album: 'In Rainbows',
        year: 2007,
        trackNumber: 3,
        musicBrainzId: '9c1ecd05-3f2e-4f3b-9a1c-0e2d4b6a8c10',
        musicBrainzReleaseId: '1b022e01-4da6-387b-8658-8c7e0a9f8b6e',
        matchedBy: 'MusicBrainzWeb',
        matchConfidence: 0.96
      }
    },
    {
      provider: 'SpotifyAPI',
      status: 'Matched',
      attemptedAtUtc: '2026-05-01T14:32:02.180Z',
      searchQuery: 'Radiohead Nude In Rainbows',
      candidate: {
        title: 'Nude',
        artist: 'Radiohead',
        album: 'In Rainbows',
        year: 2007,
        spotifyId: '3SVAN3BRByDmHOhKyIDxfC',
        matchedBy: 'SpotifyAPI',
        matchConfidence: 0.94
      }
    },
    {
      provider: 'Deezer',
      status: 'NoMatch',
      attemptedAtUtc: '2026-05-01T14:32:02.240Z',
      searchQuery: 'Radiohead Nude',
      candidate: null
    }
  ],
  changeLog: []
};

/** A real quality grade for the provenance hero + AI-grade timeline event. Model intentionally
 *  unnamed (it's swappable — OpenRouter/OpenAI-compatible, default varies by deployment). */
export const demoProvenanceGrade: SongQualityGradeView = {
  songId: 1,
  graded: true,
  score: 94,
  verdict: 'Excellent',
  summary: 'Fingerprint + 3-provider consensus all agree; paths and tags are consistent.',
  model: null,
  promptVersion: 2,
  gradedAtUtc: '2026-05-01T14:32:02.418Z',
  durationMs: 217
};

/** Shared logic for the manual-review screens (desktop + mobile). */

import type {
  ApiSong,
  EnrichmentCandidate,
  EnrichmentDetail,
  ProviderAttempt
} from '$lib/api-client';

export type ReviewReasonKey = 'low_confidence' | 'multiple_matches' | 'no_fingerprint';

export interface ReviewReason {
  key: ReviewReasonKey;
  label: string;
  /** Pill tint: maps to the design's warn/info/err palette. */
  tint: 'warn' | 'info' | 'err';
}

/**
 * Why a track landed in the review queue. The backend doesn't store an explicit
 * reason enum, so it's derived from the same signals the pipeline uses: a missing
 * fingerprint, several competing matches, or a single low-confidence match.
 */
export function reasonFor(track: Pick<ApiSong, 'fingerprint' | 'matchWarnings' | 'matchConfidence'>): ReviewReason {
  if (!track.fingerprint) return { key: 'no_fingerprint', label: 'No fingerprint', tint: 'err' };
  if ((track.matchWarnings?.length ?? 0) > 1)
    return { key: 'multiple_matches', label: 'Multiple matches', tint: 'info' };
  if (track.matchConfidence != null && track.matchConfidence < 0.7)
    return { key: 'low_confidence', label: 'Low confidence', tint: 'warn' };
  return { key: 'low_confidence', label: 'Low confidence', tint: 'warn' };
}

/** Friendly source label for a provider name (handles backend enum + demo names). */
export function providerLabel(name: string | null | undefined): string {
  switch (name) {
    case 'SpotifyAPI':
      return 'Spotify';
    case 'MusicBrainzWeb':
      return 'MusicBrainz';
    case 'AppleMusic':
      return 'Apple Music';
    case 'Tracker':
      return 'Community Tracker';
    case 'AcoustID':
    case 'Deezer':
    default:
      return name ?? 'Unknown';
  }
}

export interface ReviewCandidate {
  /** Stable key for keyed-each blocks. */
  key: string;
  source: string;
  score: number | null;
  title: string;
  artist: string;
  album: string;
  year: string;
  /** Fields applied to the metadata form when this candidate is picked. */
  fields: { title: string; artist: string; album: string; year: string };
}

/**
 * Provider attempts → selectable candidate rows, best score first. Only attempts
 * that actually produced a candidate are shown (matches the design, which lists
 * provider matches only — embedded tags are rendered separately).
 */
export function candidatesFromDetail(detail: EnrichmentDetail | null | undefined): ReviewCandidate[] {
  if (!detail) return [];
  return detail.providerAttempts
    .filter((a): a is ProviderAttempt & { candidate: NonNullable<ProviderAttempt['candidate']> } => a.candidate != null)
    .map((a) => {
      const c = a.candidate;
      const title = c.title ?? '';
      const artist = c.artist ?? '';
      const album = c.album ?? '';
      const year = c.year != null ? String(c.year) : '';
      return {
        key: `${a.provider}:${title}:${album}`,
        source: providerLabel(c.matchedBy ?? a.provider),
        score: c.matchConfidence ?? null,
        title,
        artist,
        album,
        year,
        fields: { title, artist, album, year }
      } satisfies ReviewCandidate;
    })
    .sort((a, b) => (b.score ?? 0) - (a.score ?? 0));
}

export interface ProviderAttemptRow {
  /** Stable key for keyed-each blocks. */
  key: string;
  /** Friendly provider label. */
  source: string;
  /** Raw attempt status: Matched | NoMatch | RateLimited | Failed. */
  status: string;
  /** Whether this attempt produced a candidate. */
  matched: boolean;
  score: number | null;
  title: string;
  artist: string;
  album: string;
  year: string;
  error: string | null;
  /** The consensus winner that was applied to the song. */
  chosen: boolean;
}

/**
 * Every provider attempt (matched, no-match, and failed) as read-only display rows
 * for the Fingerprint tab — matched rows first by score, then the rest. The winner
 * is derived from the song's `matchedBy` since the backend stores no explicit flag.
 */
export function providerAttemptRows(detail: EnrichmentDetail | null | undefined): ProviderAttemptRow[] {
  if (!detail) return [];
  const matchedBy = detail.matchedBy ?? null;
  return detail.providerAttempts
    .map((a) => {
      const c = a.candidate;
      const title = c?.title ?? '';
      const artist = c?.artist ?? '';
      const album = c?.album ?? '';
      const year = c?.year != null ? String(c.year) : '';
      return {
        key: `${a.provider}:${title}:${album}`,
        source: providerLabel(c?.matchedBy ?? a.provider),
        status: a.status,
        matched: c != null,
        score: c?.matchConfidence ?? null,
        title,
        artist,
        album,
        year,
        error: a.error ?? null,
        chosen: matchedBy != null && (c?.matchedBy === matchedBy || a.provider === matchedBy)
      } satisfies ProviderAttemptRow;
    })
    .sort((a, b) => {
      if (a.matched !== b.matched) return a.matched ? -1 : 1;
      return (b.score ?? 0) - (a.score ?? 0);
    });
}

export interface EmbeddedTag {
  key: string;
  value: string;
}

/** The file's embedded id3 tags as ordered key/value rows for display. */
export function embeddedTags(track: Pick<ApiSong, 'title' | 'artist' | 'album' | 'year' | 'trackNumber'>): EmbeddedTag[] {
  return [
    { key: 'title', value: track.title ?? '' },
    { key: 'artist', value: track.artist ?? '' },
    { key: 'album', value: track.album ?? '' },
    { key: 'year', value: track.year != null ? String(track.year) : '' },
    { key: 'track', value: track.trackNumber != null ? String(track.trackNumber) : '' }
  ];
}

/** Where the file lands once written, previewed live from the edited fields. */
export function buildDestinationPath(
  fields: { artist?: string; album?: string; year?: string | number; title?: string },
  extension?: string | null
): string {
  const ext = (extension ?? '').replace(/^\./, '').toLowerCase() || 'flac';
  const artist = (fields.artist || '?').toString();
  const album = (fields.album || '?').toString();
  const year = (fields.year || '?').toString();
  const title = (fields.title || '?').toString();
  return `~/Music/Library/${artist}/${album} (${year})/01 ${title}.${ext}`;
}

/**
 * Deterministic bar heights (percent) for the decorative Chromaprint visual,
 * derived from the fingerprint string so each track renders a stable waveform.
 */
export function fingerprintBars(fingerprint: string | null | undefined, count = 96): number[] {
  const seed = fingerprint && fingerprint.length > 2 ? fingerprint.charCodeAt(2) : 7;
  return Array.from({ length: count }, (_, i) => {
    const h = ((i * 37 + seed * 13) % 100) / 100;
    return 15 + h * 85;
  });
}

/** Truncated, repeated hash line shown under the fingerprint visual (decorative). */
export function fingerprintHash(fingerprint: string | null | undefined): string {
  if (!fingerprint) return '';
  return `${fingerprint.repeat(5).slice(0, 140)}…`;
}

/* ------------------------------------------------------------------ *
 * Provenance & review — header, before→after, timeline, origin matrix
 * ------------------------------------------------------------------ */

/** Brand-ish dot colour per enrichment source (ported from the design's source palette). */
export function providerColor(name: string | null | undefined): string {
  switch (providerLabel(name)) {
    case 'AcoustID':
      return '#6a89cc';
    case 'Spotify':
      return '#1db954';
    case 'MusicBrainz':
      return '#ba478f';
    case 'Apple Music':
      return '#fa243c';
    case 'Community Tracker':
      return '#4a9a6a';
    default:
      switch (name) {
        case 'Discogs':
          return '#5b5b5b';
        case 'Deezer':
          return '#ff5500';
        default:
          return '#9a9a9a';
      }
  }
}

/**
 * A public search URL for a provider + query, so a reviewer can verify a result themselves.
 * Returns null when the provider has no linkable web search (AcoustID is fingerprint-only;
 * the YeTracker is a static local catalog).
 */
export function providerSearchUrl(provider: string | null | undefined, query: string | null | undefined): string | null {
  if (!query || !query.trim()) return null;
  const q = encodeURIComponent(query.trim());
  switch (provider) {
    case 'SpotifyAPI':
      return `https://open.spotify.com/search/${q}`;
    case 'AppleMusic':
      return `https://music.apple.com/search?term=${q}`;
    case 'Deezer':
      return `https://www.deezer.com/search/${q}`;
    case 'MusicBrainzWeb':
      return `https://musicbrainz.org/search?query=${q}&type=recording&method=indexed`;
    case 'Tracker':
      return `https://juicewrldapi.com/juicewrld/songs/?format=json&search=${q}`;
    case 'AcoustID':
    case 'YeTracker':
    default:
      return null;
  }
}

export interface ContributedProvider {
  label: string;
  color: string;
}

/** Distinct providers that produced a candidate, for the CONTRIBUTED chip row. */
export function contributedProviders(detail: EnrichmentDetail | null | undefined): ContributedProvider[] {
  if (!detail) return [];
  const seen = new Set<string>();
  const out: ContributedProvider[] = [];
  for (const a of detail.providerAttempts) {
    if (a.candidate == null) continue;
    const label = providerLabel(a.candidate.matchedBy ?? a.provider);
    if (seen.has(label)) continue;
    seen.add(label);
    out.push({ label, color: providerColor(a.candidate.matchedBy ?? a.provider) });
  }
  return out;
}

/** Total wall-clock the pipeline spent on this song, derived from stored timestamps. */
export function elapsedMs(track: ApiSong, detail: EnrichmentDetail | null | undefined): number | null {
  const times: number[] = [];
  const push = (iso: string | null | undefined) => {
    if (!iso) return;
    const t = new Date(iso).getTime();
    if (Number.isFinite(t)) times.push(t);
  };
  push(track.indexedAtUtc);
  push(track.libraryBuiltAtUtc);
  if (detail) {
    push(detail.manuallyApprovedAtUtc);
    for (const a of detail.providerAttempts) push(a.attemptedAtUtc);
    for (const c of detail.changeLog) {
      push(c.createdAtUtc);
      push(c.appliedAtUtc);
    }
  }
  if (times.length < 2) return null;
  return Math.max(...times) - Math.min(...times);
}

/** "79ms" / "1.2s" / "3m 05s" — honest formatting of a derived duration. */
export function formatElapsed(ms: number | null): string {
  if (ms == null) return '—';
  if (ms < 1000) return `${ms}ms`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`;
  const m = Math.floor(ms / 60_000);
  const s = Math.round((ms % 60_000) / 1000);
  return `${m}m ${String(s).padStart(2, '0')}s`;
}

/** The big DECISION KPI: where the song currently stands. */
export function decisionLabel(detail: EnrichmentDetail | null | undefined): string {
  const status = (detail?.enrichmentStatus ?? '').toLowerCase();
  if (status === 'needsreview') return 'PENDING';
  if (status === 'matched') return detail?.isManuallyApproved ? 'ACCEPTED' : 'MATCHED';
  if (status === 'failed') return 'FAILED';
  if (status === 'pending') return 'QUEUED';
  return status ? status.toUpperCase() : '—';
}

export interface BestGuess {
  /** Title used in the header / queue row. */
  title: string;
  /** "Artist · Album" subtitle. */
  subtitle: string;
  /** True while the match is still unconfirmed (NeedsReview) — render "(best guess)". */
  isGuess: boolean;
}

/** Header title + subtitle, from the top candidate or the current metadata. */
export function bestGuess(
  track: ApiSong,
  candidates: ReviewCandidate[],
  detail: EnrichmentDetail | null | undefined
): BestGuess {
  const top = candidates[0];
  const cur = detail?.current;
  const title = top?.title || cur?.title || track.title || track.fileName;
  const artist = top?.artist || cur?.artist || track.artist || '';
  const album = top?.album || cur?.album || track.album || '';
  const subtitle = [artist, album].filter(Boolean).join(' · ');
  const isGuess = (detail?.enrichmentStatus ?? '').toLowerCase() === 'needsreview';
  return { title, subtitle, isGuess };
}

/** Status banner tone + copy for the detail header. */
export interface BannerInfo {
  tone: 'warn' | 'info' | 'err' | 'ok';
  title: string;
  body: string;
}

export function bannerFor(
  track: ApiSong,
  detail: EnrichmentDetail | null | undefined
): BannerInfo {
  const status = (detail?.enrichmentStatus ?? String(track.enrichmentStatus ?? '')).toLowerCase();
  if (status === 'matched') {
    return detail?.isManuallyApproved
      ? { tone: 'ok', title: 'Accepted', body: 'You approved this match — it will be written to the library.' }
      : { tone: 'ok', title: 'Matched', body: 'Auto-accepted above the confidence threshold. Re-enrich to recompute.' };
  }
  if (status === 'failed') {
    return { tone: 'err', title: 'Enrichment failed', body: detail?.enrichmentError || 'No provider could match this file.' };
  }
  const reason = reasonFor(track);
  if (reason.key === 'no_fingerprint') {
    return {
      tone: 'err',
      title: 'No fingerprint match',
      body: 'AcoustID returned nothing — pick a candidate, enter metadata manually, or skip.'
    };
  }
  if (reason.key === 'multiple_matches') {
    return {
      tone: 'info',
      title: 'Awaiting review · Ambiguous',
      body: 'Several providers disagree — pick the correct candidate or correct the fields.'
    };
  }
  return {
    tone: 'warn',
    title: 'Awaiting review · Low confidence',
    body: 'Top candidate is below the auto-accept threshold — pick or correct.'
  };
}

/* ----- Before → After field table ----- */

/** Form-editable fields, in display order. Matches what `manual-review` persists. */
export const EDITABLE_FIELDS = [
  { key: 'title', label: 'Title', changeName: 'Title' },
  { key: 'artist', label: 'Artist', changeName: 'Artist' },
  { key: 'albumArtist', label: 'Album artist', changeName: 'AlbumArtist' },
  { key: 'album', label: 'Album', changeName: 'Album' },
  { key: 'year', label: 'Year', changeName: 'Year' },
  { key: 'trackNumber', label: 'Track #', changeName: 'TrackNumber' }
] as const;

export type EditableFieldKey = (typeof EDITABLE_FIELDS)[number]['key'];

function candidateValue(c: EnrichmentCandidate | null | undefined, key: string): string {
  if (!c) return '';
  switch (key) {
    case 'title':
      return c.title ?? '';
    case 'artist':
      return c.artist ?? '';
    case 'albumArtist':
      return c.albumArtist ?? '';
    case 'album':
      return c.album ?? '';
    case 'year':
      return c.year != null ? String(c.year) : '';
    case 'trackNumber':
      return c.trackNumber != null ? String(c.trackNumber) : '';
    case 'isrc':
      return c.isrc ?? '';
    case 'musicBrainzId':
      return c.musicBrainzId ?? '';
    case 'spotifyId':
      return c.spotifyId ?? '';
    default:
      return '';
  }
}

export interface BeforeAfterRow {
  key: EditableFieldKey;
  label: string;
  /** Embedded (original/source) value as it was in the file. */
  embedded: string;
  /** Provider that supplied the current value (if any). */
  sourceLabel: string | null;
  sourceColor: string | null;
  sourcePct: number | null;
}

/**
 * Rows for the FIELD / EMBEDDED / FINAL / SOURCE table. FINAL is rendered by the
 * page as an editable input bound to its own edit state; this provides the
 * read-only EMBEDDED value and SOURCE attribution per field.
 */
export function beforeAfterRows(detail: EnrichmentDetail | null | undefined): BeforeAfterRow[] {
  const src = detail?.source;
  return EDITABLE_FIELDS.map((f) => {
    // Latest applied change for this field gives the most reliable source attribution.
    const change = detail?.changeLog?.find(
      (c) => c.field.toLowerCase() === f.changeName.toLowerCase() && c.applied
    );
    let sourceLabel: string | null = null;
    let sourcePct: number | null = null;
    if (change?.source) {
      sourceLabel = providerLabel(change.source);
      sourcePct = change.confidence != null ? Math.round(change.confidence * 100) : null;
    } else if (detail?.matchedBy) {
      // Fall back to the consensus winner when no per-field change row exists.
      sourceLabel = providerLabel(detail.matchedBy);
      sourcePct = detail.matchConfidence != null ? Math.round(detail.matchConfidence * 100) : null;
    }
    return {
      key: f.key,
      label: f.label,
      embedded: candidateValue(src, f.key),
      sourceLabel,
      sourceColor: sourceLabel ? providerColor(change?.source ?? detail?.matchedBy) : null,
      sourcePct
    } satisfies BeforeAfterRow;
  });
}

/* ----- Timeline ----- */

export type TimelineTint = 'ok' | 'warn' | 'err' | 'info' | 'neutral';

export interface TimelineEvent {
  key: string;
  /** ISO timestamp. */
  time: string;
  /** Stage pill label (SCAN, FINGERPRINT, FP LOOKUP, METADATA, FLAG, MATCHED, WRITE). */
  stage: string;
  tint: TimelineTint;
  provider: { label: string; color: string; pct: number | null } | null;
  description: string;
  /** The term this provider searched, if any (shown as "searched …" with an optional link). */
  searchQuery?: string | null;
  /** Public search URL to verify the query on the provider, if linkable. */
  searchUrl?: string | null;
  /** ms since the previous event. */
  deltaMs: number | null;
}

/** Reconstruct what the pipeline did, oldest-first, from stored timestamps + attempts. */
export function buildTimeline(track: ApiSong, detail: EnrichmentDetail | null | undefined): TimelineEvent[] {
  const events: Omit<TimelineEvent, 'deltaMs'>[] = [];
  const attempts = detail?.providerAttempts ?? [];
  const firstAttempt = attempts
    .map((a) => new Date(a.attemptedAtUtc).getTime())
    .filter((n) => Number.isFinite(n))
    .sort((a, b) => a - b)[0];

  if (track.indexedAtUtc) {
    const noTags = !detail?.source?.title && !detail?.source?.artist;
    events.push({
      key: 'scan',
      time: track.indexedAtUtc,
      stage: 'SCAN',
      tint: 'neutral',
      provider: { label: 'Embedded tags', color: providerColor(null), pct: null },
      description: `discovered ${track.fileName}${noTags ? ' · no usable ID3' : ''}`
    });
  }
  if (track.fingerprint && firstAttempt) {
    events.push({
      key: 'fp',
      time: new Date(firstAttempt).toISOString(),
      stage: 'FINGERPRINT',
      tint: 'info',
      provider: { label: 'AcoustID', color: providerColor('AcoustID'), pct: null },
      description: 'Chromaprint fingerprint computed'
    });
  }

  for (const a of [...attempts].sort(
    (x, y) => new Date(x.attemptedAtUtc).getTime() - new Date(y.attemptedAtUtc).getTime()
  )) {
    const label = providerLabel(a.candidate?.matchedBy ?? a.provider);
    const pct = a.candidate?.matchConfidence != null ? Math.round(a.candidate.matchConfidence * 100) : null;
    const stage = providerLabel(a.provider) === 'AcoustID' ? 'FP LOOKUP' : 'METADATA';
    let tint: TimelineTint = 'ok';
    let description: string;
    switch (a.status) {
      case 'Matched':
        description = `${label} matched · ${a.candidate?.title ?? 'candidate'}`;
        break;
      case 'NoMatch':
        tint = 'warn';
        description = `${label} returned no match`;
        break;
      case 'RateLimited':
        tint = 'warn';
        description = `${label} rate limited — will retry`;
        break;
      case 'Failed':
        tint = 'err';
        description = `${label} failed${a.error ? `: ${a.error}` : ''}`;
        break;
      default:
        description = `${label} ${a.status.toLowerCase()}`;
    }
    events.push({
      key: `attempt:${a.provider}:${a.attemptedAtUtc}`,
      time: a.attemptedAtUtc,
      stage,
      tint,
      provider: { label, color: providerColor(a.candidate?.matchedBy ?? a.provider), pct },
      description,
      searchQuery: a.searchQuery ?? null,
      searchUrl: providerSearchUrl(a.provider, a.searchQuery)
    });
  }

  // Terminal event: flagged for review, matched, or written.
  const status = (detail?.enrichmentStatus ?? '').toLowerCase();
  const lastTime =
    events.length > 0 ? events[events.length - 1].time : (track.indexedAtUtc ?? new Date().toISOString());
  if (track.libraryBuiltAtUtc) {
    events.push({
      key: 'write',
      time: track.libraryBuiltAtUtc,
      stage: 'WRITE',
      tint: 'ok',
      provider: null,
      description: 'written to destination library'
    });
  } else if (status === 'needsreview') {
    const reason = reasonFor(track);
    events.push({
      key: 'flag',
      time: detail?.manuallyApprovedAtUtc ?? lastTime,
      stage: 'FLAG',
      tint: 'warn',
      provider: { label: 'Embedded tags', color: providerColor(null), pct: null },
      description: `awaiting manual review (${reason.label.toLowerCase()})`
    });
  } else if (status === 'matched') {
    events.push({
      key: 'matched',
      time: detail?.manuallyApprovedAtUtc ?? lastTime,
      stage: 'MATCHED',
      tint: 'ok',
      provider: detail?.matchedBy
        ? {
            label: providerLabel(detail.matchedBy),
            color: providerColor(detail.matchedBy),
            pct: detail.matchConfidence != null ? Math.round(detail.matchConfidence * 100) : null
          }
        : null,
      description: detail?.isManuallyApproved ? 'accepted by reviewer' : 'auto-accepted by consensus'
    });
  }

  // Sort and compute deltas.
  const sorted = events
    .map((e) => ({ ...e, t: new Date(e.time).getTime() }))
    .sort((a, b) => a.t - b.t);
  let prev: number | null = null;
  return sorted.map(({ t, ...e }) => {
    const deltaMs = prev != null && Number.isFinite(t) ? Math.max(0, t - prev) : null;
    prev = Number.isFinite(t) ? t : prev;
    return { ...e, deltaMs } satisfies TimelineEvent;
  });
}

/* ----- Origin matrix ----- */

export interface MatrixColumn {
  key: string;
  label: string;
  color: string;
  isEmbedded: boolean;
}

export interface MatrixCell {
  value: string | null;
  winning: boolean;
  pct: number | null;
}

export interface MatrixRow {
  field: string;
  label: string;
  missing: boolean;
  cells: MatrixCell[];
}

export interface OriginMatrix {
  columns: MatrixColumn[];
  rows: MatrixRow[];
}

const MATRIX_FIELDS = [
  { key: 'title', label: 'Title' },
  { key: 'artist', label: 'Artist' },
  { key: 'album', label: 'Album' },
  { key: 'year', label: 'Year' },
  { key: 'trackNumber', label: 'Track #' },
  { key: 'isrc', label: 'ISRC' },
  { key: 'musicBrainzId', label: 'MBID' },
  { key: 'spotifyId', label: 'Spotify ID' }
] as const;

/**
 * Field × provider grid: which provider proposed which value. The winning
 * provider column (the consensus winner) is highlighted with its confidence.
 */
export function buildOriginMatrix(detail: EnrichmentDetail | null | undefined): OriginMatrix {
  if (!detail) return { columns: [], rows: [] };
  const matchedBy = detail.matchedBy ?? null;
  const contributors = detail.providerAttempts.filter((a) => a.candidate != null);

  const columns: MatrixColumn[] = [
    { key: '__embedded', label: 'Embedded tags', color: providerColor(null), isEmbedded: true },
    ...contributors.map((a) => ({
      key: a.provider,
      label: providerLabel(a.candidate?.matchedBy ?? a.provider),
      color: providerColor(a.candidate?.matchedBy ?? a.provider),
      isEmbedded: false
    }))
  ];

  const isWinner = (a: ProviderAttempt) =>
    matchedBy != null && (a.provider === matchedBy || a.candidate?.matchedBy === matchedBy);

  const rows: MatrixRow[] = MATRIX_FIELDS.map((f) => {
    const cells: MatrixCell[] = [
      { value: candidateValue(detail.source, f.key) || null, winning: false, pct: null },
      ...contributors.map((a) => {
        const value = candidateValue(a.candidate, f.key) || null;
        const winning = value != null && isWinner(a);
        const pct =
          winning && a.candidate?.matchConfidence != null
            ? Math.round(a.candidate.matchConfidence * 100)
            : null;
        return { value, winning, pct } satisfies MatrixCell;
      })
    ];
    const missing = cells.every((c) => c.value == null);
    return { field: f.key, label: f.label, missing, cells } satisfies MatrixRow;
  });

  return { columns, rows };
}

/** Shared logic for the manual-review screens (desktop + mobile). */

import type { ApiSong, EnrichmentDetail, ProviderAttempt } from '$lib/api-client';

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

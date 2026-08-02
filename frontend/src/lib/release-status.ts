/**
 * Released vs unreleased. The classification itself is derived server-side (`ReleaseClassifier`)
 * from what enrichment already recorded, so the client only reads the verdict.
 *
 * Two tiers of "unreleased", deliberately kept apart because their confidence differs a lot:
 * `Unreleased` is a community tracker saying so outright, `LikelyUnreleased` is the absence of any
 * match anywhere. The library filter shows both; anything that needs precision should not.
 */
import type { ApiSong } from '$lib/api-client';

/** A community tracker catalogued this as a leak, snippet, demo, stem or session file. */
export function isUnreleasedSong(song: ApiSong): boolean {
  return song.releaseClassification === 'Unreleased';
}

/** Every provider ran and none found anything — probably unreleased, but no catalog says so. */
export function isLikelyUnreleasedSong(song: ApiSong): boolean {
  return song.releaseClassification === 'LikelyUnreleased';
}

/** Either tier. This is what the library's "Unreleased" filter uses. */
export function isAnyUnreleasedSong(song: ApiSong): boolean {
  return isUnreleasedSong(song) || isLikelyUnreleasedSong(song);
}

/** Confirmed present in a commercial catalog. NOT the inverse of the above — a track with no
 *  evidence either way classifies as "Unknown" and is neither. */
export function isReleasedSong(song: ApiSong): boolean {
  return song.releaseClassification === 'Released';
}

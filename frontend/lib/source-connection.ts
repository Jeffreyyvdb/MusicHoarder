import type { LyricsStatus } from "@/lib/types"
import { lrclibWebSearchUrl } from "@/lib/lrclib-url"

/** True when enrichment was attributed to AcoustID (provider name from API). */
export function matchedViaAcoustId(matchedBy?: string): boolean {
  return Boolean(matchedBy?.toLowerCase().includes("acoustid"))
}

/** Green check: stored AcoustID track id, or match was produced via AcoustID fingerprint lookup. */
export function acoustIdSourceConnected(
  acoustIdTrackId: string | undefined,
  matchedBy?: string
): boolean {
  return Boolean(acoustIdTrackId) || matchedViaAcoustId(matchedBy)
}

/**
 * Green check: stored LRCLIB id, lyrics pipeline ran against LRCLIB, or enriched track has a web search URL.
 */
export function lrclibSourceConnected(options: {
  lrclibId?: string
  lyricsStatus?: LyricsStatus
  artist?: string
  title?: string
  enrichmentStatus?: string
}): boolean {
  if (options.lrclibId) return true
  const ls = options.lyricsStatus
  if (ls === "Fetched" || ls === "Instrumental" || ls === "NotFound") return true
  if (
    options.enrichmentStatus === "complete" &&
    Boolean(lrclibWebSearchUrl(options.artist, options.title))
  ) {
    return true
  }
  return false
}

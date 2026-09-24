// Client for the anonymous share surface (/api/share/{token}) consumed by the public
// /share/[token] page. Kept separate from api-client.ts: these calls carry no session and
// must work for visitors without an account. All requests still go through the same-origin
// /api/mh proxy, so no CORS is involved.

import type { LyricsProvenance } from "$lib/types"

const API_PREFIX = "/api/mh"

export interface ShareTrack {
  id: number
  title: string
  artist?: string | null
  trackNumber?: number | null
  discNumber?: number | null
  durationMs?: number | null
  hasCoverArt: boolean
  hasSyncedLyrics: boolean
  hasPlainLyrics: boolean
  isInstrumental: boolean
  /** A Ready music video rides along on the share; played muted behind the page. */
  hasVideo: boolean
  /** Owner-curated sync: videoTime = audioTime + videoOffsetMs / 1000. */
  videoOffsetMs?: number | null
  videoDurationSeconds?: number | null
  /** The bars baked into the video's frame, which the backdrop crops (see SongVideoInfo). */
  videoLetterbox?: number | null
  videoPillarbox?: number | null
}

export interface SharePayload {
  scope: "Song" | "Album"
  sharedSongId: number
  album: { title?: string | null; artist?: string | null; year?: number | null }
  tracks: ShareTrack[]
}

export interface ShareLyrics {
  id: number
  synced?: string | null
  plain?: string | null
  isInstrumental: boolean
  /**
   * How much of these lyrics came from an AI. The share page is read by people who never see the
   * library UI, so the disclosure has to travel with the words rather than live in the app.
   */
  lyricsProvenance?: LyricsProvenance | null
  /** LLM pronunciation guide + English translation — present only when fresh (never stale docs). */
  romanizedSynced?: string | null
  romanizedPlain?: string | null
  translatedSynced?: string | null
  translatedPlain?: string | null
  detectedLanguage?: string | null
}

/**
 * Load a share's playable tracklist + display metadata. Takes the caller's `fetch` so the
 * page load works during SSR (SvelteKit resolves the relative proxy route internally).
 */
export async function fetchSharePayload(fetchFn: typeof fetch, token: string): Promise<SharePayload> {
  const response = await fetchFn(`${API_PREFIX}/api/share/${encodeURIComponent(token)}`, { cache: "no-store" })
  if (!response.ok) {
    throw Object.assign(new Error("This share link does not exist or has been revoked."), {
      status: response.status,
    })
  }
  return (await response.json()) as SharePayload
}

export async function fetchShareLyrics(token: string, songId: number): Promise<ShareLyrics> {
  const response = await fetch(shareApiUrl(token, songId, "lyrics"), { cache: "no-store" })
  if (!response.ok) throw new Error(`Could not load lyrics (${response.status}).`)
  return (await response.json()) as ShareLyrics
}

export function shareStreamUrl(token: string, songId: number): string {
  return shareApiUrl(token, songId, "stream")
}

export function shareVideoStreamUrl(token: string, songId: number): string {
  return shareApiUrl(token, songId, "video/stream")
}

export function shareCoverUrl(token: string, songId: number, size?: number): string {
  const base = shareApiUrl(token, songId, "cover")
  return size ? `${base}?size=${Math.round(size)}` : base
}

function shareApiUrl(token: string, songId: number, leaf: string): string {
  return `${API_PREFIX}/api/share/${encodeURIComponent(token)}/songs/${songId}/${leaf}`
}

// ── Open/play beacons (counted on the owner's Share links page) ──────────────────────────────

/**
 * The referrer worth reporting: another site's (TikTok, a chat app, `android-app://…` from Chrome
 * on Android), never this origin's own — a reload or an in-app hop says nothing about where the
 * visitor came from.
 */
export function externalReferrer(referrer: string, origin: string): string | null {
  if (!referrer) return null
  try {
    return new URL(referrer).origin === origin ? null : referrer
  } catch {
    return null
  }
}

/**
 * Fire-and-forget: tell the server this page was opened. A script beacon rather than a count in the
 * payload load, because link-preview crawlers fetch the server-rendered page for its og-tags and run
 * no script. `keepalive` lets it finish if the visitor leaves at once. Failures are nobody's concern.
 */
export function reportShareVisit(token: string, referrer: string | null): void {
  void fetch(`${API_PREFIX}/api/share/${encodeURIComponent(token)}/visit`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ referrer }),
    keepalive: true,
    cache: "no-store",
  }).catch(() => {})
}

/** Fire-and-forget: a shared track started playing. */
export function reportSharePlay(token: string, songId: number): void {
  void fetch(shareApiUrl(token, songId, "play"), {
    method: "POST",
    keepalive: true,
    cache: "no-store",
  }).catch(() => {})
}

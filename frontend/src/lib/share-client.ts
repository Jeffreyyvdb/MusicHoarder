// Client for the anonymous share surface (/api/share/{token}) consumed by the public
// /share/[token] page. Kept separate from api-client.ts: these calls carry no session and
// must work for visitors without an account. All requests still go through the same-origin
// /api/mh proxy, so no CORS is involved.

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

export function shareCoverUrl(token: string, songId: number, size?: number): string {
  const base = shareApiUrl(token, songId, "cover")
  return size ? `${base}?size=${Math.round(size)}` : base
}

function shareApiUrl(token: string, songId: number, leaf: string): string {
  return `${API_PREFIX}/api/share/${encodeURIComponent(token)}/songs/${songId}/${leaf}`
}

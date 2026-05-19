import { mockSpotifyLikedSongs, mockSpotifyPlaylists } from "$lib/mock-data"
import type { SpotifyLibraryMatchInfo } from "$lib/api-client"

const DEMO_CONNECTED_AT = "2025-01-15T12:00:00.000Z"

/** Virtual liked-song count for pagination demo (seed tracks are cycled). */
const DEMO_LIKED_TOTAL = 127

function mapSeedTrackToApi(
  seed: (typeof mockSpotifyLikedSongs)[0],
  instanceIndex: number
) {
  const durationMs = seed.duration * 1000
  const dayMs = 86_400_000
  const addedAt = new Date(Date.now() - (instanceIndex % 90) * dayMs).toISOString()
  const suffix = instanceIndex > 0 ? ` (${instanceIndex + 1})` : ""
  return {
    spotifyId:
      instanceIndex === 0 ? seed.id : `${seed.id}-x${instanceIndex}`,
    title: instanceIndex === 0 ? seed.name : `${seed.name}${suffix}`,
    artist: seed.artist,
    album: seed.album,
    albumArt: seed.albumArt ?? null,
    durationMs,
    addedAt,
  }
}

function buildDemoLikedTracksPool() {
  const seeds = mockSpotifyLikedSongs
  if (seeds.length === 0) return []
  const pool: ReturnType<typeof mapSeedTrackToApi>[] = []
  for (let i = 0; i < DEMO_LIKED_TOTAL; i++) {
    pool.push(mapSeedTrackToApi(seeds[i % seeds.length]!, i))
  }
  return pool
}

const likedTracksPool = buildDemoLikedTracksPool()

function toLibraryMatchInfo(
  matchStatus: DemoMatchStatus,
  matchedTrack: {
    id: number
    title: string | null
    artist: string | null
    enrichmentStatus: string
  } | null,
  matchConfidence: number | null
): SpotifyLibraryMatchInfo {
  return {
    matchStatus,
    matchedSongId: matchedTrack?.id ?? null,
    matchConfidence,
    matchedTitle: matchedTrack?.title ?? null,
    matchedArtist: matchedTrack?.artist ?? null,
    matchedEnrichmentStatus: matchedTrack?.enrichmentStatus ?? null,
  }
}

type DemoMatchStatus = "InLibrary" | "PossibleMatch" | "NotInLibrary"

function classifyDemoComparison(globalIndex: number): {
  matchStatus: DemoMatchStatus
  matchedTrack: {
    id: number
    title: string | null
    artist: string | null
    enrichmentStatus: string
  } | null
  matchConfidence: number | null
} {
  const mod = globalIndex % 10
  const localSongId = (globalIndex % 180) + 1
  if (mod < 6) {
    return {
      matchStatus: "InLibrary",
      matchedTrack: {
        id: localSongId,
        title: `Local title ${globalIndex}`,
        artist: `Local artist ${globalIndex % 20}`,
        enrichmentStatus: globalIndex % 3 === 0 ? "Matched" : "NeedsReview",
      },
      matchConfidence: globalIndex % 2 === 0 ? 1 : 0.95,
    }
  }
  if (mod < 8) {
    return {
      matchStatus: "PossibleMatch",
      matchedTrack: {
        id: localSongId + 500,
        title: `Near match for track ${globalIndex}`,
        artist: `Similar Artist ${globalIndex % 7}`,
        enrichmentStatus: "Pending",
      },
      matchConfidence: 86 + (globalIndex % 8),
    }
  }
  return { matchStatus: "NotInLibrary", matchedTrack: null, matchConfidence: null }
}

function attachDemoLibraryMatch<T extends ReturnType<typeof mapSeedTrackToApi>>(
  track: T,
  globalIndex: number
): T & { libraryMatch: SpotifyLibraryMatchInfo } {
  const { matchStatus, matchedTrack, matchConfidence } = classifyDemoComparison(globalIndex)
  return {
    ...track,
    libraryMatch: toLibraryMatchInfo(matchStatus, matchedTrack, matchConfidence),
  }
}

function mapPlaylistToApi(p: (typeof mockSpotifyPlaylists)[0]) {
  return {
    spotifyId: p.id,
    name: p.name,
    description: p.description ?? null,
    imageUrl: p.image ?? null,
    trackCount: p.trackCount,
    ownerName: p.owner ?? null,
  }
}

/** Cap tracks generated per playlist for demo performance. */
function effectivePlaylistTrackCount(declared: number): number {
  return Math.min(declared, 72)
}

function buildTracksForPlaylist(playlistId: string, count: number) {
  const n = effectivePlaylistTrackCount(count)
  if (n === 0 || likedTracksPool.length === 0) return []
  let h = 0
  for (let i = 0; i < playlistId.length; i++) {
    h = (h * 31 + playlistId.charCodeAt(i)) | 0
  }
  const start = Math.abs(h) % likedTracksPool.length
  const out: ReturnType<typeof mapSeedTrackToApi>[] = []
  for (let i = 0; i < n; i++) {
    const base = likedTracksPool[(start + i) % likedTracksPool.length]!
    out.push({
      ...base,
      spotifyId: `${playlistId}-track-${i}`,
      addedAt: new Date(Date.now() - i * 3_600_000).toISOString(),
    })
  }
  return out
}

const playlistTracksCache = new Map<string, ReturnType<typeof mapSeedTrackToApi>[]>()

function getPlaylistTrackList(playlistId: string) {
  let cached = playlistTracksCache.get(playlistId)
  if (cached) return cached
  const meta = mockSpotifyPlaylists.find((p) => p.id === playlistId)
  const count = meta?.trackCount ?? 0
  cached = buildTracksForPlaylist(playlistId, count)
  playlistTracksCache.set(playlistId, cached)
  return cached
}

export function getDemoSpotifyStatus() {
  return {
    connected: true,
    connectedAt: DEMO_CONNECTED_AT,
    hasCredentials: true,
    tokenExpired: false,
  }
}

export function getDemoSpotifyCredentials() {
  return {
    clientId: "demo-client-id",
    hasClientSecret: true,
  }
}

export function getDemoSpotifyLikedSongs(offset: number, limit: number) {
  const total = likedTracksPool.length
  const items = likedTracksPool
    .slice(offset, offset + limit)
    .map((track, i) => attachDemoLibraryMatch(track, offset + i))
  return { total, offset, limit, items }
}

export function getDemoSpotifyPlaylists() {
  return {
    items: mockSpotifyPlaylists.map(mapPlaylistToApi),
  }
}

export function getDemoSpotifyPlaylistTracks(playlistId: string, offset: number, limit: number) {
  const all = getPlaylistTrackList(playlistId)
  const total = all.length
  const items = all
    .slice(offset, offset + limit)
    .map((track, i) => attachDemoLibraryMatch(track, offset + i))
  return { total, offset, limit, items }
}

export function getDemoSpotifyDisconnectMessage() {
  return { message: "Spotify account disconnected (demo)." }
}

export function getDemoSpotifySaveCredentialsMessage() {
  return { message: "Spotify credentials saved (demo)." }
}

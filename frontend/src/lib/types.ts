export type LyricsStatus = "NotFetched" | "Fetched" | "Instrumental" | "NotFound" | "Failed"

/**
 * How much of the lyrics on screen came from an AI. Mirrors the API's `LyricsProvenance` enum.
 *
 * - `Human` — human-contributed LRCLIB lyrics, timing untouched. No badge.
 * - `AiEnhanced` — the song's own words, re-timed by AI (a forced alignment, or a measured offset
 *   applied to an LRC that was running early or late).
 * - `AiGenerated` — an AI transcribed the audio; both the words and the timing are the machine's.
 */
export type LyricsProvenance = "Human" | "AiEnhanced" | "AiGenerated"

/** Outcome of checking a stored LRC's timestamps against the audio. Mirrors the API enum. */
export type LyricsSyncStatus =
  | "NotChecked"
  | "Ok"
  | "Suspect"
  | "Corrected"
  | "Unverifiable"

export interface TrackMetadata {
  title: string
  artist: string
  album: string
  year: number
  genre: string
  duration: number
  bitrate: number
  sampleRate: number
  format: string
  fileSize: number
  albumArt?: string
  lyrics?: string
  syncedLyrics?: string
  plainLyrics?: string
  lyricsStatus?: LyricsStatus
  hasSyncedLyrics?: boolean
  hasPlainLyrics?: boolean
  isInstrumental?: boolean
  fingerprint?: string
  enrichmentStatus: "pending" | "processing" | "complete" | "failed" | "needsreview"
  matchedBy?: string
  sources: {
    musicbrainz?: boolean
    lastfm?: boolean
    spotify?: boolean
    genius?: boolean
  }
  sourceIds?: {
    musicBrainzId?: string
    musicBrainzReleaseId?: string
    spotifyId?: string
    acoustIdTrackId?: string
    lrclibId?: string
  }
}

export type ImportStatus = "discovered" | "fingerprinting" | "enriching" | "copying" | "review" | "complete" | "failed"

export interface ImportJob {
  id: string
  sourcePath: string
  destinationPath: string
  status: "running" | "paused" | "completed"
  startedAt: Date
  tracksDiscovered: number
  tracksProcessed: number
  tracksCopied: number
  tracksReview: number
  tracksFailed: number
}

export interface TrackImport {
  id: string
  originalPath: string
  destinationPath?: string
  status: ImportStatus
  metadata?: TrackMetadata
  suggestedMetadata?: Partial<TrackMetadata>
  issues?: string[]
  fingerprint?: string
}

// Spotify Sync Types
export interface SpotifyPlaylist {
  id: string
  name: string
  description?: string
  image?: string
  trackCount: number
  owner: string
  syncStatus: "not_synced" | "syncing" | "synced" | "partial"
  tracksMatched: number
  lastSynced?: Date
}

export interface SpotifyTrack {
  id: string
  name: string
  artist: string
  album: string
  albumArt?: string
  duration: number
  inLibrary: boolean
  matchConfidence?: number
}

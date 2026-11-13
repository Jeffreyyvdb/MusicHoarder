// The requests worth watching, in the order a listener meets them: opening the app, opening an
// album, playing a track. Add a line here to put a new endpoint under measurement.

/** Ids and names taken from the target's own library, so every path below hits a real row. */
export interface Sample {
  songId: number
  /** A second built song, so a "next track" request is not served from anything the first warmed. */
  nextSongId: number
  lyricsSongId: number
  artist: string
  album: string
  year: number | null
  playlistId: number | null
  /** Forty song ids, the size of a long album page's provenance request. */
  albumSongIds: number[]
}

export interface Budget {
  /** 95th percentile of the whole request, called directly, with one caller. */
  p95Ms?: number
  /** Response body after gzip — what a compressing proxy puts on the wire. */
  gzipBytes?: number
}

export interface Endpoint {
  name: string
  group: 'Open the app' | 'On every start, for the admin' | 'Browse' | 'Play a track' | 'Manage'
  path: (sample: Sample) => string | null
  budget?: Budget
  /** A `Range` header, for media requests that are never read whole. */
  range?: string
  /** Also time it through the frontend's `/api/mh` proxy, the way the browser calls it. */
  viaProxy?: boolean
  /** Images and audio: no JSON breakdown, no gzip figure. */
  binary?: boolean
  /**
   * Time the request the way a client that already holds the answer sends it: ask once to learn
   * the `ETag`, then measure with it in `If-None-Match`. The row is the 304, with no body — what
   * finding out that nothing changed costs. Any other answer is reported as a failure.
   */
  revalidate?: boolean
}

const q = encodeURIComponent

export const ENDPOINTS: Endpoint[] = [
  { name: 'Session', group: 'Open the app', path: () => '/api/auth/me', budget: { p95Ms: 50 } },
  { name: 'Song list', group: 'Open the app', path: () => '/songs', viaProxy: true, budget: { p95Ms: 800, gzipBytes: 3_000_000 } },
  { name: 'Album cards', group: 'Open the app', path: () => '/api/albums', viaProxy: true, budget: { p95Ms: 300, gzipBytes: 500_000 } },
  // A page visit with the library already loaded and nothing changed since: the web app sends back
  // the tag each list came with. These only run their SQL on Postgres, so this is where it is run.
  { name: 'Song list, unchanged', group: 'Open the app', path: () => '/songs?includeDeleted=false', revalidate: true, viaProxy: true, budget: { p95Ms: 60 } },
  { name: 'Album cards, unchanged', group: 'Open the app', path: () => '/api/albums', revalidate: true, viaProxy: true, budget: { p95Ms: 60 } },
  { name: 'Playlists', group: 'Open the app', path: () => '/api/playlists', budget: { p95Ms: 150 } },
  { name: 'Playback session', group: 'Open the app', path: () => '/api/playback', budget: { p95Ms: 50 } },
  { name: 'Overview', group: 'Open the app', path: () => '/overview', budget: { p95Ms: 300 } },
  { name: 'Stats', group: 'Open the app', path: () => '/stats', budget: { p95Ms: 200 } },

  // What the shell asks for on every document load to fill the Inbox badges and status rows.
  { name: 'Duplicate tracks', group: 'On every start, for the admin', path: () => '/api/library/duplicates', budget: { p95Ms: 200 } },
  { name: 'Inbox name counts', group: 'On every start, for the admin', path: () => '/api/inbox/name-counts', budget: { p95Ms: 300 } },
  // The three reports those two counts stand in for. Only the Inbox's Artist names and Album names
  // pages ask for them now; they stay in this group so a run still lines up with the ones before it.
  { name: 'Duplicate albums', group: 'On every start, for the admin', path: () => '/api/library/albums/duplicates', budget: { p95Ms: 200 } },
  { name: 'Duplicate artists', group: 'On every start, for the admin', path: () => '/api/library/artists/duplicates', budget: { p95Ms: 200 } },
  { name: 'Split albums', group: 'On every start, for the admin', path: () => '/api/enrichment/split-albums', budget: { p95Ms: 200 } },
  { name: 'Quality overview', group: 'On every start, for the admin', path: () => '/api/quality/overview', budget: { p95Ms: 200 } },
  { name: 'Quality progress', group: 'On every start, for the admin', path: () => '/api/quality/progress', budget: { p95Ms: 100 } },
  { name: 'Library availability', group: 'On every start, for the admin', path: () => '/api/enrichment/library-availability', budget: { p95Ms: 100 } },
  { name: 'Storage', group: 'On every start, for the admin', path: () => '/storage', budget: { p95Ms: 100 } },
  { name: 'Conversations', group: 'On every start, for the admin', path: () => '/api/chat/conversations', budget: { p95Ms: 100 } },
  { name: 'Version', group: 'On every start, for the admin', path: () => '/api/version', budget: { p95Ms: 50 } },

  { name: 'Album detail', group: 'Browse', path: (s) => `/api/albums/detail?artist=${q(s.artist)}&album=${q(s.album)}${s.year ? `&year=${s.year}` : ''}`, budget: { p95Ms: 150 } },
  { name: 'Album timeline', group: 'Browse', path: (s) => `/api/albums/timeline?artist=${q(s.artist)}&album=${q(s.album)}`, budget: { p95Ms: 200 } },
  { name: 'Provenance (40 songs)', group: 'Browse', path: (s) => `/api/songs/provenance?ids=${s.albumSongIds.join(',')}`, budget: { p95Ms: 150 } },
  { name: 'Playlist', group: 'Browse', path: (s) => (s.playlistId ? `/api/playlists/${s.playlistId}` : null), budget: { p95Ms: 150 } },
  { name: 'Cover thumbnail (cold then cached)', group: 'Browse', path: (s) => `/songs/${s.songId}/cover?size=300`, binary: true, viaProxy: true, budget: { p95Ms: 60 } },
  { name: 'Cover, full size', group: 'Browse', path: (s) => `/songs/${s.songId}/cover`, binary: true, budget: { p95Ms: 100 } },

  { name: 'Stream, first 256 kB', group: 'Play a track', path: (s) => `/songs/${s.songId}/stream`, range: 'bytes=0-262143', binary: true, viaProxy: true, budget: { p95Ms: 80 } },
  { name: 'Stream decoded to WAV, first 256 kB', group: 'Play a track', path: (s) => `/songs/${s.nextSongId}/stream?format=wav`, range: 'bytes=0-262143', binary: true, budget: { p95Ms: 400 } },
  { name: 'Lyrics', group: 'Play a track', path: (s) => `/api/tracks/${s.lyricsSongId}/lyrics`, budget: { p95Ms: 60 } },
  { name: 'Track detail', group: 'Play a track', path: (s) => `/songs/${s.songId}/enrichment-detail`, budget: { p95Ms: 150 } },
  { name: 'Radio (autoplay)', group: 'Play a track', path: (s) => `/api/radio?seedSongId=${s.songId}&limit=25`, budget: { p95Ms: 300 } },

  { name: 'History feed', group: 'Manage', path: () => '/api/history', budget: { p95Ms: 500 } },
  { name: 'Insights', group: 'Manage', path: () => '/insights', budget: { p95Ms: 500 } },
  { name: 'Directory tree', group: 'Manage', path: () => '/library/directory-tree', budget: { p95Ms: 500 } },
]

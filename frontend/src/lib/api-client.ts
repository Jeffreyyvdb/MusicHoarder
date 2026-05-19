import type { FileItem } from "$lib/types"
import { isDemoMode } from "$lib/app-mode"
import { mockFileSystem, mockImportJob } from "$lib/mock-data"
import {
  getDemoSpotifyCredentials,
  getDemoSpotifyDisconnectMessage,
  getDemoSpotifyLikedSongs,
  getDemoSpotifyPlaylistTracks,
  getDemoSpotifyPlaylists,
  getDemoSpotifySaveCredentialsMessage,
  getDemoSpotifyStatus,
} from "$lib/mock-spotify-api"

const API_PREFIX = "/api/mh"
const INFRASTRUCTURE_PREFIX_SEGMENT = /^(volumes?|mnt|media|srv|share|shares|nas|data|storage|music|musichoarder|library|source|destination|users|home|tmp)$/i

export interface ApiStats {
  tracks?: {
    total?: number
    deleted?: number
  }
  storage?: {
    totalBytes?: number
    totalGiB?: number
  }
}

export interface ApiOverviewActivity {
  id: string
  type: "discovered" | "copied" | "enriched" | "review" | "failed"
  track: string
  artist: string
  time: string
}

export interface ApiOverviewScan {
  scanId: string
  totalFiles: number
  processed: number
  newFiles: number
  changedFiles: number
  skippedFiles: number
  failedFiles: number
  isComplete: boolean
  startedAt: string
  completedAt?: string | null
}

export interface ApiOverviewEnrichment {
  runId: string
  totalTracks: number
  processed: number
  enriched: number
  failed: number
  needsReview: number
  isComplete: boolean
  startedAt: string
  completedAt?: string | null
}

export interface ApiOverview {
  sourcePath: string
  destinationPath: string
  scan?: ApiOverviewScan | null
  enrichment?: ApiOverviewEnrichment | null
  job: {
    status: "running" | "completed"
    startedAt: string
    tracksDiscovered: number
    tracksProcessed: number
    tracksFingerprinted: number
    tracksEnriched: number
    tracksBuildEligible: number
    tracksCopied: number
    tracksReview: number
    tracksFailed: number
  }
  recentActivity: ApiOverviewActivity[]
}

export interface ApiSong {
  id: number
  sourcePath: string
  destinationPath?: string | null
  fileName: string
  extension?: string | null
  fileSizeBytes: number
  artist?: string | null
  albumArtist?: string | null
  album?: string | null
  title?: string | null
  year?: number | null
  trackNumber?: number | null
  durationSeconds?: number | null
  fingerprint?: string | null
  musicBrainzId?: string | null
  musicBrainzReleaseId?: string | null
  spotifyId?: string | null
  acoustIdTrackId?: string | null
  lrclibId?: string | null
  enrichmentStatus?: string | number | null
  matchedBy?: string | null
  matchConfidence?: number | null
  matchWarnings?: string[] | null
  enrichmentError?: string | null
  originalMetadataCaptured?: boolean | null
  originalArtist?: string | null
  originalAlbumArtist?: string | null
  originalAlbum?: string | null
  originalTitle?: string | null
  originalYear?: number | null
  originalTrackNumber?: number | null
  lyricsStatus?: string | null
  hasSyncedLyrics?: boolean | null
  hasPlainLyrics?: boolean | null
  isInstrumental?: boolean | null
  syncedLyrics?: string | null
  plainLyrics?: string | null
  /** Sample rate in Hz (e.g. 44100). Shown in track details when present. */
  sampleRate?: number | null
  /** Bitrate in kbps (e.g. 320, 1411). Shown in track details when present. */
  bitRate?: number | null
  /** Optional album cover URL. Populated in demo mode; the real backend currently
   * leaves this unset and the UI falls back to an initials tile. */
  albumArt?: string | null
}

interface SongsResponse {
  count: number
  includeDeleted: boolean
  songs: ApiSong[]
}

export type LibraryPathMode = "destination" | "source"

async function requestJson<T>(path: string, init?: RequestInit): Promise<T> {
  const method = (init?.method ?? "GET").toUpperCase()
  const isBodyMethod = method !== "GET" && method !== "HEAD"

  const response = await fetch(`${API_PREFIX}${path}`, {
    ...init,
    headers: isBodyMethod
      ? {
          "content-type": "application/json",
          ...(init?.headers ?? {}),
        }
      : init?.headers,
    cache: "no-store",
  })

  if (!response.ok) {
    let detail = ""
    try {
      const body = await response.json() as Record<string, unknown>
      detail = (body.message as string) ?? JSON.stringify(body)
    } catch {
      // ignore parse errors
    }
    throw new Error(detail || `Request failed for ${path}: ${response.status}`)
  }

  return (await response.json()) as T
}

function flattenAudioFiles(items: FileItem[]): FileItem[] {
  const audioFiles: FileItem[] = []
  const stack = [...items]

  while (stack.length > 0) {
    const item = stack.pop()
    if (!item) continue

    if (item.type === "audio") {
      audioFiles.push(item)
      continue
    }

    if (item.children?.length) {
      stack.push(...item.children)
    }
  }

  return audioFiles
}

function deriveExtension(fileName: string): string | null {
  const lastDot = fileName.lastIndexOf(".")
  if (lastDot < 0 || lastDot === fileName.length - 1) return null
  return fileName.slice(lastDot)
}

const SOURCE_ROOT = "/Volumes/music"
const DESTINATION_ROOT = "/Music Library"

function safePathSegment(segment: string): string {
  return segment.replace(/[/\\]/g, "").trim() || "Unknown"
}

function buildDemoDestinationPath(
  fileName: string,
  artist?: string | null,
  album?: string | null
): string {
  const artistSegment = artist ? safePathSegment(artist) : "Unknown"
  const albumSegment = album ? safePathSegment(album) : "Unknown Album"
  return `${DESTINATION_ROOT}/${artistSegment}/${albumSegment}/${fileName}`
}

function buildDemoSongs(): ApiSong[] {
  const audioFiles = flattenAudioFiles(mockFileSystem)
  const realSongs = audioFiles.map((file, index) => {
    const extension = deriveExtension(file.name)
    const artist = file.metadata?.artist ?? null
    const album = file.metadata?.album ?? null
    const hasUsableMetadata =
      artist && album && artist !== "Unknown Artist" && album !== "Unknown Album" && album !== "Unknown"
    const destinationPath = hasUsableMetadata
      ? buildDemoDestinationPath(file.name, artist, album)
      : `${DESTINATION_ROOT}/Unknown/${file.name}`

    return {
      id: index + 1,
      sourcePath: file.path,
      destinationPath,
      fileName: file.name,
      extension,
      fileSizeBytes: file.metadata?.fileSize ?? 0,
      artist,
      album,
      title: file.metadata?.title ?? null,
      year: file.metadata?.year ?? null,
      durationSeconds: file.metadata?.duration ?? null,
      fingerprint: file.metadata?.fingerprint ?? null,
      musicBrainzId: null,
      spotifyId: null,
      enrichmentStatus: file.metadata?.enrichmentStatus ?? null,
      lyricsStatus: file.metadata?.lyricsStatus ?? null,
      hasSyncedLyrics: file.metadata?.hasSyncedLyrics ?? null,
      hasPlainLyrics: file.metadata?.hasPlainLyrics ?? null,
      isInstrumental: file.metadata?.isInstrumental ?? null,
      syncedLyrics: file.metadata?.syncedLyrics ?? null,
      plainLyrics: file.metadata?.plainLyrics ?? null,
      sampleRate: file.metadata?.sampleRate ?? null,
      bitRate: file.metadata?.bitrate ?? null,
    }
  })
  const totalFromReal = realSongs.reduce((sum, s) => sum + s.fileSizeBytes, 0)
  const synthetic = buildSyntheticDemoSongs(realSongs.length + 1, totalFromReal)
  return [...realSongs, ...synthetic]
}

/** Target total demo library size in bytes (~110 GB) for a MusicHoarder-style library. */
const DEMO_TOTAL_BYTES_TARGET = 110 * 1024 * 1024 * 1024
/** Number of synthetic tracks to add so the demo feels like a large library. */
const DEMO_SYNTHETIC_TRACK_COUNT = 2100

/** Messy top-level source folders (like real hoarded sources: downloads, rips, backups). */
const MESSY_SOURCE_ROOTS = [
  "Downloads",
  "CD Rips",
  "Music Backup",
  "New Folder",
  "Import 2023",
  "From Phone",
  "Rips",
  "Unsorted",
  "Music (1)",
  "Transfer 2022",
  "My Music",
  "random",
  "2022 Import",
  "Old Library",
  "iTunes Export",
  "Spotify Export",
  "Backup Jan",
]

/** Messy subfolder patterns: date folders, disc folders, duplicates, inconsistent casing. */
const MESSY_SUBFOLDERS = [
  "2022-11",
  "2020-03",
  "2023-01",
  "Disc 1",
  "disc2",
  "Disc1",
  "Album",
  "Tracks",
  " (1)",
  " - copy",
  "",
]

function buildMessySourcePath(
  index: number,
  artist: string,
  album: string,
  fileName: string
): string {
  const rootIndex = index % MESSY_SOURCE_ROOTS.length
  const root = MESSY_SOURCE_ROOTS[rootIndex]
  const subIndex = (index >> 4) % MESSY_SUBFOLDERS.length
  const sub = MESSY_SUBFOLDERS[subIndex]

  // Mix: some under artist-like folders (but messy), some under date/random folders
  const useArtistFolder = index % 3 !== 0
  if (useArtistFolder) {
    // Inconsistent casing / naming: sometimes lowercase, sometimes "Artist (1)"
    const artistFolder =
      index % 5 === 0
        ? artist.toLowerCase()
        : index % 7 === 0
          ? `${artist} (1)`
          : index % 11 === 0
            ? artist.replace(/\s/g, "_")
            : artist
    const base = `${SOURCE_ROOT}/${root}/${artistFolder}`
    if (sub) {
      const albumFolder = sub.startsWith(" ") ? `${album}${sub}` : sub
      return `${base}/${albumFolder}/${fileName}`
    }
    return `${base}/${album}/${fileName}`
  }

  // No artist folder: e.g. Downloads/2022-11/01 - Track 1.flac or Unsorted/track_01.mp3
  const base = `${SOURCE_ROOT}/${root}`
  if (sub) return `${base}/${sub}/${fileName}`
  return `${base}/${fileName}`
}

const DEMO_SYNCED_LYRICS_POOL = [
  `[00:00.00] Sunrise on the avenue\n[00:04.50] Colors bleeding through the haze\n[00:09.00] Every step I take is new\n[00:13.50] Walking through these golden days\n[00:18.00] \n[00:19.50] Turn the dial and find a sound\n[00:24.00] Let the melody unwind\n[00:28.50] Echoes spinning all around\n[00:33.00] Leaving yesterday behind`,
  `[00:05.00] Neon lights and midnight rain\n[00:09.50] Driving down an empty lane\n[00:14.00] Radio is on again\n[00:18.50] Singing through the windowpane\n[00:23.00] \n[00:24.50] We don't need a masterplan\n[00:29.00] Just the road beneath our feet\n[00:33.50] Hand in hand we understand\n[00:38.00] Every stranger that we meet`,
  `[00:02.00] Woke up to a silent room\n[00:06.50] Shadows dancing on the wall\n[00:11.00] Petals from a wilted bloom\n[00:15.50] Scattered down the empty hall\n[00:20.00] \n[00:21.50] Time moves slow when you're alone\n[00:26.00] Counting cracks along the floor\n[00:30.50] Dialing on a broken phone\n[00:35.00] Waiting for a knock at the door`,
  `[00:08.00] Electric blue and cherry red\n[00:12.50] Frequencies inside my head\n[00:17.00] Dancing on a laser thread\n[00:21.50] Follow where the signal led\n[00:26.00] \n[00:27.50] Bass drop hits and walls collide\n[00:32.00] Neon pulses in my chest\n[00:36.50] Nothing left for us to hide\n[00:41.00] This is how we pass the test`,
  `[00:03.00] Morning fog across the bay\n[00:07.50] Coffee steam and yesterday\n[00:12.00] Pages turn and drift away\n[00:16.50] Words I never thought I'd say\n[00:21.00] \n[00:22.50] Take my hand and close your eyes\n[00:27.00] Feel the earth beneath your skin\n[00:31.50] Underneath these open skies\n[00:36.00] Let the healing now begin`,
  `[00:10.00] City hum and traffic flow\n[00:14.50] Skyscrapers begin to glow\n[00:19.00] Underground where rivers go\n[00:23.50] Secrets that the streets all know\n[00:28.00] \n[00:29.50] Subway doors slide open wide\n[00:34.00] Faces lost in passing light\n[00:38.50] Everyone has wounds inside\n[00:43.00] Holding on to make it right`,
  `[00:00.00] Starlight falling like the rain\n[00:04.50] Catching fire on the plain\n[00:09.00] Every ending starts again\n[00:13.50] Joy is born from letting go of pain\n[00:18.00] \n[00:19.50] Open up the rusted gate\n[00:24.00] Futures bloom from broken ground\n[00:28.50] It is never, ever, too late\n[00:33.00] To become what you have found`,
  `[00:06.00] Velvet sky at half past nine\n[00:10.50] Satellites begin to shine\n[00:15.00] Tracing out a dotted line\n[00:19.50] Connecting your world to mine\n[00:24.00] \n[00:25.50] Waves crash on a distant shore\n[00:30.00] Carrying the songs we wrote\n[00:34.50] Every tide reveals once more\n[00:39.00] Messages sealed inside a note`,
  `[00:04.00] Dusty roads and summer heat\n[00:08.50] Gravel crunching under feet\n[00:13.00] Lemonade on repeat\n[00:17.50] Lazy days are bittersweet\n[00:22.00] \n[00:23.50] Fireflies at half past eight\n[00:28.00] Porch light glowing amber gold\n[00:32.50] Stories that we'd annotate\n[00:37.00] Memories that never get old`,
  `[00:07.00] Pixel hearts on glowing screens\n[00:11.50] We are more than what it seems\n[00:16.00] Living somewhere in between\n[00:20.50] Reality and lucid dreams\n[00:25.00] \n[00:26.50] Upload all your fears tonight\n[00:31.00] Download courage, press restart\n[00:35.50] Every bug becomes a light\n[00:40.00] Code that's written from the heart`,
]

function demoSyncedLyricsForIndex(i: number): string {
  return DEMO_SYNCED_LYRICS_POOL[i % DEMO_SYNCED_LYRICS_POOL.length]
}

function demoPlainLyricsFromSynced(synced: string): string {
  return synced
    .split("\n")
    .map((line) => line.replace(/^\[\d{2}:\d{2}\.\d{2,3}\]\s*/, ""))
    .filter((line) => line.trim().length > 0)
    .join("\n")
}

// Artist → factual list of real album titles. Titles are short and factual;
// no lyrics, descriptions or creative content is reproduced.
const DEMO_DISCOGRAPHY: { artist: string; albums: string[] }[] = [
  {
    artist: "Arctic Monkeys",
    albums: [
      "AM",
      "Whatever People Say I Am, That's What I'm Not",
      "Favourite Worst Nightmare",
      "Humbug",
      "Suck It and See",
      "Tranquility Base Hotel & Casino",
      "The Car",
    ],
  },
  {
    artist: "Tame Impala",
    albums: ["Currents", "Lonerism", "Innerspeaker", "The Slow Rush"],
  },
  {
    artist: "Tyler, the Creator",
    albums: [
      "Flower Boy",
      "IGOR",
      "Call Me If You Get Lost",
      "Goblin",
      "Wolf",
      "Cherry Bomb",
      "Chromakopia",
    ],
  },
  {
    artist: "Kendrick Lamar",
    albums: [
      "DAMN.",
      "good kid, m.A.A.d city",
      "To Pimp a Butterfly",
      "Mr. Morale & the Big Steppers",
      "Section.80",
      "GNX",
    ],
  },
  {
    artist: "Billie Eilish",
    albums: [
      "When We All Fall Asleep, Where Do We Go?",
      "Happier Than Ever",
      "Hit Me Hard and Soft",
      "Don't Smile at Me",
    ],
  },
  {
    artist: "Lana Del Rey",
    albums: [
      "Born to Die",
      "Ultraviolence",
      "Honeymoon",
      "Lust for Life",
      "Norman Fucking Rockwell!",
      "Blue Banisters",
      "Did You Know That There's a Tunnel Under Ocean Blvd",
    ],
  },
  {
    artist: "The Strokes",
    albums: [
      "Is This It",
      "Room on Fire",
      "First Impressions of Earth",
      "Angles",
      "Comedown Machine",
      "The New Abnormal",
    ],
  },
  {
    artist: "MGMT",
    albums: [
      "Oracular Spectacular",
      "Congratulations",
      "MGMT",
      "Little Dark Age",
      "Loss of Life",
    ],
  },
  {
    artist: "Gorillaz",
    albums: [
      "Gorillaz",
      "Demon Days",
      "Plastic Beach",
      "Humanz",
      "The Now Now",
      "Song Machine, Season One: Strange Timez",
      "Cracker Island",
    ],
  },
  {
    artist: "LCD Soundsystem",
    albums: [
      "LCD Soundsystem",
      "Sound of Silver",
      "This Is Happening",
      "American Dream",
    ],
  },
  {
    artist: "Phoenix",
    albums: [
      "Wolfgang Amadeus Phoenix",
      "Bankrupt!",
      "Ti Amo",
      "Alpha Zulu",
      "It's Never Been Like That",
    ],
  },
  {
    artist: "Vampire Weekend",
    albums: [
      "Vampire Weekend",
      "Contra",
      "Modern Vampires of the City",
      "Father of the Bride",
      "Only God Was Above Us",
    ],
  },
  {
    artist: "Arcade Fire",
    albums: [
      "Funeral",
      "Neon Bible",
      "The Suburbs",
      "Reflektor",
      "Everything Now",
      "WE",
    ],
  },
  {
    artist: "Kanye West",
    albums: [
      "The College Dropout",
      "Late Registration",
      "Graduation",
      "808s & Heartbreak",
      "My Beautiful Dark Twisted Fantasy",
      "Yeezus",
      "The Life of Pablo",
      "ye",
      "Donda",
    ],
  },
  {
    artist: "Frank Ocean",
    albums: ["Channel Orange", "Blonde", "Endless", "Nostalgia, Ultra"],
  },
  {
    artist: "Childish Gambino",
    albums: [
      "Because the Internet",
      "Awaken, My Love!",
      "3.15.20",
      "Camp",
      "Atavista",
      "Bando Stone & the New World",
    ],
  },
  {
    artist: "Anderson .Paak",
    albums: ["Malibu", "Oxnard", "Ventura"],
  },
  {
    artist: "Mac DeMarco",
    albums: [
      "2",
      "Salad Days",
      "Another One",
      "This Old Dog",
      "Here Comes the Cowboy",
      "Five Easy Hot Dogs",
    ],
  },
]

// Generic evocative song-title fragments; not real song titles, just placeholders
// that read more naturally than "Track 1 / Track 2".
const DEMO_TRACK_TITLES = [
  "Golden Hour",
  "Velvet Hour",
  "Paper Planes",
  "Lighthouse",
  "Satellite",
  "No Good Reason",
  "Gold Dust",
  "Silver Tongue",
  "Rearview",
  "Headlights",
  "City Lights",
  "Northern Sky",
  "Tidal Wave",
  "Undertow",
  "Half Moon",
  "Pale Blue",
  "Slow Dance",
  "Neon Rush",
  "Static",
  "Home Again",
  "Open Sky",
  "The Drive",
  "Borderline",
  "Alibi",
  "Apology",
  "Orbit",
  "Eclipse",
  "Backroads",
  "Heat Lightning",
  "Dust and Bones",
  "Empty Rooms",
  "Second Chance",
  "Ghost Town",
  "Porcelain",
  "Sleeplines",
  "Crossfire",
]

// Stable 32-bit FNV-1a hash — deterministic cover / title selection from strings.
function hashString(value: string): number {
  let h = 2166136261
  for (let i = 0; i < value.length; i++) {
    h ^= value.charCodeAt(i)
    h = Math.imul(h, 16777619)
  }
  return h >>> 0
}

// Real cover art for the curated demo discography. URLs were resolved once
// against Deezer's public search API (https://api.deezer.com/search/album)
// and baked in here so the demo has actual album artwork without any runtime
// lookups, proxy routes, or rate-limit handling. If an album isn't in the map
// (e.g. the synthetic "(Disc N)" cycles or future additions), the synthesis
// falls back to a deterministic picsum photo for visual variety.
const DEMO_COVERS: Record<string, string> = {
  "Arctic Monkeys::AM":
    "https://cdn-images.dzcdn.net/images/cover/64e54e307bd5e2bdb27ffeb662fd910d/500x500-000000-80-0-0.jpg",
  "Arctic Monkeys::Whatever People Say I Am, That's What I'm Not":
    "https://cdn-images.dzcdn.net/images/cover/f7a0a1ca91431861989efe5a22aad557/500x500-000000-80-0-0.jpg",
  "Arctic Monkeys::Favourite Worst Nightmare":
    "https://cdn-images.dzcdn.net/images/cover/d7a4f9f1af8736457de34f28d50ef496/500x500-000000-80-0-0.jpg",
  "Arctic Monkeys::Humbug":
    "https://cdn-images.dzcdn.net/images/cover/13cdeb23547351f3ea543a2f5b4b9a4b/500x500-000000-80-0-0.jpg",
  "Arctic Monkeys::Suck It and See":
    "https://cdn-images.dzcdn.net/images/cover/9751005be2b826746df12c45b761573a/500x500-000000-80-0-0.jpg",
  "Arctic Monkeys::Tranquility Base Hotel & Casino":
    "https://cdn-images.dzcdn.net/images/cover/b223decfaa57910ef709736e49eaf0de/500x500-000000-80-0-0.jpg",
  "Arctic Monkeys::The Car":
    "https://cdn-images.dzcdn.net/images/cover/1f137dac0e31b896d5350742b4365f07/500x500-000000-80-0-0.jpg",
  "Tame Impala::Currents":
    "https://cdn-images.dzcdn.net/images/cover/de5b9b704cd4ec36f8bf49beb3e17ba2/500x500-000000-80-0-0.jpg",
  "Tame Impala::Lonerism":
    "https://cdn-images.dzcdn.net/images/cover/9fe30ce99ef17cb1250bef071f15ccee/500x500-000000-80-0-0.jpg",
  "Tame Impala::Innerspeaker":
    "https://cdn-images.dzcdn.net/images/cover/5bf6a2d836429e215be5f0213882ad1f/500x500-000000-80-0-0.jpg",
  "Tame Impala::The Slow Rush":
    "https://cdn-images.dzcdn.net/images/cover/d8eb61bd4becf79a602a75b69eebde7d/500x500-000000-80-0-0.jpg",
  "Tyler, the Creator::Flower Boy":
    "https://cdn-images.dzcdn.net/images/cover/a7a16b8f63b1ec0e9fbd327619966737/500x500-000000-80-0-0.jpg",
  "Tyler, the Creator::IGOR":
    "https://cdn-images.dzcdn.net/images/cover/041ab5ceb6fb6ebf9512966835be9e1b/500x500-000000-80-0-0.jpg",
  "Tyler, the Creator::Call Me If You Get Lost":
    "https://cdn-images.dzcdn.net/images/cover/2d740784396546039fe626ac2b92877b/500x500-000000-80-0-0.jpg",
  "Tyler, the Creator::Goblin":
    "https://cdn-images.dzcdn.net/images/cover/65d4a36d03918097176d42f8f55900af/500x500-000000-80-0-0.jpg",
  "Tyler, the Creator::Wolf":
    "https://cdn-images.dzcdn.net/images/cover/243cc17e7688cb2f9739120ae4eb9912/500x500-000000-80-0-0.jpg",
  "Tyler, the Creator::Cherry Bomb":
    "https://cdn-images.dzcdn.net/images/cover/502b157c53630785c3f499fd032baf96/500x500-000000-80-0-0.jpg",
  "Tyler, the Creator::Chromakopia":
    "https://cdn-images.dzcdn.net/images/cover/cb415a59a7bc198ec4aab01f02600691/500x500-000000-80-0-0.jpg",
  "Kendrick Lamar::DAMN.":
    "https://cdn-images.dzcdn.net/images/cover/7ce6b8452fae425557067db6e6a1cad5/500x500-000000-80-0-0.jpg",
  "Kendrick Lamar::good kid, m.A.A.d city":
    "https://cdn-images.dzcdn.net/images/cover/beaffccb372735044f4b8252b0cd9c2b/500x500-000000-80-0-0.jpg",
  "Kendrick Lamar::To Pimp a Butterfly":
    "https://cdn-images.dzcdn.net/images/cover/00dd0da365a94b1829302d6b7fec70e6/500x500-000000-80-0-0.jpg",
  "Kendrick Lamar::Mr. Morale & the Big Steppers":
    "https://cdn-images.dzcdn.net/images/cover/412361ce41f0bd2595978dbf0e035ad3/500x500-000000-80-0-0.jpg",
  "Kendrick Lamar::Section.80":
    "https://cdn-images.dzcdn.net/images/cover/85287abdf7a4b2c40c5168c24b2bc978/500x500-000000-80-0-0.jpg",
  "Kendrick Lamar::GNX":
    "https://cdn-images.dzcdn.net/images/cover/82db4c0f8e9412cafb1cd765b076d58c/500x500-000000-80-0-0.jpg",
  "Billie Eilish::When We All Fall Asleep, Where Do We Go?":
    "https://cdn-images.dzcdn.net/images/cover/6630083f454d48eadb6a9b53f035d734/500x500-000000-80-0-0.jpg",
  "Billie Eilish::Happier Than Ever":
    "https://cdn-images.dzcdn.net/images/cover/bb2880548dd3bc71fb97def2eedec130/500x500-000000-80-0-0.jpg",
  "Billie Eilish::Hit Me Hard and Soft":
    "https://cdn-images.dzcdn.net/images/cover/5d284b31cb9ddeb1a0c79aede5a94e1c/500x500-000000-80-0-0.jpg",
  "Billie Eilish::Don't Smile at Me":
    "https://cdn-images.dzcdn.net/images/cover/c6e5ffd676146c447a4a81819c5d29ae/500x500-000000-80-0-0.jpg",
  "Lana Del Rey::Born to Die":
    "https://cdn-images.dzcdn.net/images/cover/4c2c6143c3e83a01ea73517c57d1d138/500x500-000000-80-0-0.jpg",
  "Lana Del Rey::Ultraviolence":
    "https://cdn-images.dzcdn.net/images/cover/b52b26d48c81c5edba0efbdda044b33a/500x500-000000-80-0-0.jpg",
  "Lana Del Rey::Honeymoon":
    "https://cdn-images.dzcdn.net/images/cover/e69dade74de5e1f3f524f1c985696330/500x500-000000-80-0-0.jpg",
  "Lana Del Rey::Lust for Life":
    "https://cdn-images.dzcdn.net/images/cover/7120083b059299f380eb1fe3bca0eefb/500x500-000000-80-0-0.jpg",
  "Lana Del Rey::Norman Fucking Rockwell!":
    "https://cdn-images.dzcdn.net/images/cover/c0f4f022fa51f13e877aae2e758e241d/500x500-000000-80-0-0.jpg",
  "Lana Del Rey::Blue Banisters":
    "https://cdn-images.dzcdn.net/images/cover/c03cb054182a8e33a9588c8755f35d70/500x500-000000-80-0-0.jpg",
  "Lana Del Rey::Did You Know That There's a Tunnel Under Ocean Blvd":
    "https://cdn-images.dzcdn.net/images/cover/0ae53c84250981214bcb7ca39f8c2195/500x500-000000-80-0-0.jpg",
  "The Strokes::Is This It":
    "https://cdn-images.dzcdn.net/images/cover/700f0375d5ac8570f16a2c7eb128303f/500x500-000000-80-0-0.jpg",
  "The Strokes::Room on Fire":
    "https://cdn-images.dzcdn.net/images/cover/3d1246b483aefa9bd0bcd07dfc926be8/500x500-000000-80-0-0.jpg",
  "The Strokes::First Impressions of Earth":
    "https://cdn-images.dzcdn.net/images/cover/26b25d58623f89e163b8e4c4a5ae2ca2/500x500-000000-80-0-0.jpg",
  "The Strokes::Angles":
    "https://cdn-images.dzcdn.net/images/cover/c4cfdc177036a8f9ab8abdc287d185f4/500x500-000000-80-0-0.jpg",
  "The Strokes::Comedown Machine":
    "https://cdn-images.dzcdn.net/images/cover/7f392e337f26190c66eb03f9135c7592/500x500-000000-80-0-0.jpg",
  "The Strokes::The New Abnormal":
    "https://cdn-images.dzcdn.net/images/cover/f8a0a2e1ec12c1026cd03208237cd934/500x500-000000-80-0-0.jpg",
  "MGMT::Oracular Spectacular":
    "https://cdn-images.dzcdn.net/images/cover/d910a6585e4a80f06e6fddce4f6f859d/500x500-000000-80-0-0.jpg",
  "MGMT::Congratulations":
    "https://cdn-images.dzcdn.net/images/cover/45b1228d06903dd42c8150f1c493b0ea/500x500-000000-80-0-0.jpg",
  "MGMT::MGMT":
    "https://cdn-images.dzcdn.net/images/cover/dcfcfc3885110f022bd69469b2c05392/500x500-000000-80-0-0.jpg",
  "MGMT::Little Dark Age":
    "https://cdn-images.dzcdn.net/images/cover/494429a8ec9251591dea6c1f10ade166/500x500-000000-80-0-0.jpg",
  "MGMT::Loss of Life":
    "https://cdn-images.dzcdn.net/images/cover/db776aa36c21952e0e6b97737ca7f778/500x500-000000-80-0-0.jpg",
  "Gorillaz::Gorillaz":
    "https://cdn-images.dzcdn.net/images/cover/f4d581f4b86c869547704d7db9aa2c43/500x500-000000-80-0-0.jpg",
  "Gorillaz::Demon Days":
    "https://cdn-images.dzcdn.net/images/cover/3dc29a565149240729afc08e1f251b46/500x500-000000-80-0-0.jpg",
  "Gorillaz::Plastic Beach":
    "https://cdn-images.dzcdn.net/images/cover/4ddf15e6d4fa3cf61fdc8271cdec4815/500x500-000000-80-0-0.jpg",
  "Gorillaz::Humanz":
    "https://cdn-images.dzcdn.net/images/cover/d98d08e767ce7c3a8a3a83de5d3d1302/500x500-000000-80-0-0.jpg",
  "Gorillaz::The Now Now":
    "https://cdn-images.dzcdn.net/images/cover/8b3147d5b4b94459a54983dfcdeb4516/500x500-000000-80-0-0.jpg",
  "Gorillaz::Song Machine, Season One: Strange Timez":
    "https://cdn-images.dzcdn.net/images/cover/9d66280decadce7bf8deaaf63264c533/500x500-000000-80-0-0.jpg",
  "Gorillaz::Cracker Island":
    "https://cdn-images.dzcdn.net/images/cover/2257be2054a24b9accfc7f2276ceec0f/500x500-000000-80-0-0.jpg",
  "LCD Soundsystem::LCD Soundsystem":
    "https://cdn-images.dzcdn.net/images/cover/ef9c10c7b4c10d2b2b3fcadc4c810415/500x500-000000-80-0-0.jpg",
  "LCD Soundsystem::Sound of Silver":
    "https://cdn-images.dzcdn.net/images/cover/d5aec97cfc1581df6b32a267fe067827/500x500-000000-80-0-0.jpg",
  "LCD Soundsystem::This Is Happening":
    "https://cdn-images.dzcdn.net/images/cover/6d1c8c822d58041dfc7fd0bb5612cdf5/500x500-000000-80-0-0.jpg",
  "LCD Soundsystem::American Dream":
    "https://cdn-images.dzcdn.net/images/cover/466b59cac13e24f83912e0aa6ed3d6e5/500x500-000000-80-0-0.jpg",
  "Phoenix::Wolfgang Amadeus Phoenix":
    "https://cdn-images.dzcdn.net/images/cover/c3bb90a6b2f333c1510a876236bacf0c/500x500-000000-80-0-0.jpg",
  "Phoenix::Bankrupt!":
    "https://cdn-images.dzcdn.net/images/cover/d1d28797b68ec425501394ad6bd37097/500x500-000000-80-0-0.jpg",
  "Phoenix::Ti Amo":
    "https://cdn-images.dzcdn.net/images/cover/dc62909cbb88b0d012ccbbc16de015e6/500x500-000000-80-0-0.jpg",
  "Phoenix::Alpha Zulu":
    "https://cdn-images.dzcdn.net/images/cover/c23cb99c99b75d2148d21f6d60750cf3/500x500-000000-80-0-0.jpg",
  "Phoenix::It's Never Been Like That":
    "https://cdn-images.dzcdn.net/images/cover/8a043d88d2823e897272e3c34be81507/500x500-000000-80-0-0.jpg",
  "Vampire Weekend::Vampire Weekend":
    "https://cdn-images.dzcdn.net/images/cover/6fc963e3e5bd489dd82b0e02c3122792/500x500-000000-80-0-0.jpg",
  "Vampire Weekend::Contra":
    "https://cdn-images.dzcdn.net/images/cover/59327de36493020190874f2725a8ae2d/500x500-000000-80-0-0.jpg",
  "Vampire Weekend::Modern Vampires of the City":
    "https://cdn-images.dzcdn.net/images/cover/470b179cc499f76813311609e4e3b9b9/500x500-000000-80-0-0.jpg",
  "Vampire Weekend::Father of the Bride":
    "https://cdn-images.dzcdn.net/images/cover/aee106a0dc2eba379ae92998e51fe3d3/500x500-000000-80-0-0.jpg",
  "Vampire Weekend::Only God Was Above Us":
    "https://cdn-images.dzcdn.net/images/cover/bd5cf168c66b339b0026d83e030e86dc/500x500-000000-80-0-0.jpg",
  "Arcade Fire::Funeral":
    "https://cdn-images.dzcdn.net/images/cover/d73ebc04554b4ab4a334fa9acaa5f9af/500x500-000000-80-0-0.jpg",
  "Arcade Fire::Neon Bible":
    "https://cdn-images.dzcdn.net/images/cover/9730632d20fbe04a1109fb9ccb850c5d/500x500-000000-80-0-0.jpg",
  "Arcade Fire::The Suburbs":
    "https://cdn-images.dzcdn.net/images/cover/d6764ed9d1f942942fb47ecde23919eb/500x500-000000-80-0-0.jpg",
  "Arcade Fire::Reflektor":
    "https://cdn-images.dzcdn.net/images/cover/0cebf46a58ca97dcf4c722d79e999214/500x500-000000-80-0-0.jpg",
  "Arcade Fire::Everything Now":
    "https://cdn-images.dzcdn.net/images/cover/eb78511c083d8f432f73692047f134a7/500x500-000000-80-0-0.jpg",
  "Arcade Fire::WE":
    "https://cdn-images.dzcdn.net/images/cover/1d3d8b593468d30cb2f2f03da8d82fda/500x500-000000-80-0-0.jpg",
  "Kanye West::The College Dropout":
    "https://cdn-images.dzcdn.net/images/cover/069a5dba671436da9301aad36fc9a983/500x500-000000-80-0-0.jpg",
  "Kanye West::Late Registration":
    "https://cdn-images.dzcdn.net/images/cover/7cbfc94084895e59b5a313a98ab1bd9a/500x500-000000-80-0-0.jpg",
  "Kanye West::Graduation":
    "https://cdn-images.dzcdn.net/images/cover/8c6578a2099561992fb7544e6826f767/500x500-000000-80-0-0.jpg",
  "Kanye West::808s & Heartbreak":
    "https://cdn-images.dzcdn.net/images/cover/2877c6bf4fad750c54dd212fb50a366b/500x500-000000-80-0-0.jpg",
  "Kanye West::My Beautiful Dark Twisted Fantasy":
    "https://cdn-images.dzcdn.net/images/cover/742aba8510ba803bea51d304cf2ca786/500x500-000000-80-0-0.jpg",
  "Kanye West::Yeezus":
    "https://cdn-images.dzcdn.net/images/cover/5a56530f7906bd9786fa47bd0be421b3/500x500-000000-80-0-0.jpg",
  "Kanye West::The Life of Pablo":
    "https://cdn-images.dzcdn.net/images/cover/e055ecc8d01680cda0460017087728be/500x500-000000-80-0-0.jpg",
  "Kanye West::ye":
    "https://cdn-images.dzcdn.net/images/cover/71d34b7f041d43ea821a9afaeab73666/500x500-000000-80-0-0.jpg",
  "Kanye West::Donda":
    "https://cdn-images.dzcdn.net/images/cover/330da8bf0a57b47c2078db2d3761dc5e/500x500-000000-80-0-0.jpg",
  "Frank Ocean::Channel Orange":
    "https://cdn-images.dzcdn.net/images/cover/519400e29d268f449cf00af879e71af6/500x500-000000-80-0-0.jpg",
  "Frank Ocean::Blonde":
    "https://cdn-images.dzcdn.net/images/cover/aa7e6de00b0810f5051aa60b489f58d8/500x500-000000-80-0-0.jpg",
  "Frank Ocean::Endless":
    "https://cdn-images.dzcdn.net/images/cover/80509293340ae18d86f99fca01053034/500x500-000000-80-0-0.jpg",
  "Frank Ocean::Nostalgia, Ultra":
    "https://cdn-images.dzcdn.net/images/cover/70836f2fe73435ec0c0f844d93bde08f/500x500-000000-80-0-0.jpg",
  "Childish Gambino::Because the Internet":
    "https://cdn-images.dzcdn.net/images/cover/a07c38caadefae99abe4047dbcb0c778/500x500-000000-80-0-0.jpg",
  "Childish Gambino::Awaken, My Love!":
    "https://cdn-images.dzcdn.net/images/cover/57ae3715e73c0486616bab8c2d6c6159/500x500-000000-80-0-0.jpg",
  "Childish Gambino::Camp":
    "https://cdn-images.dzcdn.net/images/cover/206697ef5a060454db2e107360543189/500x500-000000-80-0-0.jpg",
  "Childish Gambino::Atavista":
    "https://cdn-images.dzcdn.net/images/cover/787b51915e0c88a0813e2d5d6c337ab4/500x500-000000-80-0-0.jpg",
  "Childish Gambino::Bando Stone & the New World":
    "https://cdn-images.dzcdn.net/images/cover/7207a0bc6ced0ebb635e6035c7173ec1/500x500-000000-80-0-0.jpg",
  "Anderson .Paak::Malibu":
    "https://cdn-images.dzcdn.net/images/cover/dc2fac96693ccae66ea8e0a43069c716/500x500-000000-80-0-0.jpg",
  "Anderson .Paak::Oxnard":
    "https://cdn-images.dzcdn.net/images/cover/48eebe922e8560724795f645e02fa6a3/500x500-000000-80-0-0.jpg",
  "Anderson .Paak::Ventura":
    "https://cdn-images.dzcdn.net/images/cover/cfff75ee48e3a1bfea51894e9f772036/500x500-000000-80-0-0.jpg",
  "Mac DeMarco::2":
    "https://cdn-images.dzcdn.net/images/cover/48dd98d88f1af797d65faf7f3e4beef7/500x500-000000-80-0-0.jpg",
  "Mac DeMarco::Salad Days":
    "https://cdn-images.dzcdn.net/images/cover/96f16ccb3da4d231b72bc5de25a16202/500x500-000000-80-0-0.jpg",
  "Mac DeMarco::Another One":
    "https://cdn-images.dzcdn.net/images/cover/a8cc3d9a142cd0119c42eb1aafc974b9/500x500-000000-80-0-0.jpg",
  "Mac DeMarco::This Old Dog":
    "https://cdn-images.dzcdn.net/images/cover/5e7b8670b572a110d4453e6ac94421d8/500x500-000000-80-0-0.jpg",
  "Mac DeMarco::Here Comes the Cowboy":
    "https://cdn-images.dzcdn.net/images/cover/b4b7dd92a404cd45a556b4066f7b8cbd/500x500-000000-80-0-0.jpg",
  "Mac DeMarco::Five Easy Hot Dogs":
    "https://cdn-images.dzcdn.net/images/cover/a1f36a535fcdd18d85a099b5558f4b51/500x500-000000-80-0-0.jpg",
}

function demoCoverArtForAlbum(artist: string, album: string): string {
  // Strip the synthetic "(Disc N)" suffix so cycled albums reuse the base
  // album's cover URL.
  const baseAlbum = album.replace(/\s*\(Disc\s*\d+\)\s*$/i, "").trim()
  const baked = DEMO_COVERS[`${artist}::${baseAlbum}`]
  if (baked) return baked
  // Fallback for albums not in the curated map (real songs from mockFileSystem
  // with placeholder metadata, future synthesis additions, etc.). picsum is
  // deterministic per seed and CORS-friendly.
  const seed = encodeURIComponent(`${artist}::${album}`)
  return `https://picsum.photos/seed/${seed}/400/400`
}

function buildSyntheticDemoSongs(startId: number, totalBytesFromReal: number): ApiSong[] {
  const bytesRemaining = Math.max(0, DEMO_TOTAL_BYTES_TARGET - totalBytesFromReal)
  const bytesPerTrack = Math.floor(bytesRemaining / DEMO_SYNTHETIC_TRACK_COUNT)
  const synthetic: ApiSong[] = []

  // Walk artist-by-album so every album gets a complete, sequential track list
  // (1..N) instead of slicing every album by the same i%12 cycle.
  let i = 0
  outer: for (const { artist, albums } of DEMO_DISCOGRAPHY) {
    for (const album of albums) {
      const albumKey = `${artist}::${album}`
      const albumHash = hashString(albumKey)
      const tracksPerAlbum = 8 + (albumHash % 7) // 8..14, realistic range
      const titleOffset = albumHash % DEMO_TRACK_TITLES.length
      const yearForAlbum = 1998 + (albumHash % 27) // 1998..2024
      const albumArtUrl = demoCoverArtForAlbum(artist, album)

      for (let t = 0; t < tracksPerAlbum; t++) {
        if (i >= DEMO_SYNTHETIC_TRACK_COUNT) break outer
        const trackNum = t + 1
        const title =
          DEMO_TRACK_TITLES[(titleOffset + t) % DEMO_TRACK_TITLES.length]
        const ext = i % 3 === 0 ? "mp3" : "flac"
        const safeTitle = title.replace(/[/\\]/g, "")
        const cleanFileName = `${String(trackNum).padStart(2, "0")} - ${safeTitle}.${ext}`
        const messyFileName =
          i % 6 === 0
            ? `track_${String(trackNum).padStart(2, "0")}.${ext}`
            : cleanFileName
        const sourcePath = buildMessySourcePath(i, artist, album, messyFileName)
        const destArtist = artist.replace(/[/\\]/g, "").trim() || "Unknown"
        const destAlbum = album.replace(/[/\\]/g, "").trim() || "Unknown Album"
        const destinationPath = `${DESTINATION_ROOT}/${destArtist}/${destAlbum}/${cleanFileName}`
        const fileSizeBytes = bytesPerTrack + (i % 5) * 1024 * 1024
        const synced = demoSyncedLyricsForIndex(i)
        const plain = demoPlainLyricsFromSynced(synced)
        synthetic.push({
          id: startId + i,
          sourcePath,
          destinationPath,
          fileName: cleanFileName,
          extension: `.${ext}`,
          fileSizeBytes,
          artist,
          album,
          title,
          trackNumber: trackNum,
          year: yearForAlbum,
          durationSeconds: 150 + ((albumHash + t * 31) % 240),
          fingerprint: null,
          musicBrainzId: null,
          spotifyId: null,
          enrichmentStatus: i % 5 === 0 ? "pending" : "complete",
          lyricsStatus: "Fetched",
          hasSyncedLyrics: true,
          hasPlainLyrics: true,
          isInstrumental: false,
          syncedLyrics: synced,
          plainLyrics: plain,
          sampleRate: ext === "flac" ? 44100 : 48000,
          bitRate: ext === "flac" ? 1411 : 320,
          albumArt: albumArtUrl,
        })
        i++
      }
    }
  }

  // If the curated discography didn't fill the target track count, loop back
  // through the discography from album 0 again so the library still feels full.
  if (i < DEMO_SYNTHETIC_TRACK_COUNT) {
    let cycle = 1
    while (i < DEMO_SYNTHETIC_TRACK_COUNT) {
      for (const { artist, albums } of DEMO_DISCOGRAPHY) {
        for (const album of albums) {
          if (i >= DEMO_SYNTHETIC_TRACK_COUNT) break
          // Suffix album name on subsequent cycles so the synthetic key stays unique
          // and the grid doesn't accumulate inflated track counts on one album.
          const cycledAlbum = `${album} (Disc ${cycle + 1})`
          const albumKey = `${artist}::${cycledAlbum}`
          const albumHash = hashString(albumKey)
          const tracksPerAlbum = 8 + (albumHash % 7)
          const titleOffset = albumHash % DEMO_TRACK_TITLES.length
          const yearForAlbum = 1998 + (albumHash % 27)
          const albumArtUrl = demoCoverArtForAlbum(artist, cycledAlbum)
          for (let t = 0; t < tracksPerAlbum; t++) {
            if (i >= DEMO_SYNTHETIC_TRACK_COUNT) break
            const trackNum = t + 1
            const title =
              DEMO_TRACK_TITLES[(titleOffset + t) % DEMO_TRACK_TITLES.length]
            const ext = i % 3 === 0 ? "mp3" : "flac"
            const cleanFileName = `${String(trackNum).padStart(2, "0")} - ${title.replace(/[/\\]/g, "")}.${ext}`
            const sourcePath = buildMessySourcePath(i, artist, cycledAlbum, cleanFileName)
            const destinationPath = `${DESTINATION_ROOT}/${artist}/${cycledAlbum}/${cleanFileName}`
            const fileSizeBytes = bytesPerTrack + (i % 5) * 1024 * 1024
            const synced = demoSyncedLyricsForIndex(i)
            const plain = demoPlainLyricsFromSynced(synced)
            synthetic.push({
              id: startId + i,
              sourcePath,
              destinationPath,
              fileName: cleanFileName,
              extension: `.${ext}`,
              fileSizeBytes,
              artist,
              album: cycledAlbum,
              title,
              trackNumber: trackNum,
              year: yearForAlbum,
              durationSeconds: 150 + ((albumHash + t * 31) % 240),
              fingerprint: null,
              musicBrainzId: null,
              spotifyId: null,
              enrichmentStatus: i % 5 === 0 ? "pending" : "complete",
              lyricsStatus: "Fetched",
              hasSyncedLyrics: true,
              hasPlainLyrics: true,
              isInstrumental: false,
              syncedLyrics: synced,
              plainLyrics: plain,
              sampleRate: ext === "flac" ? 44100 : 48000,
              bitRate: ext === "flac" ? 1411 : 320,
              albumArt: albumArtUrl,
            })
            i++
          }
        }
      }
      cycle++
    }
  }

  return synthetic
}

export type NormalizedEnrichmentStatus =
  | "pending"
  | "processing"
  | "complete"
  | "failed"
  | "needsreview"

export function mapEnrichmentStatus(status?: string | number | null): NormalizedEnrichmentStatus {
  if (typeof status === "number") {
    switch (status) {
      case 1:
        return "complete"
      case 2:
        return "needsreview"
      case 3:
        return "failed"
      default:
        return "pending"
    }
  }

  if (typeof status === "string") {
    const normalized = status.toLowerCase()
    if (normalized === "failed") return "failed"
    if (normalized === "matched" || normalized === "complete") return "complete"
    if (normalized === "needsreview") return "needsreview"
    if (normalized === "running" || normalized === "processing") {
      return "processing"
    }
  }

  return "pending"
}

function createFolder(parent: FileItem, folderName: string): FileItem {
  const parentPath = parent.path === "/" ? "" : parent.path
  return {
    id: `folder:${parentPath}/${folderName}`,
    name: folderName,
    type: "folder",
    path: `${parentPath}/${folderName}`.replace("//", "/"),
    parentId: parent.id,
    children: [],
  }
}

function getOrCreateChildFolder(parent: FileItem, folderName: string): FileItem {
  const existingFolder = parent.children?.find(
    (child) => child.type === "folder" && child.name === folderName
  )
  if (existingFolder) {
    return existingFolder
  }

  const folder = createFolder(parent, folderName)
  parent.children = [...(parent.children ?? []), folder]
  return folder
}

function normalizeSourcePath(sourcePath?: string | null): string {
  const rawPath = (sourcePath ?? "").trim()
  if (!rawPath) return ""

  const slashNormalized = rawPath.replace(/\\/g, "/").replace(/\/+/g, "/")
  const withoutWindowsDrive = slashNormalized.replace(/^[a-zA-Z]:\//, "/")
  return withoutWindowsDrive.replace(/\/+$/, "")
}

function isFileNameSegment(segment: string): boolean {
  return /\.[^/.]+$/.test(segment)
}

function getPathSegments(rawPath?: string | null): string[] {
  return normalizeSourcePath(rawPath).split("/").filter(Boolean)
}

function getFolderSegmentsFromPath(rawPath?: string | null): string[] {
  const segments = getPathSegments(rawPath)
  if (segments.length === 0) return []
  return isFileNameSegment(segments[segments.length - 1] ?? "") ? segments.slice(0, -1) : segments
}

function commonPrefixSegments(paths: string[][]): string[] {
  if (paths.length === 0) return []

  const first = paths[0]
  let end = first.length
  for (let i = 1; i < paths.length; i++) {
    const current = paths[i]
    let j = 0
    while (j < end && j < current.length && first[j] === current[j]) {
      j++
    }
    end = j
    if (end === 0) break
  }

  return first.slice(0, end)
}

function countRemovablePrefixSegments(sharedPrefix: string[]): number {
  let count = 0
  for (const segment of sharedPrefix) {
    if (/^[a-zA-Z]:$/.test(segment) || INFRASTRUCTURE_PREFIX_SEGMENT.test(segment)) {
      count++
      continue
    }
    break
  }
  return count
}

function countRemovablePrefixSegmentsForMode(
  mode: LibraryPathMode,
  sharedPrefix: string[],
  allFolderSegments: string[][]
): number {
  if (mode === "destination") {
    if (allFolderSegments.length === 0) return 0
    const shortestPathLength = Math.min(...allFolderSegments.map((segments) => segments.length))
    const maxRemovable = Math.max(0, shortestPathLength - 1)
    return Math.min(sharedPrefix.length, maxRemovable)
  }

  return countRemovablePrefixSegments(sharedPrefix)
}

function inferPathParts(song: ApiSong): {
  folderSegments: string[]
  fileName: string
  normalizedFilePath: string | null
  normalizedPathSegments: string[]
} {
  const normalizedPath = normalizeSourcePath(song.sourcePath)
  const pathSegments = normalizedPath.split("/").filter(Boolean)
  const lastSegment = pathSegments[pathSegments.length - 1] ?? ""
  const hasFileNameInPath = /\.[^/.]+$/.test(lastSegment)

  const fileNameFromApi = song.fileName?.trim()
  const inferredFromPath = hasFileNameInPath ? lastSegment : ""
  const fileName = fileNameFromApi || inferredFromPath || `track-${song.id}`

  const folderSegments = hasFileNameInPath ? pathSegments.slice(0, -1) : pathSegments
  const normalizedFilePath = hasFileNameInPath ? `/${pathSegments.join("/")}` : null

  return { folderSegments, fileName, normalizedFilePath, normalizedPathSegments: pathSegments }
}

function getSongsForMode(songs: ApiSong[], mode: LibraryPathMode): ApiSong[] {
  if (mode === "source") return songs
  return songs.filter((song) => Boolean(song.destinationPath?.trim()))
}

export function buildFileSystemFromSongs(
  songs: ApiSong[],
  mode: LibraryPathMode = "source"
): FileItem[] {
  const songsForMode = getSongsForMode(songs, mode)
  const allFolderSegments = songsForMode.map((song) =>
    getFolderSegmentsFromPath(mode === "destination" ? song.destinationPath : song.sourcePath)
  )
  const pathRootPrefixSegments = commonPrefixSegments(allFolderSegments)
  const removablePrefixCount = countRemovablePrefixSegmentsForMode(
    mode,
    pathRootPrefixSegments,
    allFolderSegments
  )

  const root: FileItem = {
    id: "root",
    name: mode === "destination" ? "Destination Library" : "Source Library",
    type: "folder",
    path: "/",
    parentId: null,
    children: [],
  }

  for (const song of songsForMode) {
    const selectedPath = mode === "destination" ? song.destinationPath : song.sourcePath
    const { folderSegments, fileName, normalizedPathSegments } = inferPathParts({
      ...song,
      sourcePath: selectedPath ?? "",
    })
    const relativeFolderSegments = folderSegments.slice(removablePrefixCount)
    const relativePathSegments = normalizedPathSegments.slice(removablePrefixCount)
    const normalizedFilePath = relativePathSegments.length > 0 ? `/${relativePathSegments.join("/")}` : null

    let currentFolder = root
    for (const segment of relativeFolderSegments) {
      currentFolder = getOrCreateChildFolder(currentFolder, segment)
    }

    const metadataTitle = song.title?.trim() || fileName.replace(/\.[^.]+$/, "")
    const metadataArtist = song.artist?.trim() || "Unknown Artist"
    const metadataAlbum = song.album?.trim() || "Unknown Album"

    const audioFile: FileItem = {
      id: `song:${song.id}`,
      name: fileName,
      type: "audio",
      path: normalizedFilePath || `${currentFolder.path}/${fileName}`,
      parentId: currentFolder.id,
      metadata: {
        title: metadataTitle,
        artist: metadataArtist,
        album: metadataAlbum,
        year: song.year ?? 0,
        genre: "Unknown",
        duration: song.durationSeconds ?? 0,
        bitrate: song.bitRate ?? 0,
        sampleRate: song.sampleRate ?? 0,
        format: (song.extension ?? "Unknown").replace(/^\./, "").toUpperCase(),
        fileSize: song.fileSizeBytes ?? 0,
        fingerprint: song.fingerprint ?? undefined,
        enrichmentStatus: mapEnrichmentStatus(song.enrichmentStatus),
        matchedBy: song.matchedBy ?? undefined,
        lyricsStatus: (song.lyricsStatus ?? "NotFetched") as import("$lib/types").LyricsStatus,
        hasSyncedLyrics: song.hasSyncedLyrics ?? false,
        hasPlainLyrics: song.hasPlainLyrics ?? false,
        isInstrumental: song.isInstrumental ?? undefined,
        syncedLyrics: song.syncedLyrics ?? undefined,
        plainLyrics: song.plainLyrics ?? undefined,
        lyrics: song.syncedLyrics ?? song.plainLyrics ?? undefined,
        sources: {
          musicbrainz: Boolean(song.musicBrainzId),
          spotify: Boolean(song.spotifyId),
        },
        sourceIds: {
          musicBrainzId: song.musicBrainzId ?? undefined,
          musicBrainzReleaseId: song.musicBrainzReleaseId ?? undefined,
          spotifyId: song.spotifyId ?? undefined,
          acoustIdTrackId: song.acoustIdTrackId ?? undefined,
          lrclibId: song.lrclibId ?? undefined,
        },
      },
    }

    currentFolder.children = [...(currentFolder.children ?? []), audioFile]
  }

  return [root]
}

// ── Album grouping ────────────────────────────────────────────────────────────

const UNKNOWN_ALBUM = "Unknown Album"
const UNKNOWN_ARTIST = "Unknown Artist"

/** Aggregated view of all songs sharing an `(albumArtist, album)` pair. */
export interface AlbumSummary {
  /** Stable key — `${artistLower}::${titleLower}`. Matches the `?album=` URL param the existing UI emits. */
  key: string
  title: string
  artist: string
  year: number | null
  trackCount: number
  /** Sum of durationSeconds across known tracks. */
  durationSeconds: number
  /** Sum of fileSizeBytes across known tracks. */
  byteSize: number
  /** First non-null genre encountered; null otherwise. */
  genre: string | null
  /** First non-null musicBrainzReleaseId encountered. */
  musicBrainzReleaseId: string | null
  /** First non-null albumArt URL encountered. */
  coverUrl: string | null
  /** Songs ordered by track number then title. */
  songs: ApiSong[]
}

function nonEmpty(value: string | null | undefined): string | null {
  const trimmed = (value ?? "").trim()
  return trimmed.length > 0 ? trimmed : null
}

/**
 * Group raw songs into album summaries used by Gallery / AlbumPage.
 * Same key shape as the previous AlbumGridView (artistLower::titleLower) so
 * existing `?album=` URLs keep working across the migration.
 */
export function buildAlbumsFromSongs(songs: ApiSong[]): AlbumSummary[] {
  const map = new Map<string, AlbumSummary>()
  for (const song of songs) {
    const title = nonEmpty(song.album) ?? UNKNOWN_ALBUM
    const artist = nonEmpty(song.albumArtist) ?? nonEmpty(song.artist) ?? UNKNOWN_ARTIST
    const key = `${artist.toLowerCase()}::${title.toLowerCase()}`
    let entry = map.get(key)
    if (!entry) {
      entry = {
        key,
        title,
        artist,
        year: song.year ?? null,
        trackCount: 0,
        durationSeconds: 0,
        byteSize: 0,
        genre: null,
        musicBrainzReleaseId: null,
        coverUrl: null,
        songs: [],
      }
      map.set(key, entry)
    }
    entry.trackCount += 1
    entry.durationSeconds += song.durationSeconds ?? 0
    entry.byteSize += song.fileSizeBytes ?? 0
    if (song.year && (!entry.year || song.year < entry.year)) entry.year = song.year
    if (!entry.musicBrainzReleaseId && song.musicBrainzReleaseId) {
      entry.musicBrainzReleaseId = song.musicBrainzReleaseId
    }
    if (!entry.coverUrl && song.albumArt) entry.coverUrl = song.albumArt
    entry.songs.push(song)
  }
  for (const album of map.values()) {
    album.songs.sort((a, b) => {
      const na = a.trackNumber ?? Number.POSITIVE_INFINITY
      const nb = b.trackNumber ?? Number.POSITIVE_INFINITY
      if (na !== nb) return na - nb
      const ta = (a.title ?? a.fileName).toLocaleLowerCase()
      const tb = (b.title ?? b.fileName).toLocaleLowerCase()
      return ta.localeCompare(tb)
    })
  }
  return Array.from(map.values()).sort((a, b) => {
    const artistCmp = a.artist.localeCompare(b.artist)
    if (artistCmp !== 0) return artistCmp
    return a.title.localeCompare(b.title)
  })
}

export async function fetchStats(): Promise<ApiStats> {
  if (isDemoMode) {
    const demoSongs = buildDemoSongs()
    return {
      tracks: {
        total: mockImportJob.tracksDiscovered || demoSongs.length,
        deleted: 0,
      },
      storage: {
        totalBytes: demoSongs.reduce((sum, song) => sum + song.fileSizeBytes, 0),
      },
    }
  }

  return requestJson<ApiStats>("/stats")
}

export async function fetchOverview(): Promise<ApiOverview> {
  if (isDemoMode) {
    const demoSongs = buildDemoSongs()
    const copiedCount = demoSongs.filter(
      (s) => s.destinationPath && s.artist && s.artist !== "Unknown Artist"
    ).length
    const fingerprintedCount =
      demoSongs.filter((s) => Boolean(s.fingerprint)).length ||
      Math.floor(demoSongs.length * 0.9)
    const enrichedCount =
      demoSongs.filter((s) => s.enrichmentStatus === "complete").length ||
      Math.floor(demoSongs.length * 0.76)
    return {
      sourcePath: mockImportJob.sourcePath,
      destinationPath: mockImportJob.destinationPath,
      scan: null,
      job: {
        status: "running",
        startedAt: new Date(Date.now() - 45 * 60 * 1000).toISOString(),
        tracksDiscovered: demoSongs.length,
        tracksProcessed: demoSongs.length,
        tracksFingerprinted: fingerprintedCount,
        tracksEnriched: enrichedCount,
        tracksBuildEligible: Math.min(enrichedCount, Math.max(copiedCount, Math.floor(demoSongs.length * 0.6))),
        tracksCopied: copiedCount,
        tracksReview: mockImportJob.tracksReview,
        tracksFailed: mockImportJob.tracksFailed,
      },
      recentActivity: (await import("$lib/mock-data")).mockRecentActivity.map((a) => ({
        id: a.id,
        type: a.type,
        track: a.track,
        artist: a.artist,
        time: a.time,
      })),
    }
  }

  return requestJson<ApiOverview>("/overview")
}

export async function fetchSongs(includeDeleted = false): Promise<ApiSong[]> {
  if (isDemoMode) {
    const demoSongs = buildDemoSongs()
    if (includeDeleted) return demoSongs
    return demoSongs
  }

  const result = await requestJson<SongsResponse>(`/songs?includeDeleted=${includeDeleted}`)
  return result.songs ?? []
}

export async function startScan(): Promise<{ scanId: string }> {
  if (isDemoMode) {
    return { scanId: `demo-scan-${Date.now()}` }
  }

  return requestJson<{ scanId: string }>("/scan", { method: "POST" })
}

// ── Enrichment controller types ───────────────────────────────────────────────

export interface StepSnapshot {
  status: string
  isPaused: boolean
}

/** Real-time progress snapshot emitted by the SSE stream and the status endpoint. */
export interface ProgressSnapshot {
  status: string
  jobId: string | null
  startedAt: string | null
  completedAt: string | null
  isComplete: boolean
  discovered: number
  scanned: number
  fingerprinted: number
  enriched: number
  built: number
  failed: number
  scan: StepSnapshot
  fingerprint: StepSnapshot
  enrich: StepSnapshot
  build: StepSnapshot
}

export interface JobStatusResponse {
  progress: ProgressSnapshot
}

export type EnrichmentTriggerResult =
  | { ok: true; jobId: string }
  | { ok: false; status: number; message: string }

// ── Enrichment controller API calls ──────────────────────────────────────────

async function triggerEnrichmentJob(path: string): Promise<EnrichmentTriggerResult> {
  const response = await fetch(`${API_PREFIX}${path}`, {
    method: "POST",
    cache: "no-store",
  })
  const body = await response.json().catch(() => ({})) as Record<string, string>
  if (response.ok) {
    return { ok: true, jobId: body.jobId ?? "" }
  }
  return {
    ok: false,
    status: response.status,
    message: body.message ?? `Request failed: ${response.status}`,
  }
}

export async function triggerEnrichmentScan(): Promise<EnrichmentTriggerResult> {
  if (isDemoMode) return { ok: true, jobId: `demo-scan-${Date.now()}` }
  return triggerEnrichmentJob("/api/enrichment/scan")
}

export async function triggerFingerprint(): Promise<EnrichmentTriggerResult> {
  if (isDemoMode) return { ok: true, jobId: `demo-fp-${Date.now()}` }
  return triggerEnrichmentJob("/api/enrichment/fingerprint")
}

export async function triggerEnrich(): Promise<EnrichmentTriggerResult> {
  if (isDemoMode) return { ok: true, jobId: `demo-enrich-${Date.now()}` }
  return triggerEnrichmentJob("/api/enrichment/enrich")
}

export async function triggerBuild(): Promise<EnrichmentTriggerResult> {
  if (isDemoMode) return { ok: true, jobId: `demo-build-${Date.now()}` }
  return triggerEnrichmentJob("/api/enrichment/build")
}

export async function cancelJob(): Promise<{ message: string }> {
  if (isDemoMode) return { message: "No job is currently running." }
  return requestJson<{ message: string }>("/api/enrichment/cancel", { method: "POST" })
}

export type PurgeStatus = "idle" | "running" | "completed" | "failed"
export type PurgeMode = "post-fingerprint" | "all"

export interface PurgeSnapshot {
  status: PurgeStatus
  mode: PurgeMode | null
  jobId: string | null
  startedAt: string | null
  completedAt: string | null
  songsTotal: number
  songsProcessed: number
  filesTotal: number
  filesDeleted: number
  filesFailed: number
  spotifyMatchesCleared: number
  error: string | null
}

export type PurgeStartResult =
  | { ok: true; jobId: string; mode: PurgeMode }
  | { ok: false; status: number; message: string }

async function startPurge(path: string, mode: PurgeMode): Promise<PurgeStartResult> {
  const response = await fetch(`${API_PREFIX}${path}`, {
    method: "POST",
    cache: "no-store",
  })
  const body = (await response.json().catch(() => ({}))) as Record<string, unknown>
  if (response.status === 202) {
    return { ok: true, jobId: String(body.jobId ?? ""), mode }
  }
  return {
    ok: false,
    status: response.status,
    message: (body.message as string) ?? `Request failed: ${response.status}`,
  }
}

export async function purgePostFingerprint(): Promise<PurgeStartResult> {
  if (isDemoMode) {
    return { ok: true, jobId: `demo-purge-${Date.now()}`, mode: "post-fingerprint" }
  }
  return startPurge("/api/enrichment/purge-post-fingerprint", "post-fingerprint")
}

export async function purgeAll(): Promise<PurgeStartResult> {
  if (isDemoMode) {
    return { ok: true, jobId: `demo-purge-${Date.now()}`, mode: "all" }
  }
  return startPurge("/api/enrichment/purge-all", "all")
}

function toPurgeSnapshot(body: Record<string, unknown>): PurgeSnapshot {
  return {
    status: (body.status as PurgeStatus) ?? "idle",
    mode: (body.mode as PurgeMode | null) ?? null,
    jobId: (body.jobId as string | null) ?? null,
    startedAt: (body.startedAt as string | null) ?? null,
    completedAt: (body.completedAt as string | null) ?? null,
    songsTotal: Number(body.songsTotal ?? 0),
    songsProcessed: Number(body.songsProcessed ?? 0),
    filesTotal: Number(body.filesTotal ?? 0),
    filesDeleted: Number(body.filesDeleted ?? 0),
    filesFailed: Number(body.filesFailed ?? 0),
    spotifyMatchesCleared: Number(body.spotifyMatchesCleared ?? 0),
    error: (body.error as string | null) ?? null,
  }
}

export async function fetchPurgeStatus(): Promise<PurgeSnapshot> {
  if (isDemoMode) {
    return toPurgeSnapshot({ status: "idle" })
  }
  const body = await requestJson<Record<string, unknown>>("/api/enrichment/purge-status")
  return toPurgeSnapshot(body)
}

export async function pauseStep(step: string): Promise<{ message: string }> {
  if (isDemoMode) return { message: `${step} paused.` }
  return requestJson<{ message: string }>(`/api/enrichment/pause?step=${step}`, { method: "POST" })
}

export async function resumeStep(step: string): Promise<{ message: string }> {
  if (isDemoMode) return { message: `${step} resumed.` }
  return requestJson<{ message: string }>(`/api/enrichment/resume?step=${step}`, { method: "POST" })
}

export async function fetchJobStatus(): Promise<JobStatusResponse> {
  return requestJson<JobStatusResponse>("/api/enrichment/status")
}

/**
 * Opens an SSE connection to `/api/enrichment/progress`.
 * Calls `onSnapshot` for every event, and `onClose` when the server closes
 * the stream (job completed) or a connection error occurs.
 *
 * Returns a cleanup function that closes the EventSource.
 */
export function openProgressStream(
  onSnapshot: (snapshot: ProgressSnapshot) => void,
  onClose?: () => void
): () => void {
  const es = new EventSource(`${API_PREFIX}/api/enrichment/progress`)

  es.onmessage = (event) => {
    try {
      onSnapshot(JSON.parse(event.data as string) as ProgressSnapshot)
    } catch {
      // Ignore parse errors
    }
  }

  es.onerror = () => {
    es.close()
    onClose?.()
  }

  return () => es.close()
}

export interface ResetEnrichmentResponse {
  id: number
  fileName: string
  enrichmentStatus: number
  libraryBuildStatus: number
  restoredOriginalMetadata: boolean
  message: string
}

export async function resetSongEnrichment(
  songId: number,
  restoreOriginalMetadata = true
): Promise<ResetEnrichmentResponse> {
  return requestJson<ResetEnrichmentResponse>(
    `/songs/${songId}/reset-enrichment?restoreOriginalMetadata=${restoreOriginalMetadata}`,
    { method: "POST" }
  )
}

export interface TrackLyricsResponse {
  id: number
  lyricsStatus: string
  isInstrumental?: boolean | null
  synced?: string | null
  plain?: string | null
}

export async function fetchTrackLyrics(trackId: number): Promise<TrackLyricsResponse> {
  if (isDemoMode) {
    const demoSongs = buildDemoSongs()
    const song = demoSongs.find((s) => s.id === trackId)
    if (song && (song.syncedLyrics ?? song.plainLyrics)) {
      return {
        id: trackId,
        lyricsStatus: song.lyricsStatus ?? "Fetched",
        isInstrumental: song.isInstrumental ?? undefined,
        synced: song.syncedLyrics ?? null,
        plain: song.plainLyrics ?? null,
      }
    }
    return { id: trackId, lyricsStatus: "NotFound", synced: null, plain: null }
  }
  return requestJson<TrackLyricsResponse>(`/api/tracks/${trackId}/lyrics`)
}

export function getSongStreamUrl(songId: number): string {
  if (isDemoMode) return "/demo-audio.mp3"
  return `${API_PREFIX}/songs/${songId}/stream`
}

export function parseSongId(fileItemId: string): number | null {
  if (!fileItemId.startsWith("song:")) return null
  const parsed = Number(fileItemId.slice(5))
  return Number.isFinite(parsed) ? parsed : null
}

// ── Track review API ──────────────────────────────────────────────────────────

export interface ManualReviewRequest {
  decision: "approve" | "reject"
  rejectReason?: string
  artist?: string
  albumArtist?: string
  album?: string
  title?: string
  year?: number
  trackNumber?: number
}

export interface ManualReviewResponse {
  id: number
  fileName: string
  decision: string
  enrichmentStatus: number
  libraryBuildStatus: number
  artist?: string | null
  album?: string | null
  title?: string | null
  year?: number | null
}

export async function submitManualReview(
  songId: number,
  request: ManualReviewRequest
): Promise<ManualReviewResponse> {
  return requestJson<ManualReviewResponse>(`/songs/${songId}/manual-review`, {
    method: "PATCH",
    body: JSON.stringify(request),
  })
}

export interface BulkApproveResponse {
  minConfidence: number
  approvedCount: number
  approvedIds: number[]
}

export async function bulkApprove(
  minConfidence = 0.75
): Promise<BulkApproveResponse> {
  return requestJson<BulkApproveResponse>("/songs/bulk-approve", {
    method: "POST",
    body: JSON.stringify({ minConfidence }),
  })
}

export interface SoftDeleteResponse {
  id: number
  fileName: string
  deletedAtUtc: string
  message: string
}

export async function softDeleteSong(songId: number): Promise<SoftDeleteResponse> {
  return requestJson<SoftDeleteResponse>(`/songs/${songId}`, {
    method: "DELETE",
  })
}

// ── Spotify API ───────────────────────────────────────────────────────────────

export interface SpotifyStatusResponse {
  connected: boolean
  connectedAt?: string | null
  hasCredentials: boolean
  tokenExpired: boolean
}

export interface SpotifyConnectResponse {
  authorizationUrl: string
  state: string
}

export interface SpotifyCredentialsResponse {
  clientId?: string | null
  hasClientSecret: boolean
}

export type SpotifyLibraryMatchStatus = "InLibrary" | "PossibleMatch" | "NotInLibrary"

export interface SpotifyLibraryMatchInfo {
  matchStatus: SpotifyLibraryMatchStatus
  matchedSongId: number | null
  matchConfidence: number | null
  matchedTitle?: string | null
  matchedArtist?: string | null
  matchedEnrichmentStatus?: string | null
}

export interface SpotifyApiTrack {
  spotifyId: string
  title: string
  artist: string
  album: string
  albumArt?: string | null
  durationMs: number
  addedAt: string
  libraryMatch?: SpotifyLibraryMatchInfo | null
}

export interface SpotifyLikedSongsApiResponse {
  total: number
  offset: number
  limit: number
  items: SpotifyApiTrack[]
}

export interface SpotifyApiPlaylist {
  spotifyId: string
  name: string
  description?: string | null
  imageUrl?: string | null
  trackCount: number
  ownerName?: string | null
}

export interface SpotifyPlaylistsApiResponse {
  items: SpotifyApiPlaylist[]
}

export interface SpotifyPlaylistTracksApiResponse {
  total: number
  offset: number
  limit: number
  items: SpotifyApiTrack[]
}

export async function fetchSpotifyStatus(): Promise<SpotifyStatusResponse> {
  if (isDemoMode) return getDemoSpotifyStatus()
  return requestJson<SpotifyStatusResponse>("/api/spotify/status")
}

export async function fetchSpotifyConnectUrl(): Promise<SpotifyConnectResponse> {
  if (isDemoMode) {
    throw new Error("Spotify login is not available in demo mode. Data shown is sample content only.")
  }
  return requestJson<SpotifyConnectResponse>("/api/spotify/connect")
}

export async function disconnectSpotify(): Promise<{ message: string }> {
  if (isDemoMode) return getDemoSpotifyDisconnectMessage()
  return requestJson<{ message: string }>("/api/spotify/disconnect", { method: "DELETE" })
}

export async function fetchSpotifyCredentials(): Promise<SpotifyCredentialsResponse> {
  if (isDemoMode) return getDemoSpotifyCredentials()
  return requestJson<SpotifyCredentialsResponse>("/api/spotify/credentials")
}

export async function saveSpotifyCredentials(clientId: string, clientSecret: string): Promise<{ message: string }> {
  if (isDemoMode) return getDemoSpotifySaveCredentialsMessage()
  return requestJson<{ message: string }>("/api/spotify/credentials", {
    method: "PUT",
    body: JSON.stringify({ clientId, clientSecret }),
  })
}

export async function fetchSpotifyLikedSongs(offset = 0, limit = 50): Promise<SpotifyLikedSongsApiResponse> {
  if (isDemoMode) return getDemoSpotifyLikedSongs(offset, limit)
  return requestJson<SpotifyLikedSongsApiResponse>(`/api/spotify/liked-songs?offset=${offset}&limit=${limit}`)
}

export async function fetchSpotifyPlaylists(): Promise<SpotifyPlaylistsApiResponse> {
  if (isDemoMode) return getDemoSpotifyPlaylists()
  return requestJson<SpotifyPlaylistsApiResponse>("/api/spotify/playlists")
}

export async function fetchSpotifyPlaylistTracks(playlistId: string, offset = 0, limit = 50): Promise<SpotifyPlaylistTracksApiResponse> {
  if (isDemoMode) return getDemoSpotifyPlaylistTracks(playlistId, offset, limit)
  return requestJson<SpotifyPlaylistTracksApiResponse>(
    `/api/spotify/playlists/${playlistId}/tracks?offset=${offset}&limit=${limit}`
  )
}

export async function fetchReviewTracks(): Promise<ApiSong[]> {
  if (isDemoMode) {
    const demoSongs = buildDemoSongs()
    return demoSongs.filter(
      (s) =>
        mapEnrichmentStatus(s.enrichmentStatus) === "needsreview"
    )
  }

  const result = await requestJson<SongsResponse>(
    "/songs?enrichmentStatus=needsreview"
  )
  return result.songs ?? []
}

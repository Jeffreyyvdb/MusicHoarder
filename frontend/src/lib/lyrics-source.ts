import { lrclibWebUrl } from "#lib/lrclib-url.ts"

export type LyricsOrigin = "lrclib" | "genius"

const GENIUS_ORIGIN = "https://genius.com"

/** The API sends the enum name ("Lrclib" | "Genius"); anything else, or nothing, reads as LRCLIB. */
export function parseLyricsOrigin(raw: string | null | undefined): LyricsOrigin {
  return raw?.toLowerCase() === "genius" ? "genius" : "lrclib"
}

export function lyricsSourceLabel(origin: LyricsOrigin): string {
  return origin === "genius" ? "Genius" : "LRCLIB"
}

/**
 * Where to read the stored lyrics at their source. Genius's `/songs/{id}` redirects to the song's page;
 * without an id the link falls back to LRCLIB's search.
 */
export function lyricsSourceUrl(
  origin: LyricsOrigin,
  geniusSongId: number | null | undefined,
  artist: string | null | undefined,
  title: string | null | undefined
): string {
  if (origin === "genius" && geniusSongId) return `${GENIUS_ORIGIN}/songs/${geniusSongId}`
  return lrclibWebUrl(artist, title)
}

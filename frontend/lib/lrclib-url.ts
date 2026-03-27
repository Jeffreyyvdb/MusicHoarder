const LRCLIB_ORIGIN = "https://lrclib.net"

/**
 * LRCLIB’s web UI uses path-based search: /search/{encodeURIComponent("Artist Title")}.
 */
export function lrclibWebSearchUrl(
  artist: string | null | undefined,
  title: string | null | undefined
): string | undefined {
  const a = artist?.trim() ?? ""
  const t = title?.trim() ?? ""
  if (!a || !t) return undefined
  return `${LRCLIB_ORIGIN}/search/${encodeURIComponent(`${a} ${t}`)}`
}

export function lrclibWebUrl(
  artist: string | null | undefined,
  title: string | null | undefined
): string {
  return lrclibWebSearchUrl(artist, title) ?? LRCLIB_ORIGIN
}

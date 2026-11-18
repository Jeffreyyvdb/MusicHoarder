import { describe, expect, it } from "vitest"
import { lyricsSourceLabel, lyricsSourceUrl, parseLyricsOrigin } from "#lib/lyrics-source.ts"

describe("lyrics source", () => {
  it("reads the API's enum name, defaulting to LRCLIB", () => {
    expect(parseLyricsOrigin("Genius")).toBe("genius")
    expect(parseLyricsOrigin("Lrclib")).toBe("lrclib")
    expect(parseLyricsOrigin(undefined)).toBe("lrclib")
    expect(parseLyricsOrigin("something else")).toBe("lrclib")
  })

  it("names the service the lyrics came from", () => {
    expect(lyricsSourceLabel("lrclib")).toBe("LRCLIB")
    expect(lyricsSourceLabel("genius")).toBe("Genius")
  })

  it("links a Genius origin to its song page", () => {
    expect(lyricsSourceUrl("genius", 14584110, "JoeyAK", "MANNSCHAFT")).toBe(
      "https://genius.com/songs/14584110"
    )
  })

  it("links an LRCLIB origin to the LRCLIB search", () => {
    expect(lyricsSourceUrl("lrclib", null, "JoeyAK", "MANNSCHAFT")).toBe(
      "https://lrclib.net/search/JoeyAK%20MANNSCHAFT"
    )
  })

  it("falls back to LRCLIB when a Genius origin has no id", () => {
    expect(lyricsSourceUrl("genius", null, "A", "B")).toBe("https://lrclib.net/search/A%20B")
  })
})

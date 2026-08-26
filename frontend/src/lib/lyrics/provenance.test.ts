import { describe, expect, it } from 'vitest'
import { computeLyricsProvenance } from './provenance'

/**
 * The client-side copy of the API's provenance rule. It exists because the track panel switches
 * between the LRCLIB and AI versions locally, so these cases mirror the ones pinned server-side in
 * LyricsProvenanceTests — if the two drift, the badge starts lying about who wrote the words.
 */
describe('computeLyricsProvenance', () => {
  it('makes no AI claim about untouched LRCLIB lyrics', () => {
    expect(
      computeLyricsProvenance({
        showingTranscription: false,
        alignedToReference: false,
        hasSyncedLyrics: true
      })
    ).toBe('Human')
  })

  it('calls a transcription aligned to the official lyrics "AI Enhanced"', () => {
    expect(
      computeLyricsProvenance({
        showingTranscription: true,
        alignedToReference: true,
        hasSyncedLyrics: true
      })
    ).toBe('AiEnhanced')
  })

  it('calls a transcription it could not align "AI Generated"', () => {
    // The words here are the model's guess, not anybody's lyric sheet — the stronger disclosure.
    expect(
      computeLyricsProvenance({
        showingTranscription: true,
        alignedToReference: false,
        hasSyncedLyrics: false
      })
    ).toBe('AiGenerated')
  })

  it('calls human lyrics re-timed by the probe "AI Enhanced"', () => {
    expect(
      computeLyricsProvenance({
        showingTranscription: false,
        alignedToReference: false,
        hasSyncedLyrics: true,
        syncOffsetMs: 15000
      })
    ).toBe('AiEnhanced')
  })

  it('does not claim a repair for a song that has no synced lyrics to repair', () => {
    expect(
      computeLyricsProvenance({
        showingTranscription: false,
        alignedToReference: false,
        hasSyncedLyrics: false,
        syncOffsetMs: 15000
      })
    ).toBe('Human')
  })

  it('treats a zero offset as a real repair, not as absent', () => {
    // 0 is falsy in JS; reading it as "no offset" would silently drop the label on a song whose
    // measured drift rounded to nothing.
    expect(
      computeLyricsProvenance({
        showingTranscription: false,
        alignedToReference: false,
        hasSyncedLyrics: true,
        syncOffsetMs: 0
      })
    ).toBe('AiEnhanced')
  })
})

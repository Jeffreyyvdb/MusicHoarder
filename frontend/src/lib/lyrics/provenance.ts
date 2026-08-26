import type { LyricsProvenance } from '$lib/types'

/**
 * How much of the lyrics on screen came from an AI.
 *
 * A port of `SongMetadata.ComputeLyricsProvenance` in the API, and it exists for one reason: the track
 * panel lets the user flip between the LRCLIB version and the AI version locally, without a refetch, so
 * the label has to be recomputed client-side from the same inputs. Everywhere the displayed source is
 * fixed (the share page, the standalone lyrics panel) the server's value is used directly.
 *
 * Keep the two in step. The asymmetry is the load-bearing part: an AI transcription that could NOT be
 * aligned to published lyrics is reported as fully AI-generated, because its words are the machine's
 * guess. Anything else the AI touched only moved timestamps, and the words stayed human.
 */
export function computeLyricsProvenance(input: {
  /** True when the viewer is currently showing the AI transcription rather than the LRCLIB lyrics. */
  showingTranscription: boolean
  /** True when that transcription carries the official words re-timed, not its own guess at them. */
  alignedToReference: boolean
  hasSyncedLyrics: boolean
  /** Non-null when a measured constant offset was applied to the stored LRC. */
  syncOffsetMs?: number | null
}): LyricsProvenance {
  if (input.showingTranscription) {
    return input.alignedToReference ? 'AiEnhanced' : 'AiGenerated'
  }
  return input.syncOffsetMs != null && input.hasSyncedLyrics ? 'AiEnhanced' : 'Human'
}

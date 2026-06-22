import { fetchSettings } from '$lib/api-client';

/**
 * Server feature flags, fetched once from /api/settings. Used to hide experimental UI when the backing
 * provider isn't configured on the server (e.g. AI lyrics transcription is invisible without a key).
 */
class FeaturesStore {
  /** AI lyrics transcription — true only when a transcription provider is configured on the server. */
  lyricsTranscription = $state(false);

  #loaded = false;

  /** Loads the flags once (idempotent). Safe to call from any component on mount. */
  async ensureLoaded(): Promise<void> {
    if (this.#loaded) return;
    this.#loaded = true;
    try {
      const settings = await fetchSettings();
      this.lyricsTranscription = settings.lyricsTranscription?.enabled ?? false;
    } catch {
      this.#loaded = false; // allow a later retry
    }
  }
}

export const featuresStore = new FeaturesStore();

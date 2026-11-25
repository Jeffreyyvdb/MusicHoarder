import type { SettingsLyricsTranscriptionView } from '#lib/api-client.ts';

/**
 * Whether to ask the admin about background AI-timed lyrics: a provider is configured and nobody has
 * answered yet. A server too old to report the choice (`autoSyncChosen` absent) is never asked about.
 */
export function shouldAskAboutAutoSync(view: SettingsLyricsTranscriptionView): boolean {
  return view.enabled && view.autoSyncChosen === false;
}

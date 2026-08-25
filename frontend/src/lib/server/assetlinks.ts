// Digital Asset Links statement for Android App Links: proves to Android that the app
// signed with these certificates may open this origin's https links directly. Served by
// /.well-known/assetlinks.json; fingerprints come from ANDROID_ASSETLINKS_FINGERPRINTS so
// every deployment (each with its own signing story) configures its own trust.

/** The applicationId of the Android client — fixed across hosts; only the manifest host varies. */
export const ANDROID_PACKAGE_NAME = 'com.musichoarder.app';

/**
 * Parses the env value into SHA-256 cert fingerprints. Comma-separated to allow several
 * certs at once (release + debug). Returns null when nothing usable is configured — the
 * route treats that as "endpoint disabled" (404) so instances without the app are unaffected.
 */
export function parseFingerprints(raw: string | undefined | null): string[] | null {
  if (!raw) return null;
  const fingerprints = raw
    .split(',')
    .map((f) => f.trim().toUpperCase())
    .filter((f) => f.length > 0);
  return fingerprints.length > 0 ? fingerprints : null;
}

/** The assetlinks.json body Android's verifier expects: a single handle_all_urls statement. */
export function assetlinksJson(fingerprints: string[]): string {
  return JSON.stringify([
    {
      relation: ['delegate_permission/common.handle_all_urls'],
      target: {
        namespace: 'android_app',
        package_name: ANDROID_PACKAGE_NAME,
        sha256_cert_fingerprints: fingerprints
      }
    }
  ]);
}

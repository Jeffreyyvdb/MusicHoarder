// Digital Asset Links statement: proves to Android that the app signed with these certificates
// speaks for this origin. Served by /.well-known/assetlinks.json; fingerprints come from
// ANDROID_ASSETLINKS_FINGERPRINTS so every deployment (each with its own signing story)
// configures its own trust.
//
// Two capabilities ride on the one file:
//   - handle_all_urls  — the app may open this origin's https links directly (share, invite).
//   - get_login_creds  — Credential Manager may offer this origin's passkeys inside the app.
// A passkey is bound to the web origin, so without the second relation the in-app sign-in sheet
// reports no usable credential and nothing in the app can fix it.

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

/**
 * The assetlinks.json body Android's verifiers expect: one statement per relation, both naming the
 * same app. Kept as two entries rather than one with both relations because that is the shape
 * Google's documentation and its statement-list tester use, and the App Links verifier is the
 * fussier of the two consumers.
 */
export function assetlinksJson(fingerprints: string[]): string {
  const target = {
    namespace: 'android_app',
    package_name: ANDROID_PACKAGE_NAME,
    sha256_cert_fingerprints: fingerprints
  };
  return JSON.stringify([
    { relation: ['delegate_permission/common.handle_all_urls'], target },
    { relation: ['delegate_permission/common.get_login_creds'], target }
  ]);
}

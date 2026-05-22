import { createHmac, timingSafeEqual } from 'node:crypto';

/**
 * Server-side verification for the Spotify OAuth relay. Mirrors the C# `SpotifyOAuthStateProtector`:
 *
 *   state = base64url(payloadJson) + "." + base64url(hmacSha256(key, base64url(payloadJson)))
 *   payload = { ro: "<returnOrigin>", n: "<nonce>", iat: <unixSeconds> }
 *
 * The HMAC is over the ASCII bytes of the first segment, so we verify by re-signing the literal segment we
 * received (no JSON re-serialization). The signature is what stops the relay from bouncing the OAuth `code` to an
 * attacker-chosen origin, so a missing/invalid signature must fail closed.
 */

function base64UrlDecode(value: string): Buffer {
  const padded = value.replace(/-/g, '+').replace(/_/g, '/');
  return Buffer.from(padded, 'base64');
}

/** Default state TTL in seconds, mirroring the C# `SpotifyOptions.OAuthStateTtlMinutes` default (10 min). */
const DEFAULT_MAX_AGE_SECONDS = 600;

/** Clock-skew tolerance for future-dated states, mirroring the C# protector's `age < -1min` guard. */
const CLOCK_SKEW_TOLERANCE_SECONDS = 60;

/**
 * Returns the verified return origin (normalized, no path) or null when the state is
 * forged/tampered/malformed/expired.
 *
 * TTL is enforced here (not just in the C# callback) so the relay fails closed on a stale or replayed
 * `state` before it bounces the OAuth `code` — defense in depth that mirrors the C# protector's TTL check.
 */
export function verifyRelayState(
  state: string | null,
  signingKey: string,
  maxAgeSeconds: number = DEFAULT_MAX_AGE_SECONDS
): string | null {
  if (!state || !signingKey) return null;

  const dot = state.indexOf('.');
  if (dot <= 0 || dot === state.length - 1) return null;

  const payloadSegment = state.slice(0, dot);
  const signatureSegment = state.slice(dot + 1);

  let provided: Buffer;
  try {
    provided = base64UrlDecode(signatureSegment);
  } catch {
    return null;
  }

  const expected = createHmac('sha256', signingKey).update(payloadSegment, 'ascii').digest();
  if (provided.length !== expected.length || !timingSafeEqual(provided, expected)) return null;

  let parsed: { ro?: unknown; iat?: unknown };
  try {
    parsed = JSON.parse(base64UrlDecode(payloadSegment).toString('utf8'));
  } catch {
    return null;
  }

  if (typeof parsed.ro !== 'string' || parsed.ro.length === 0) return null;

  if (typeof parsed.iat !== 'number' || !Number.isFinite(parsed.iat)) return null;
  const ageSeconds = Math.floor(Date.now() / 1000) - parsed.iat;
  if (ageSeconds > maxAgeSeconds || ageSeconds < -CLOCK_SKEW_TOLERANCE_SECONDS) return null;

  try {
    // Normalize to an origin (scheme://host[:port]) so any path/query smuggled into `ro` is dropped before we
    // build the redirect target — the bounce can only ever hit the origin's own callback path.
    return new URL(parsed.ro).origin;
  } catch {
    return null;
  }
}

/**
 * Matches a return origin against a comma/whitespace-separated allowlist. Each entry is an origin pattern where a
 * single `*` matches one run of non-dot/non-slash characters — e.g. `https://*.preview.example.com` (any PR
 * subdomain) or `https://localhost:*` / `http://127.0.0.1:*` (any local dev port).
 */
export function isAllowedReturnOrigin(origin: string, allowlist: string): boolean {
  const patterns = allowlist
    .split(/[\s,]+/)
    .map((p) => p.trim().replace(/\/+$/, ''))
    .filter(Boolean);

  return patterns.some((pattern) => {
    const regex = new RegExp('^' + escapeRegex(pattern).replace(/\\\*/g, '[^./]+') + '$', 'i');
    return regex.test(origin);
  });
}

function escapeRegex(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

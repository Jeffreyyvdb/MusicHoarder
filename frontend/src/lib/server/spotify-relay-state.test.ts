import { createHmac } from 'node:crypto';
import { describe, expect, it } from 'vitest';
import { isAllowedReturnOrigin, verifyRelayState } from './spotify-relay-state';

const KEY = 'test-signing-key-0123456789';

function b64url(buf: Buffer): string {
  return buf.toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

/**
 * Mirrors the C# `SpotifyOAuthStateProtector.Create`: base64url(payloadJson) + "." + base64url(hmac).
 * Lets us forge valid, tampered, and time-shifted states without depending on the producer.
 */
function createState(
  returnOrigin: string,
  key: string,
  iatSeconds: number = Math.floor(Date.now() / 1000),
  nonce = 'test-nonce'
): string {
  const payload = { ro: returnOrigin, n: nonce, iat: iatSeconds };
  const payloadSegment = b64url(Buffer.from(JSON.stringify(payload), 'utf8'));
  const signature = createHmac('sha256', key).update(payloadSegment, 'ascii').digest();
  return `${payloadSegment}.${b64url(signature)}`;
}

describe('verifyRelayState', () => {
  it('round-trips a valid state to its return origin', () => {
    const state = createState('https://localhost:65284', KEY);
    expect(verifyRelayState(state, KEY)).toBe('https://localhost:65284');
  });

  it('returns null when the signing key is wrong', () => {
    const state = createState('https://localhost:65284', KEY);
    expect(verifyRelayState(state, 'a-different-key')).toBeNull();
  });

  it('returns null when the signing key is empty', () => {
    const state = createState('https://localhost:65284', KEY);
    expect(verifyRelayState(state, '')).toBeNull();
  });

  it('returns null when the payload is tampered but the signature is kept', () => {
    const state = createState('https://localhost:65284', KEY);
    const dot = state.indexOf('.');
    const payload = state.slice(0, dot);
    const mutated = (payload[0] === 'A' ? 'B' : 'A') + payload.slice(1);
    const tampered = `${mutated}${state.slice(dot)}`;
    expect(verifyRelayState(tampered, KEY)).toBeNull();
  });

  it('returns null for a tampered-but-valid-base64 signature (right length, wrong bytes)', () => {
    const state = createState('https://localhost:65284', KEY);
    const dot = state.indexOf('.');
    const payloadSegment = state.slice(0, dot);
    // A signature that decodes cleanly as base64url and has the correct length, but is computed under a
    // different key — exercises the timingSafeEqual mismatch path rather than the decode-failure path.
    const forgedSig = b64url(
      createHmac('sha256', 'attacker-key').update(payloadSegment, 'ascii').digest()
    );
    const forged = `${payloadSegment}.${forgedSig}`;
    expect(forged).not.toBe(state);
    expect(verifyRelayState(forged, KEY)).toBeNull();
  });

  it.each([
    ['null', null],
    ['empty', ''],
    ['no separator', 'no-dot-here'],
    ['empty signature', 'only.'],
    ['empty payload', '.onlysig'],
    ['non-base64 signature', 'cGF5bG9hZA.not!base64!!']
  ])('returns null for malformed input (%s)', (_label, input) => {
    expect(verifyRelayState(input as string | null, KEY)).toBeNull();
  });

  it('strips any path/query smuggled into the return origin via URL.origin normalization', () => {
    const state = createState('https://origin.com/path/here?q=1#frag', KEY);
    expect(verifyRelayState(state, KEY)).toBe('https://origin.com');
  });

  it('returns null when the return origin is empty', () => {
    const state = createState('', KEY);
    expect(verifyRelayState(state, KEY)).toBeNull();
  });

  it('returns null when the return origin is not a parseable URL', () => {
    const state = createState('not a url', KEY);
    expect(verifyRelayState(state, KEY)).toBeNull();
  });

  describe('TTL enforcement', () => {
    const now = () => Math.floor(Date.now() / 1000);

    it('accepts a freshly issued state', () => {
      const state = createState('https://localhost:65284', KEY, now() - 5);
      expect(verifyRelayState(state, KEY)).toBe('https://localhost:65284');
    });

    it('rejects a state older than the default 10-minute TTL', () => {
      const state = createState('https://localhost:65284', KEY, now() - 601);
      expect(verifyRelayState(state, KEY)).toBeNull();
    });

    it('honors a custom maxAgeSeconds', () => {
      const state = createState('https://localhost:65284', KEY, now() - 30);
      expect(verifyRelayState(state, KEY, 60)).toBe('https://localhost:65284');
      expect(verifyRelayState(state, KEY, 10)).toBeNull();
    });

    it('rejects a future-dated state beyond the clock-skew tolerance', () => {
      const state = createState('https://localhost:65284', KEY, now() + 120);
      expect(verifyRelayState(state, KEY)).toBeNull();
    });

    it('tolerates minor clock skew (future-dated within 60s)', () => {
      const state = createState('https://localhost:65284', KEY, now() + 30);
      expect(verifyRelayState(state, KEY)).toBe('https://localhost:65284');
    });

    it('returns null when iat is missing', () => {
      const payloadSegment = b64url(
        Buffer.from(JSON.stringify({ ro: 'https://x.com', n: 'n' }), 'utf8')
      );
      const sig = b64url(createHmac('sha256', KEY).update(payloadSegment, 'ascii').digest());
      expect(verifyRelayState(`${payloadSegment}.${sig}`, KEY)).toBeNull();
    });
  });
});

describe('isAllowedReturnOrigin', () => {
  it('returns false for an empty allowlist', () => {
    expect(isAllowedReturnOrigin('https://example.com', '')).toBe(false);
    expect(isAllowedReturnOrigin('https://example.com', '   ')).toBe(false);
  });

  it('matches an exact origin', () => {
    expect(isAllowedReturnOrigin('https://example.com', 'https://example.com')).toBe(true);
  });

  it('does not match a different exact origin', () => {
    expect(isAllowedReturnOrigin('https://evil.com', 'https://example.com')).toBe(false);
  });

  it('matches a single subdomain label via wildcard', () => {
    expect(isAllowedReturnOrigin('https://api.example.com', 'https://*.example.com')).toBe(true);
    expect(isAllowedReturnOrigin('https://pr-123.example.com', 'https://*.example.com')).toBe(true);
  });

  it('does NOT match across a subdomain boundary (wildcard excludes dots)', () => {
    // The `[^./]+` expansion means `*` cannot span a `.` — this is the open-redirect escape the review flagged.
    expect(isAllowedReturnOrigin('https://evil.sub.example.com', 'https://*.example.com')).toBe(
      false
    );
  });

  it('does not let the wildcard match the bare apex', () => {
    expect(isAllowedReturnOrigin('https://example.com', 'https://*.example.com')).toBe(false);
  });

  it('matches any port via a port wildcard', () => {
    expect(isAllowedReturnOrigin('https://localhost:5173', 'https://localhost:*')).toBe(true);
    expect(isAllowedReturnOrigin('http://127.0.0.1:65284', 'http://127.0.0.1:*')).toBe(true);
  });

  it('does not match a different scheme or host on a port wildcard', () => {
    expect(isAllowedReturnOrigin('http://localhost:5173', 'https://localhost:*')).toBe(false);
    expect(isAllowedReturnOrigin('https://localhost.evil.com:5173', 'https://localhost:*')).toBe(
      false
    );
  });

  it('matches against any entry in a comma/whitespace-separated allowlist', () => {
    const allowlist = 'https://app.example.com, https://*.preview.example.com\nhttps://localhost:*';
    expect(isAllowedReturnOrigin('https://app.example.com', allowlist)).toBe(true);
    expect(isAllowedReturnOrigin('https://pr-7.preview.example.com', allowlist)).toBe(true);
    expect(isAllowedReturnOrigin('https://localhost:3000', allowlist)).toBe(true);
    expect(isAllowedReturnOrigin('https://nope.example.com', allowlist)).toBe(false);
  });

  it('normalizes trailing slashes on allowlist entries', () => {
    expect(isAllowedReturnOrigin('https://example.com', 'https://example.com/')).toBe(true);
  });

  it('matches case-insensitively', () => {
    expect(isAllowedReturnOrigin('https://Example.com', 'https://example.com')).toBe(true);
  });
});

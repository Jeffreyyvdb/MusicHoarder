import { describe, expect, it } from 'vitest';
import { ANDROID_PACKAGE_NAME, assetlinksJson, parseFingerprints } from './assetlinks';

describe('parseFingerprints', () => {
  it('returns null for unset or blank values', () => {
    expect(parseFingerprints(undefined)).toBeNull();
    expect(parseFingerprints(null)).toBeNull();
    expect(parseFingerprints('')).toBeNull();
    expect(parseFingerprints('  ')).toBeNull();
    expect(parseFingerprints(',, ,')).toBeNull();
  });

  it('parses a single fingerprint and uppercases it', () => {
    expect(parseFingerprints('aa:bb:cc')).toEqual(['AA:BB:CC']);
  });

  it('splits a comma-separated list, trimming whitespace and dropping empties', () => {
    expect(parseFingerprints(' AA:BB , cc:dd ,, ')).toEqual(['AA:BB', 'CC:DD']);
  });
});

describe('assetlinksJson', () => {
  const target = {
    namespace: 'android_app',
    package_name: ANDROID_PACKAGE_NAME,
    sha256_cert_fingerprints: ['AA:BB', 'CC:DD']
  };

  it('emits the handle_all_urls statement Android expects', () => {
    const parsed = JSON.parse(assetlinksJson(['AA:BB', 'CC:DD']));
    expect(parsed).toContainEqual({
      relation: ['delegate_permission/common.handle_all_urls'],
      target
    });
  });

  it('emits the get_login_creds statement, without which in-app passkeys find nothing', () => {
    const parsed = JSON.parse(assetlinksJson(['AA:BB', 'CC:DD']));
    expect(parsed).toContainEqual({
      relation: ['delegate_permission/common.get_login_creds'],
      target
    });
    expect(parsed).toHaveLength(2);
  });
});

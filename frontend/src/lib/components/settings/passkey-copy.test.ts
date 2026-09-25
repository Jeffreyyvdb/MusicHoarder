import { describe, expect, it } from 'vitest';
import { defaultPasskeyName, passkeyPlatform, passkeyUnlockPhrase } from './passkey-copy';

const IPHONE =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1';
const MAC =
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15';
const WINDOWS =
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36';
const ANDROID =
  'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Mobile Safari/537.36';
const LINUX =
  'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/128.0 Safari/537.36';

describe('passkeyPlatform', () => {
  it('tells the platforms apart, including an iPad posing as a Mac', () => {
    expect(passkeyPlatform(IPHONE)).toBe('iphone');
    expect(passkeyPlatform(MAC, 0)).toBe('mac');
    expect(passkeyPlatform(MAC, 5)).toBe('ipad');
    expect(passkeyPlatform(WINDOWS)).toBe('windows');
    expect(passkeyPlatform(ANDROID)).toBe('android');
    expect(passkeyPlatform(LINUX)).toBe('other');
  });
});

describe('passkey copy', () => {
  it('never names Windows Hello on an iPhone, nor Face ID on Windows', () => {
    expect(passkeyUnlockPhrase('iphone')).toBe('Face ID or Touch ID');
    expect(passkeyUnlockPhrase('iphone')).not.toMatch(/Windows/);
    expect(passkeyUnlockPhrase('windows')).not.toMatch(/Face ID|Touch ID/);
  });

  it('names a new passkey after the device it was made on', () => {
    expect(defaultPasskeyName(passkeyPlatform(IPHONE))).toBe('iPhone');
    expect(defaultPasskeyName(passkeyPlatform(MAC, 5))).toBe('iPad');
    expect(defaultPasskeyName('other')).toBe('Passkey');
  });
});

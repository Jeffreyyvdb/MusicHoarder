import { afterEach, describe, expect, it, vi } from 'vitest';
import { isIosDevice, isIosSafari, isIosSafariUserAgent } from './ios-safari';

const IPHONE_SAFARI =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Mobile/15E148 Safari/604.1';
const IPAD_SAFARI_MOBILE_UA =
  'Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E148 Safari/604.1';
const IPADOS_DESKTOP_UA =
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.1 Safari/605.1.15';
const IPHONE_CHROME =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) CriOS/130.0.6723.90 Mobile/15E148 Safari/604.1';
const IPHONE_FIREFOX =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) FxiOS/132.0 Mobile/15E148 Safari/605.1.15';
const IPHONE_INSTAGRAM =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148 Instagram 350.0.0.0';
const IPHONE_HOME_SCREEN_APP =
  'Mozilla/5.0 (iPhone; CPU iPhone OS 18_1 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Mobile/15E148';
const ANDROID_CHROME =
  'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36';

afterEach(() => vi.unstubAllGlobals());

describe('isIosSafariUserAgent', () => {
  it('matches Safari on iPhone and on an iPad sending its mobile agent', () => {
    expect(isIosSafariUserAgent(IPHONE_SAFARI)).toBe(true);
    expect(isIosSafariUserAgent(IPAD_SAFARI_MOBILE_UA)).toBe(true);
  });

  it('rejects the other iOS browsers, in-app web views, Android and a missing header', () => {
    expect(isIosSafariUserAgent(IPHONE_CHROME)).toBe(false);
    expect(isIosSafariUserAgent(IPHONE_FIREFOX)).toBe(false);
    expect(isIosSafariUserAgent(IPHONE_INSTAGRAM)).toBe(false);
    expect(isIosSafariUserAgent(ANDROID_CHROME)).toBe(false);
    expect(isIosSafariUserAgent(null)).toBe(false);
  });

  it('cannot see iPadOS behind its desktop agent — that one is the client check', () => {
    expect(isIosSafariUserAgent(IPADOS_DESKTOP_UA)).toBe(false);
  });
});

describe('isIosSafari', () => {
  it('tells iPadOS Safari from a Mac by its touch points', () => {
    vi.stubGlobal('navigator', { userAgent: IPADOS_DESKTOP_UA, maxTouchPoints: 5 });
    expect(isIosSafari()).toBe(true);
    vi.stubGlobal('navigator', { userAgent: IPADOS_DESKTOP_UA, maxTouchPoints: 0 });
    expect(isIosSafari()).toBe(false);
  });

  it('is false where there is no navigator (SSR)', () => {
    vi.stubGlobal('navigator', undefined);
    expect(isIosSafari()).toBe(false);
    expect(isIosDevice()).toBe(false);
  });

  it('treats the installed Home Screen app as an iOS device, but not as Safari', () => {
    vi.stubGlobal('navigator', { userAgent: IPHONE_HOME_SCREEN_APP, maxTouchPoints: 5 });
    expect(isIosDevice()).toBe(true);
    expect(isIosSafari()).toBe(false);
  });
});

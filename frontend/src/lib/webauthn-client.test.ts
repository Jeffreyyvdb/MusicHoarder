import { afterEach, describe, expect, it, vi } from 'vitest';
import { isPasskeySupported } from './webauthn-client.ts';

afterEach(() => {
  vi.unstubAllGlobals();
});

function browserWithWebAuthn(capacitor?: object) {
  vi.stubGlobal('window', { PublicKeyCredential: class {}, ...(capacitor ? { Capacitor: capacitor } : {}) });
  vi.stubGlobal('navigator', { credentials: {} });
}

describe('passkey support', () => {
  it('is offered in a browser that has WebAuthn', () => {
    browserWithWebAuthn();
    expect(isPasskeySupported()).toBe(true);
  });

  it('is not offered inside the app shell, where WebKit refuses every request', () => {
    browserWithWebAuthn({ isNativePlatform: () => true });
    expect(isPasskeySupported()).toBe(false);
  });
});

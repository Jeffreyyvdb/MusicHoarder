/**
 * Passkey wording that names only what the device in hand can do.
 *
 * On an iPhone "Touch ID, Windows Hello, or a security key" lists two things the phone does not
 * have (HIG, managing-accounts: refer only to authentication methods available in the current
 * context), and a name field that suggests "MacBook Touch ID" there is a small lie too. Pure
 * functions of the user agent so they are testable and SSR-safe; callers pass
 * `navigator.userAgent` / `navigator.maxTouchPoints`.
 */
export type PasskeyPlatform = 'iphone' | 'ipad' | 'mac' | 'windows' | 'android' | 'other';

export function passkeyPlatform(userAgent: string, maxTouchPoints = 0): PasskeyPlatform {
  if (/\b(iPhone|iPod)\b/.test(userAgent)) return 'iphone';
  if (/\biPad\b/.test(userAgent)) return 'ipad';
  // iPadOS asks for desktop sites with a Mac user agent; only the touch points tell it apart.
  if (/\bMacintosh\b/.test(userAgent)) return maxTouchPoints > 1 ? 'ipad' : 'mac';
  if (/\bAndroid\b/.test(userAgent)) return 'android';
  if (/\bWindows\b/.test(userAgent)) return 'windows';
  return 'other';
}

/** How the passkey is unlocked here, for "Sign in with ___ — no email needed." */
export function passkeyUnlockPhrase(platform: PasskeyPlatform): string {
  switch (platform) {
    case 'iphone':
    case 'ipad':
      return 'Face ID or Touch ID';
    case 'mac':
      return 'Touch ID or your password manager';
    case 'windows':
      return 'Windows Hello or a security key';
    case 'android':
      return 'your fingerprint or screen lock';
    default:
      return 'this device or a security key';
  }
}

/** The name a new passkey gets when the field is left empty: the device it was made on. */
export function defaultPasskeyName(platform: PasskeyPlatform): string {
  switch (platform) {
    case 'iphone':
      return 'iPhone';
    case 'ipad':
      return 'iPad';
    case 'mac':
      return 'Mac';
    case 'windows':
      return 'Windows PC';
    case 'android':
      return 'Android phone';
    default:
      return 'Passkey';
  }
}

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { noteAppSignInRequested, takeAppSignInRequest } from './app-sign-in.ts';

beforeEach(() => {
  const items = new Map<string, string>();
  vi.stubGlobal('localStorage', {
    getItem: (key: string) => items.get(key) ?? null,
    setItem: (key: string, value: string) => void items.set(key, value),
    removeItem: (key: string) => void items.delete(key)
  });
});

describe('a sign-in link handed to the app', () => {
  it('is refused when none was asked for here', () => {
    expect(takeAppSignInRequest(1000)).toBe(false);
  });

  it('is taken once, while the link can still be alive', () => {
    noteAppSignInRequested(1000);
    expect(takeAppSignInRequest(1000 + 10 * 60 * 1000)).toBe(true);
    expect(takeAppSignInRequest(1000 + 10 * 60 * 1000)).toBe(false);
  });

  it('is refused once the link it waited for has expired', () => {
    noteAppSignInRequested(1000);
    expect(takeAppSignInRequest(1000 + 21 * 60 * 1000)).toBe(false);
  });
});

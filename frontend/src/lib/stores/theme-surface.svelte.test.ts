import { describe, expect, it } from 'vitest';
import { themeSurface } from './theme-surface.svelte';

describe('themeSurface', () => {
  it('is media while any claim is held', () => {
    expect(themeSurface.media).toBe(false);
    const a = themeSurface.claimMedia();
    const b = themeSurface.claimMedia();
    a();
    expect(themeSurface.media).toBe(true);
    b();
    expect(themeSurface.media).toBe(false);
  });

  it('counts a repeated release once', () => {
    const a = themeSurface.claimMedia();
    const b = themeSurface.claimMedia();
    a();
    a();
    expect(themeSurface.media).toBe(true);
    b();
    expect(themeSurface.media).toBe(false);
  });
});

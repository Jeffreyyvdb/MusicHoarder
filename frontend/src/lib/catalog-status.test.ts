import { describe, expect, it } from 'vitest';
import { isRetry, wishlistLabel } from '#lib/catalog-status.ts';

describe('wishlistLabel', () => {
  it('offers Add for a track not on the wishlist', () => {
    expect(wishlistLabel(null)).toBeNull();
    expect(wishlistLabel(undefined)).toBeNull();
  });

  it('offers Add again after a download that produced no file', () => {
    expect(wishlistLabel('Failed')).toBeNull();
    expect(wishlistLabel('NotFound')).toBeNull();
    expect(isRetry('NotFound')).toBe(true);
    expect(isRetry(null)).toBe(false);
  });

  it('names a queued or finished download instead of offering a second add', () => {
    expect(wishlistLabel('Pending')).toBe('In wishlist');
    expect(wishlistLabel('Downloading')).toBe('Downloading');
    expect(wishlistLabel('Downloaded')).toBe('Downloaded');
    expect(wishlistLabel('SkippedOwned')).toBe('In library');
  });

  it('treats a status it does not know as already on the wishlist', () => {
    expect(wishlistLabel('SomethingNew')).toBe('In wishlist');
  });
});

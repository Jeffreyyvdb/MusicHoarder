import { describe, expect, it } from 'vitest';
import { isChunkLoadError } from './chunk-load-error';

describe('isChunkLoadError', () => {
  it('matches the browser phrasings for a failed dynamic import', () => {
    expect(isChunkLoadError(new Error('Failed to fetch dynamically imported module: /x.js'))).toBe(
      true
    );
    expect(isChunkLoadError(new Error('error loading dynamically imported module'))).toBe(true);
    expect(isChunkLoadError(new Error('Importing a module script failed. 500'))).toBe(true);
  });

  it('accepts strings and { message } shapes (e.g. unhandledrejection reasons)', () => {
    expect(isChunkLoadError('Importing a module script failed.')).toBe(true);
    expect(isChunkLoadError({ message: 'Failed to fetch dynamically imported module' })).toBe(true);
  });

  it('ignores unrelated and empty errors', () => {
    expect(isChunkLoadError(new Error('TypeError: undefined is not a function'))).toBe(false);
    expect(isChunkLoadError(null)).toBe(false);
    expect(isChunkLoadError(undefined)).toBe(false);
    expect(isChunkLoadError({})).toBe(false);
  });
});

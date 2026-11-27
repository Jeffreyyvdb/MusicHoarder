import { describe, expect, it } from 'vitest';
import { buildOwnedIndex, isInLibrary } from '#lib/catalog-match.ts';

const index = buildOwnedIndex([
  { title: 'Beyoncé’s Song', artist: 'Beyoncé' },
  { title: 'Best Friend', artist: 'Saweetie; Doja Cat', artists: 'Saweetie; Doja Cat' },
  { title: 'Not Mine', artist: 'Someone', sharedByUserId: 'u1' },
  { title: null, artist: 'Nobody' }
]);

describe('isInLibrary', () => {
  it('matches across case, accents and punctuation', () => {
    expect(isInLibrary(index, { title: 'BEYONCES SONG!', artist: 'beyonce' })).toBe(true);
  });

  it('matches a result by the lead of a multi-artist credit', () => {
    expect(isInLibrary(index, { title: 'Best Friend', artist: 'Saweetie' })).toBe(true);
  });

  it('does not match a different artist with the same title', () => {
    expect(isInLibrary(index, { title: 'Best Friend', artist: 'Negative Approach' })).toBe(false);
  });

  it('does not count a song shared by someone else', () => {
    expect(isInLibrary(index, { title: 'Not Mine', artist: 'Someone' })).toBe(false);
  });

  it('never matches a result whose title or artist folds to nothing', () => {
    expect(isInLibrary(index, { title: '???', artist: 'Beyoncé' })).toBe(false);
  });
});

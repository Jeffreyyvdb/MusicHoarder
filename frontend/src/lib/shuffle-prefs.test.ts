import { describe, expect, it } from 'vitest';
import {
  MAX_REMEMBERED,
  isShuffleOn,
  isShuffleScope,
  parseShufflePrefs,
  shuffleScopes,
  withShuffle,
  type ShuffleScope
} from './shuffle-prefs';

describe('shuffle prefs', () => {
  it('names one scope per album and per playlist, and one for everything else', () => {
    expect(shuffleScopes.album('Artist|Album')).toBe('album:Artist|Album');
    expect(shuffleScopes.playlist(12)).toBe('playlist:12');
    for (const scope of ['library', 'album:x', 'playlist:3'])
      expect(isShuffleScope(scope)).toBe(true);
    for (const bad of ['', 'album:', 'playlist:x', 'artist:y', 3, null]) {
      expect(isShuffleScope(bad)).toBe(false);
    }
  });

  it('is off until turned on, and forgets a scope turned off', () => {
    let prefs = parseShufflePrefs(null);
    expect(isShuffleOn(prefs, 'library')).toBe(false);
    prefs = withShuffle(prefs, 'album:a', true);
    expect(isShuffleOn(prefs, 'album:a')).toBe(true);
    expect(isShuffleOn(prefs, 'album:b')).toBe(false);
    prefs = withShuffle(prefs, 'album:a', false);
    expect(prefs).toEqual([]);
  });

  it('reads back what it stored and drops what it could not have written', () => {
    const prefs = withShuffle(withShuffle([], 'library', true), 'playlist:4', true);
    expect(parseShufflePrefs(JSON.stringify(prefs))).toEqual(['library', 'playlist:4']);
    expect(parseShufflePrefs('{"library":true}')).toEqual([]);
    expect(parseShufflePrefs('not json')).toEqual([]);
    expect(parseShufflePrefs('["library", 5, "artist:x", "library"]')).toEqual(['library']);
  });

  it('forgets the least recently set scopes past its cap', () => {
    let prefs: readonly ShuffleScope[] = [];
    for (let i = 0; i <= MAX_REMEMBERED; i++)
      prefs = withShuffle(prefs, shuffleScopes.playlist(i), true);
    expect(prefs).toHaveLength(MAX_REMEMBERED);
    expect(isShuffleOn(prefs, 'playlist:0')).toBe(false);

    prefs = withShuffle(prefs, 'playlist:1', true); // set again: the newest now
    prefs = withShuffle(prefs, 'library', true);
    expect(isShuffleOn(prefs, 'playlist:1')).toBe(true);
    expect(isShuffleOn(prefs, 'playlist:2')).toBe(false);
  });
});

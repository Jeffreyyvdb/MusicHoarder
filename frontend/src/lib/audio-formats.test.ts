import { describe, expect, it } from 'vitest';
import {
  convertedStreamUrl,
  createUnplayableFormats,
  formatOf,
  needsConversion
} from './audio-formats';

/** A `canPlayType` for a browser that plays everything except the given media types. */
const browserWithout =
  (...missing: string[]) =>
  (mediaType: string) =>
    missing.includes(mediaType) ? '' : 'maybe';

const nothingRemembered = { has: () => false };

class MemoryStorage {
  values = new Map<string, string>();
  getItem(key: string) {
    return this.values.get(key) ?? null;
  }
  setItem(key: string, value: string) {
    this.values.set(key, value);
  }
}

describe('formatOf', () => {
  it('reads a file extension as a lowercase format', () => {
    expect(formatOf('.OPUS')).toBe('opus');
    expect(formatOf('flac')).toBe('flac');
  });

  it('is null without an extension', () => {
    expect(formatOf(null)).toBeNull();
    expect(formatOf(undefined)).toBeNull();
    expect(formatOf('')).toBeNull();
  });
});

describe('convertedStreamUrl', () => {
  it('asks the stream endpoint to decode the file', () => {
    expect(convertedStreamUrl('/api/mh/songs/7/stream')).toBe('/api/mh/songs/7/stream?format=wav');
    expect(convertedStreamUrl('/x/stream?t=1')).toBe('/x/stream?t=1&format=wav');
  });
});

describe('needsConversion', () => {
  it('converts only a format the browser says it cannot play', () => {
    const safari = browserWithout('audio/ogg; codecs="opus"');
    expect(needsConversion('opus', safari, nothingRemembered)).toBe(true);
    expect(needsConversion('flac', safari, nothingRemembered)).toBe(false);
    expect(needsConversion('mp3', safari, nothingRemembered)).toBe(false);
  });

  it('never converts where the browser can play the original', () => {
    const chrome = browserWithout();
    for (const format of ['opus', 'ogg', 'flac', 'mp3', 'm4a', 'wav']) {
      expect(needsConversion(format, chrome, nothingRemembered)).toBe(false);
    }
  });

  it('converts a format that already failed here, whatever the browser claims', () => {
    expect(needsConversion('opus', browserWithout(), { has: (f) => f === 'opus' })).toBe(true);
  });

  it('tries the original when the format or the browser is unknown', () => {
    expect(needsConversion(null, browserWithout(), nothingRemembered)).toBe(false);
    expect(needsConversion('xyz', browserWithout(), nothingRemembered)).toBe(false);
    expect(needsConversion('opus', null, nothingRemembered)).toBe(false);
  });
});

describe('createUnplayableFormats', () => {
  it('remembers a format across page loads in the same browser', () => {
    const storage = new MemoryStorage() as unknown as Storage;
    createUnplayableFormats(storage, 'Safari 26').add('opus');

    expect(createUnplayableFormats(storage, 'Safari 26').has('opus')).toBe(true);
  });

  it('forgets once the browser is updated, so the original gets another try', () => {
    const storage = new MemoryStorage() as unknown as Storage;
    createUnplayableFormats(storage, 'Safari 26.0').add('opus');

    expect(createUnplayableFormats(storage, 'Safari 26.1').has('opus')).toBe(false);
  });

  it('works for the page when storage is unavailable or holds junk', () => {
    const broken = {
      getItem: () => {
        throw new Error('blocked');
      },
      setItem: () => {
        throw new Error('blocked');
      }
    } as unknown as Storage;
    const formats = createUnplayableFormats(broken, 'ua');
    formats.add('opus');
    expect(formats.has('opus')).toBe(true);

    const junk = new MemoryStorage();
    junk.setItem('mh:unplayable-formats', '{not json');
    expect(createUnplayableFormats(junk as unknown as Storage, 'ua').has('opus')).toBe(false);
  });
});

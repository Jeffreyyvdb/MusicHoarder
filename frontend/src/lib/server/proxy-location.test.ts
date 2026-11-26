import { describe, expect, it } from 'vitest';
import { browserLocation } from './proxy-location.ts';

const target = 'http://api.internal:8080/api/artists/image?name=Daft%20Punk';

describe('browserLocation', () => {
  it('passes a redirect to another origin through unchanged', () => {
    expect(browserLocation('https://cdn.example/portrait.jpg', target)).toBe(
      'https://cdn.example/portrait.jpg'
    );
  });

  it('maps a relative location on the API back under the proxy', () => {
    expect(browserLocation('/api/songs/1/cover?v=abc', target)).toBe(
      '/api/mh/api/songs/1/cover?v=abc'
    );
    expect(browserLocation('other?x=1', target)).toBe('/api/mh/api/artists/other?x=1');
  });

  it("maps an absolute location naming the API's own origin back under the proxy", () => {
    expect(browserLocation('http://api.internal:8080/songs/2', target)).toBe('/api/mh/songs/2');
  });
});

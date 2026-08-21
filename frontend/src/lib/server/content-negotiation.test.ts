import { describe, expect, it } from 'vitest';
import {
  appendVaryAccept,
  explicitlyAcceptsHtml,
  parseAccept,
  preferredType
} from './content-negotiation';

const PRODUCES = ['text/html', 'text/markdown'];

describe('parseAccept', () => {
  it('reads types, q-values, and specificity', () => {
    expect(parseAccept('text/markdown;q=0.9, text/*;q=0.5, */*;q=0.1')).toEqual([
      { type: 'text/markdown', q: 0.9, specificity: 2 },
      { type: 'text/*', q: 0.5, specificity: 1 },
      { type: '*/*', q: 0.1, specificity: 0 }
    ]);
  });

  it('defaults a missing q to 1 and clamps out-of-range values', () => {
    expect(parseAccept('text/html, text/markdown;q=7, text/plain;q=-3')).toEqual([
      { type: 'text/html', q: 1, specificity: 2 },
      { type: 'text/markdown', q: 1, specificity: 2 },
      { type: 'text/plain', q: 0, specificity: 2 }
    ]);
  });

  it('ignores unrelated parameters and is case-insensitive on the type', () => {
    expect(parseAccept('TEXT/Markdown;charset=utf-8;q=0.4')).toEqual([
      { type: 'text/markdown', q: 0.4, specificity: 2 }
    ]);
  });
});

describe('preferredType', () => {
  it('serves markdown when it is the only acceptable type', () => {
    expect(preferredType('text/markdown', PRODUCES)).toBe('text/markdown');
  });

  it('defaults to HTML when no Accept header is sent', () => {
    expect(preferredType(null, PRODUCES)).toBe('text/html');
    expect(preferredType('', PRODUCES)).toBe('text/html');
  });

  it('defaults to HTML for a bare wildcard, so browsers and curl are unaffected', () => {
    expect(preferredType('*/*', PRODUCES)).toBe('text/html');
  });

  it('honours q-values in both directions', () => {
    expect(preferredType('text/html;q=0.2, text/markdown;q=0.9', PRODUCES)).toBe('text/markdown');
    expect(preferredType('text/html;q=0.9, text/markdown;q=0.2', PRODUCES)).toBe('text/html');
  });

  it('ignores a type explicitly refused with q=0', () => {
    expect(preferredType('text/html;q=0, text/markdown', PRODUCES)).toBe('text/markdown');
    expect(preferredType('text/markdown;q=0, */*', PRODUCES)).toBe('text/html');
  });

  it('takes each type q-value from the most specific range that matches it (RFC 9110)', () => {
    // markdown is named exactly (0.9); html only matches the `text/*` range (0.5).
    expect(preferredType('text/*;q=0.5, text/markdown;q=0.9', PRODUCES)).toBe('text/markdown');
    // The mirror case: the catch-all gives html 1.0, which outranks markdown's own 0.5.
    expect(preferredType('text/markdown;q=0.5, */*;q=1.0', PRODUCES)).toBe('text/html');
  });

  it('handles the browser Accept header a real Chrome request sends', () => {
    const chrome =
      'text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,*/*;q=0.8';
    expect(preferredType(chrome, PRODUCES)).toBe('text/html');
  });

  it('returns null when nothing acceptable can be produced, which is the 406 signal', () => {
    expect(preferredType('application/pdf', PRODUCES)).toBeNull();
    expect(preferredType('application/json, image/png', PRODUCES)).toBeNull();
    expect(preferredType('*/*;q=0', PRODUCES)).toBeNull();
  });
});

describe('explicitlyAcceptsHtml', () => {
  it('is true only when HTML is named by an exact type or a text/* range', () => {
    expect(explicitlyAcceptsHtml('text/html')).toBe(true);
    expect(explicitlyAcceptsHtml('text/html,application/xhtml+xml,*/*;q=0.8')).toBe(true);
    expect(explicitlyAcceptsHtml('text/*')).toBe(true);
  });

  it('is false for a bare wildcard, a missing header, or a refused HTML type', () => {
    expect(explicitlyAcceptsHtml('*/*')).toBe(false);
    expect(explicitlyAcceptsHtml(null)).toBe(false);
    expect(explicitlyAcceptsHtml('text/markdown')).toBe(false);
    expect(explicitlyAcceptsHtml('text/html;q=0, */*')).toBe(false);
  });
});

describe('appendVaryAccept', () => {
  it('sets Vary when the response has none', () => {
    const headers = new Headers();
    appendVaryAccept(headers);
    expect(headers.get('vary')).toBe('Accept');
  });

  it('appends to an existing Vary without clobbering it', () => {
    const headers = new Headers({ Vary: 'Accept-Encoding' });
    appendVaryAccept(headers);
    expect(headers.get('vary')).toBe('Accept-Encoding, Accept');
  });

  it('does not duplicate Accept, whatever its casing', () => {
    const headers = new Headers({ Vary: 'Accept-Encoding, accept' });
    appendVaryAccept(headers);
    expect(headers.get('vary')).toBe('Accept-Encoding, accept');
  });

  it('leaves a Vary: * response alone', () => {
    const headers = new Headers({ Vary: '*' });
    appendVaryAccept(headers);
    expect(headers.get('vary')).toBe('*');
  });
});

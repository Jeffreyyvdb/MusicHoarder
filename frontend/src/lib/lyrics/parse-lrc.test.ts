import { describe, expect, it } from 'vitest';
import { parseLrc } from './parse-lrc';

describe('parseLrc', () => {
  it('parses standard [mm:ss.xx] lines into time-ordered entries', () => {
    const lrc = '[00:12.34]First line\n[00:15.00]Second line';
    expect(parseLrc(lrc)).toEqual([
      { timeMs: 12340, text: 'First line' },
      { timeMs: 15000, text: 'Second line' }
    ]);
  });

  it('handles CRLF line endings (the blank-panel regression)', () => {
    // The old `/^...$/` regex left a trailing \r on every line, so nothing parsed and
    // the synced panel rendered blank while the badge still said "synced".
    const lrc = '[00:01.00]a\r\n[00:02.00]b\r\n';
    expect(parseLrc(lrc)).toEqual([
      { timeMs: 1000, text: 'a' },
      { timeMs: 2000, text: 'b' }
    ]);
  });

  it('returns [] for plain text with no timestamps so callers can fall back', () => {
    expect(parseLrc('Just some lyrics\nwith no timestamps')).toEqual([]);
  });

  it('emits one entry per inline timestamp when a line repeats', () => {
    expect(parseLrc('[00:12.00][00:15.50]Chorus')).toEqual([
      { timeMs: 12000, text: 'Chorus' },
      { timeMs: 15500, text: 'Chorus' }
    ]);
  });

  it('skips metadata tags that carry no mm:ss timestamp', () => {
    const lrc = '[ar:Artist]\n[ti:Title]\n[length:3:21]\n[00:05.00]Real line';
    expect(parseLrc(lrc)).toEqual([{ timeMs: 5000, text: 'Real line' }]);
  });

  it('accepts a colon-separated fractional part ([mm:ss:cc])', () => {
    expect(parseLrc('[01:02:50]Text')).toEqual([{ timeMs: 62500, text: 'Text' }]);
  });

  it('tolerates 1- and 3-digit minute and fractional fields', () => {
    expect(parseLrc('[3:04.5]short\n[123:04.500]long')).toEqual([
      { timeMs: 184500, text: 'short' },
      { timeMs: 7384500, text: 'long' }
    ]);
  });

  it('keeps an empty lyric line (no text after the timestamp)', () => {
    expect(parseLrc('[00:10.00]')).toEqual([{ timeMs: 10000, text: '' }]);
  });

  it('sorts out-of-order timestamps', () => {
    expect(parseLrc('[00:20.00]later\n[00:05.00]earlier')).toEqual([
      { timeMs: 5000, text: 'earlier' },
      { timeMs: 20000, text: 'later' }
    ]);
  });
});

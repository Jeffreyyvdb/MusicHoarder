import { describe, expect, it } from 'vitest';
import { createLogBudget, parseCspReports, readBodyCapped, redactUrl } from './csp-report.ts';

describe('parseCspReports', () => {
  it('reads the legacy report-uri shape', () => {
    const [v] = parseCspReports({
      'csp-report': {
        'document-uri': 'https://musichoarder.app/library?q=secret',
        'effective-directive': 'script-src-elem',
        'blocked-uri': 'https://evil.example/x.js?token=abc',
        disposition: 'report',
        'script-sample': ''
      }
    });
    expect(v).toEqual({
      directive: 'script-src-elem',
      blocked: 'https://evil.example/x.js',
      page: 'https://musichoarder.app/library',
      disposition: 'report',
      sample: ''
    });
  });

  it('reads the Reporting API shape and skips other report types', () => {
    const reports = parseCspReports([
      { type: 'deprecation', body: { id: 'x' } },
      {
        type: 'csp-violation',
        body: {
          documentURL: 'https://musichoarder.app/',
          effectiveDirective: 'img-src',
          blockedURL: 'inline',
          disposition: 'report'
        }
      }
    ]);
    expect(reports).toHaveLength(1);
    expect(reports[0].directive).toBe('img-src');
    expect(reports[0].blocked).toBe('inline');
  });

  it('returns nothing for unrelated bodies', () => {
    expect(parseCspReports(null)).toEqual([]);
    expect(parseCspReports({ hello: 'world' })).toEqual([]);
    expect(parseCspReports('csp-report')).toEqual([]);
  });
});

describe('redactUrl', () => {
  it('masks long path segments such as share tokens', () => {
    expect(redactUrl('https://musichoarder.app/share/0123456789abcdef0123')).toBe(
      'https://musichoarder.app/share/:id'
    );
  });

  it('keeps only the scheme of non-http URLs', () => {
    expect(redactUrl('data:image/png;base64,AAAA')).toBe('data');
  });
});

describe('createLogBudget', () => {
  it('allows `limit` per minute, then resets', () => {
    let t = 0;
    const take = createLogBudget(2, () => t);
    expect([take(), take(), take()]).toEqual([true, true, false]);
    t = 60_000;
    expect(take()).toBe(true);
  });
});

describe('readBodyCapped', () => {
  /** A request with a streamed body and no Content-Length, like a chunked upload. */
  function streamed(chunks: string[]): { request: Request; pulled: () => number } {
    let pulled = 0;
    const encoder = new TextEncoder();
    const body = new ReadableStream<Uint8Array>({
      pull(controller) {
        if (pulled < chunks.length) controller.enqueue(encoder.encode(chunks[pulled++]));
        else controller.close();
      }
    });
    const request = new Request('http://x/api/csp-report', {
      method: 'POST',
      body,
      // @ts-expect-error Node's fetch needs this for a stream body; the DOM types lack it.
      duplex: 'half'
    });
    return { request, pulled: () => pulled };
  }

  it('returns the text of a body within the cap', async () => {
    const { request } = streamed(['{"a":', '1}']);
    expect(await readBodyCapped(request, 64)).toBe('{"a":1}');
  });

  it('stops reading as soon as a streamed body passes the cap', async () => {
    const { request, pulled } = streamed([
      'x'.repeat(40),
      'x'.repeat(40),
      'x'.repeat(40),
      'x'.repeat(40)
    ]);
    expect(await readBodyCapped(request, 64)).toBeNull();
    expect(pulled()).toBeLessThan(4);
  });

  it('refuses a declared length over the cap without reading', async () => {
    const request = new Request('http://x/', {
      method: 'POST',
      body: 'x',
      headers: { 'content-length': '999999' }
    });
    expect(await readBodyCapped(request, 64)).toBeNull();
  });
});

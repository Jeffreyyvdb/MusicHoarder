import { describe, expect, it } from 'vitest';
import {
  addSources,
  applySecurityHeaders,
  originOf,
  requestIsHttps,
  type SecurityHeaderContext
} from './security-headers.ts';

const PROD: SecurityHeaderContext = {
  https: true,
  hostname: 'musichoarder.app',
  analyticsOrigins: []
};

describe('applySecurityHeaders', () => {
  it('sets the baseline headers', () => {
    const headers = new Headers();
    applySecurityHeaders(headers, PROD);
    expect(headers.get('X-Content-Type-Options')).toBe('nosniff');
    expect(headers.get('Referrer-Policy')).toBe('strict-origin-when-cross-origin');
    expect(headers.get('X-Frame-Options')).toBe('DENY');
    expect(headers.get('Content-Security-Policy')).toBe("frame-ancestors 'none'");
    expect(headers.get('Cross-Origin-Opener-Policy')).toBe('same-origin-allow-popups');
    expect(headers.get('Permissions-Policy')).toContain('camera=()');
    expect(headers.get('Strict-Transport-Security')).toMatch(/^max-age=\d+$/);
  });

  it('keeps headers a handler already set', () => {
    const headers = new Headers({ 'Referrer-Policy': 'no-referrer' });
    applySecurityHeaders(headers, PROD);
    expect(headers.get('Referrer-Policy')).toBe('no-referrer');
  });

  it('sends no HSTS over plain HTTP or on localhost', () => {
    const http = new Headers();
    applySecurityHeaders(http, { ...PROD, https: false });
    expect(http.has('Strict-Transport-Security')).toBe(false);

    const local = new Headers();
    applySecurityHeaders(local, { ...PROD, hostname: 'localhost' });
    expect(local.has('Strict-Transport-Security')).toBe(false);
  });

  it('adds analytics origins and the reporting endpoint to a report-only policy', () => {
    const headers = new Headers({
      'Content-Security-Policy-Report-Only':
        "default-src 'self'; script-src 'self' 'nonce-abc'; connect-src 'self'; report-to csp-endpoint"
    });
    applySecurityHeaders(headers, { ...PROD, analyticsOrigins: ['https://stats.example'] });
    const policy = headers.get('Content-Security-Policy-Report-Only')!;
    expect(policy).toContain("script-src 'self' 'nonce-abc' https://stats.example");
    expect(policy).toContain("connect-src 'self' https://stats.example");
    expect(policy).toContain("default-src 'self';");
    expect(headers.get('Reporting-Endpoints')).toBe('csp-endpoint="/api/csp-report"');
  });

  it('adds no Reporting-Endpoints header to responses without a report-only policy', () => {
    const headers = new Headers();
    applySecurityHeaders(headers, PROD);
    expect(headers.has('Reporting-Endpoints')).toBe(false);
  });
});

describe('addSources', () => {
  it('does not add a source twice', () => {
    expect(addSources("script-src 'self' https://a", 'script-src', ['https://a'])).toBe(
      "script-src 'self' https://a"
    );
  });

  it('leaves a policy without the directive alone', () => {
    expect(addSources("img-src 'self'", 'script-src', ['https://a'])).toBe("img-src 'self'");
  });
});

describe('originOf', () => {
  it('returns the origin of an absolute http(s) URL only', () => {
    expect(originOf('https://stats.example/script.js')).toBe('https://stats.example');
    expect(originOf('/script.js')).toBeNull();
    expect(originOf('javascript:alert(1)')).toBeNull();
    expect(originOf(undefined)).toBeNull();
  });
});

describe('requestIsHttps', () => {
  it('trusts the first X-Forwarded-Proto hop', () => {
    expect(requestIsHttps(new URL('http://internal/'), 'https')).toBe(true);
    expect(requestIsHttps(new URL('http://internal/'), 'https, http')).toBe(true);
    expect(requestIsHttps(new URL('http://internal/'), null)).toBe(false);
    expect(requestIsHttps(new URL('https://site/'), null)).toBe(true);
  });
});

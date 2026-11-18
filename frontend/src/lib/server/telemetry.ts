import { OTLPTraceExporter as GrpcTraceExporter } from '@opentelemetry/exporter-trace-otlp-grpc';
import { OTLPTraceExporter as JsonTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { OTLPTraceExporter as ProtobufTraceExporter } from '@opentelemetry/exporter-trace-otlp-proto';
import type { Span, SpanExporter, SpanProcessor } from '@opentelemetry/sdk-trace';

// Span attributes that hold a request's query string, alone or inside a URL. SvelteKit's root span
// records the whole URL (`http.url`), the HTTP instrumentations `url.full`, `url.query` and
// `http.target`.
const QUERY_ATTRIBUTES = ['http.url', 'url.full', 'http.target', 'url.query'];

const REDACTED = 'REDACTED';

/**
 * Keeps a query's parameter names and drops every value, as the API's .NET instrumentation does. A
 * query can carry a credential: an app sign-in link (`/auth/callback?client=app&token=…`) is not
 * spent by the page it opens, and a Looping reporter key arrives as `?looping=…`.
 */
export function redactQuery(query: string): string {
  return query
    .split('&')
    .map((part) => {
      const eq = part.indexOf('=');
      return eq < 0 ? (part === '' ? part : REDACTED) : `${part.slice(0, eq)}=${REDACTED}`;
    })
    .join('&');
}

function redactAttribute(name: string, value: string): string {
  if (name === 'url.query') return redactQuery(value);
  const start = value.indexOf('?');
  if (start < 0) return value;
  const end = value.indexOf('#', start);
  const query = value.slice(start + 1, end < 0 ? undefined : end);
  return `${value.slice(0, start + 1)}${redactQuery(query)}${end < 0 ? '' : value.slice(end)}`;
}

/** Redacts query values on every span just before it ends, after all its attributes are set. */
export const redactQueryValues: SpanProcessor = {
  onStart() {},
  onEnding(span: Span) {
    for (const name of QUERY_ATTRIBUTES) {
      const value = span.attributes[name];
      if (typeof value === 'string') span.setAttribute(name, redactAttribute(name, value));
    }
  },
  onEnd() {},
  forceFlush: () => Promise.resolve(),
  shutdown: () => Promise.resolve()
};

/**
 * The trace exporter the standard variables ask for, as the SDK would pick it on its own. It has to
 * be built here because a span processor of our own replaces the SDK's default pipeline.
 */
export function traceExporterFromEnv(env = process.env): SpanExporter {
  const protocol = env.OTEL_EXPORTER_OTLP_TRACES_PROTOCOL || env.OTEL_EXPORTER_OTLP_PROTOCOL;
  if (protocol === 'grpc') return new GrpcTraceExporter();
  if (protocol === 'http/json') return new JsonTraceExporter();
  return new ProtobufTraceExporter();
}

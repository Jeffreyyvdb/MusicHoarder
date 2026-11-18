import { OTLPTraceExporter as GrpcTraceExporter } from '@opentelemetry/exporter-trace-otlp-grpc';
import { OTLPTraceExporter as JsonTraceExporter } from '@opentelemetry/exporter-trace-otlp-http';
import { OTLPTraceExporter as ProtobufTraceExporter } from '@opentelemetry/exporter-trace-otlp-proto';
import { BatchSpanProcessor } from '@opentelemetry/sdk-trace';
import type { Span, SpanExporter, SpanProcessor } from '@opentelemetry/sdk-trace';

// Span attributes that hold a request's path or query, alone or inside a URL. SvelteKit's root span
// records the whole URL (`http.url`), the HTTP instrumentations the rest.
const URL_ATTRIBUTES = ['http.url', 'url.full', 'http.target', 'url.path', 'url.query'];

const REDACTED = 'REDACTED';

// Path segments that are credentials: an invite (`/invite/<token>`, `/api/invite/<token>`, which
// previews it without spending it) and a share link (`/share/<token>`, `/api/share/<token>/…`,
// `/api/chat/share-links/<token>`). `/api/invite/accept` is a route, not a token.
const TOKEN_SEGMENT = /\/(invite|share|share-links)\/(?!accept(?:[/?#]|$))[^/?#]+/g;

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

/** Redacts the token segments of a path or URL and the values of its query. */
export function redactUrl(value: string): string {
  const start = value.indexOf('?');
  const path = (start < 0 ? value : value.slice(0, start)).replace(
    TOKEN_SEGMENT,
    `/$1/${REDACTED}`
  );
  if (start < 0) return path;
  const end = value.indexOf('#', start);
  const query = value.slice(start + 1, end < 0 ? undefined : end);
  return `${path}?${redactQuery(query)}${end < 0 ? '' : value.slice(end)}`;
}

/** Redacts every URL attribute of a span just before it ends, after all its attributes are set. */
export const redactUrls: SpanProcessor = {
  onStart() {},
  onEnding(span: Span) {
    for (const name of URL_ATTRIBUTES) {
      const value = span.attributes[name];
      if (typeof value !== 'string') continue;
      span.setAttribute(name, name === 'url.query' ? redactQuery(value) : redactUrl(value));
    }
  },
  onEnd() {},
  forceFlush: () => Promise.resolve(),
  shutdown: () => Promise.resolve()
};

/**
 * The trace pipeline the standard variables ask for, as the SDK would build it on its own: a span
 * processor of our own replaces the SDK's default one, so the exporter and the batch settings
 * (`OTEL_BSP_*`) are read here.
 */
export function batchExportFromEnv(env = process.env): BatchSpanProcessor {
  const positive = (name: string) => {
    const value = Number(env[name]);
    return Number.isFinite(value) && value > 0 ? value : undefined;
  };
  return new BatchSpanProcessor({
    exporter: traceExporterFromEnv(env),
    scheduledDelayMillis: positive('OTEL_BSP_SCHEDULE_DELAY'),
    exportTimeoutMillis: positive('OTEL_BSP_EXPORT_TIMEOUT'),
    maxQueueSize: positive('OTEL_BSP_MAX_QUEUE_SIZE'),
    maxExportBatchSize: positive('OTEL_BSP_MAX_EXPORT_BATCH_SIZE')
  });
}

export function traceExporterFromEnv(env = process.env): SpanExporter {
  const protocol = env.OTEL_EXPORTER_OTLP_TRACES_PROTOCOL || env.OTEL_EXPORTER_OTLP_PROTOCOL;
  if (protocol === 'grpc') return new GrpcTraceExporter();
  if (protocol === 'http/json') return new JsonTraceExporter();
  return new ProtobufTraceExporter();
}

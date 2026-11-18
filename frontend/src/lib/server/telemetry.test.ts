import { describe, expect, it } from 'vitest';
import {
  InMemorySpanExporter,
  SimpleSpanProcessor,
  TracerProvider
} from '@opentelemetry/sdk-trace';
import { OTLPTraceExporter as GrpcTraceExporter } from '@opentelemetry/exporter-trace-otlp-grpc';
import { OTLPTraceExporter as ProtobufTraceExporter } from '@opentelemetry/exporter-trace-otlp-proto';
import { redactQueryValues, traceExporterFromEnv } from './telemetry.ts';

async function exportedAttributes(
  attributes: Record<string, string>,
  later: Record<string, string> = {}
) {
  const exporter = new InMemorySpanExporter();
  const provider = new TracerProvider({
    spanProcessors: [redactQueryValues, new SimpleSpanProcessor({ exporter })]
  });
  const span = provider.getTracer('test').startSpan('request', { attributes });
  for (const [name, value] of Object.entries(later)) span.setAttribute(name, value);
  span.end();
  await provider.forceFlush();
  return exporter.getFinishedSpans()[0].attributes;
}

describe('redactQueryValues', () => {
  it('drops a sign-in token from the URL SvelteKit records', async () => {
    const attributes = await exportedAttributes({
      'http.url': 'https://example.test/auth/callback?client=app&token=secret-token#top'
    });
    expect(attributes['http.url']).toBe(
      'https://example.test/auth/callback?client=REDACTED&token=REDACTED#top'
    );
  });

  it('redacts the HTTP instrumentation attributes, including ones set after the span starts', async () => {
    const attributes = await exportedAttributes(
      { 'url.query': 'looping=reporter-key&debug', 'url.path': '/library' },
      { 'url.full': 'http://api:8080/songs?ids=1,2', 'http.target': '/x?token=secret' }
    );
    expect(attributes).toMatchObject({
      'url.query': 'looping=REDACTED&REDACTED',
      'url.path': '/library',
      'url.full': 'http://api:8080/songs?ids=REDACTED',
      'http.target': '/x?token=REDACTED'
    });
  });

  it('leaves a URL without a query as it was', async () => {
    expect(
      (await exportedAttributes({ 'http.url': 'https://example.test/library' }))['http.url']
    ).toBe('https://example.test/library');
  });
});

describe('traceExporterFromEnv', () => {
  it('follows the protocol variable, the traces-only one first', () => {
    expect(traceExporterFromEnv({ OTEL_EXPORTER_OTLP_PROTOCOL: 'grpc' })).toBeInstanceOf(
      GrpcTraceExporter
    );
    expect(traceExporterFromEnv({})).toBeInstanceOf(ProtobufTraceExporter);
    expect(
      traceExporterFromEnv({
        OTEL_EXPORTER_OTLP_PROTOCOL: 'grpc',
        OTEL_EXPORTER_OTLP_TRACES_PROTOCOL: 'http/protobuf'
      })
    ).toBeInstanceOf(ProtobufTraceExporter);
  });
});

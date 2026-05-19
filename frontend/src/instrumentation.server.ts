import { NodeSDK } from '@opentelemetry/sdk-node';
import { getNodeAutoInstrumentations } from '@opentelemetry/auto-instrumentations-node';
import { OTLPTraceExporter } from '@opentelemetry/exporter-trace-otlp-grpc';
import { createAddHookMessageChannel } from 'import-in-the-middle';
import { register } from 'node:module';

// SvelteKit guarantees this file runs before any app code, so we can register
// the import hook before any instrumented module is loaded.
const { registerOptions } = createAddHookMessageChannel();
register('import-in-the-middle/hook.mjs', import.meta.url, registerOptions);

if (process.env.OTEL_EXPORTER_OTLP_ENDPOINT) {
	const sdk = new NodeSDK({
		serviceName: process.env.OTEL_SERVICE_NAME ?? 'frontend',
		traceExporter: new OTLPTraceExporter(),
		instrumentations: [getNodeAutoInstrumentations()]
	});
	sdk.start();
}

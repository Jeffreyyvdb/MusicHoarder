import adapter from '@sveltejs/adapter-node';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';
import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [
    tailwindcss(),
    sveltekit({
      preprocess: vitePreprocess(),
      adapter: adapter(),
      // Poll _app/version.json so the app notices a new deploy and can do a full
      // page navigation instead of client-routing into deleted chunks.
      version: { pollInterval: 60_000 },
      tracing: { server: true },
      // Report-only for now: violations go to /api/csp-report (logged by the frontend server).
      // Move these to `directives` once the reports are quiet. hooks.server.ts adds the Umami
      // origins (runtime env) and the Reporting-Endpoints header that `report-to` names.
      // 'unsafe-inline' stays on style-src because Svelte and bits-ui write style attributes; with
      // it there, SvelteKit adds no style nonce (a nonce would void it).
      csp: {
        mode: 'nonce',
        reportOnly: {
          'default-src': ['self'],
          'script-src': ['self'],
          'style-src': ['self', 'unsafe-inline'],
          'img-src': ['self', 'data:', 'blob:', 'https:'],
          'media-src': ['self', 'blob:'],
          'font-src': ['self', 'data:'],
          'connect-src': ['self'],
          'worker-src': ['self'],
          'manifest-src': ['self'],
          'frame-src': ['none'],
          'object-src': ['none'],
          'base-uri': ['self'],
          'form-action': ['self'],
          'report-uri': ['/api/csp-report'],
          'report-to': ['csp-endpoint']
        }
      }
    })
  ],
  optimizeDeps: { include: ['@lucide/svelte'] },
  ssr: {
    optimizeDeps: {
      include: ['@lucide/svelte']
    },
    noExternal: ['@lucide/svelte']
  }
});

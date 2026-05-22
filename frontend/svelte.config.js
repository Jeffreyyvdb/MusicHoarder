import adapter from '@sveltejs/adapter-node';
import { vitePreprocess } from '@sveltejs/vite-plugin-svelte';

/** @type {import('@sveltejs/kit').Config} */
const config = {
  preprocess: vitePreprocess(),
  kit: {
    adapter: adapter(),
    alias: {
      $lib: 'src/lib'
    },
    // Poll _app/version.json so the app notices a new deploy and can do a full
    // page navigation instead of client-routing into deleted chunks.
    version: {
      pollInterval: 60_000
    },
    experimental: {
      tracing: {
        server: true
      },
      instrumentation: {
        server: true
      }
    }
  }
};

export default config;

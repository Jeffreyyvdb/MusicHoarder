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
      tracing: { server: true }
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

import { sveltekit } from '@sveltejs/kit/vite';
import tailwindcss from '@tailwindcss/vite';
import { defineConfig } from 'vite';

export default defineConfig({
  plugins: [tailwindcss(), sveltekit()],
  optimizeDeps: {
    include: ['@lucide/svelte']
  },
  ssr: {
    optimizeDeps: {
      include: ['@lucide/svelte']
    },
    noExternal: ['@lucide/svelte']
  }
});

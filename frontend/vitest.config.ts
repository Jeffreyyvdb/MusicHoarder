import { defineConfig } from 'vitest/config';

// Standalone config for server-side unit tests. We deliberately do NOT load the SvelteKit/Tailwind
// plugin chain here: the units under test (e.g. src/lib/server/*) are plain Node modules, so a bare
// node environment keeps the suite fast and free of browser/SSR machinery.
export default defineConfig({
  test: {
    environment: 'node',
    include: ['src/**/*.{test,spec}.ts']
  }
});

import { fileURLToPath } from 'node:url';
import { defineConfig } from 'vitest/config';

// Standalone config for server-side unit tests. We deliberately do NOT load the SvelteKit/Tailwind
// plugin chain here: the units under test (e.g. src/lib/server/*) are plain Node modules, so a bare
// node environment keeps the suite fast and free of browser/SSR machinery. We do alias `$lib` so
// pure-logic modules that live under it (e.g. api-client's album grouping) are importable; the only
// browser-touching helper they pull in (webauthn-client) gates every DOM access behind `typeof`.
export default defineConfig({
  resolve: {
    alias: {
      $lib: fileURLToPath(new URL('./src/lib', import.meta.url))
    }
  },
  test: {
    environment: 'node',
    include: ['src/**/*.{test,spec}.ts']
  }
});

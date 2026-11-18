import { svelte } from '@sveltejs/vite-plugin-svelte';
import { defineConfig } from 'vitest/config';

// Standalone config for server-side unit tests. We deliberately do NOT load the SvelteKit/Tailwind
// plugin chain here: the units under test (e.g. src/lib/server/*) are plain Node modules, so a bare
// node environment keeps the suite fast and free of browser/SSR machinery. `#lib` resolves through
// package.json `imports`, so pure-logic modules under it (e.g. api-client's album grouping) import
// without an alias; the only browser-touching helper they pull in (webauthn-client) gates every DOM
// access behind `typeof`.
//
// The one plugin we do load is vite-plugin-svelte, purely as a *resolver*: `#lib/nav.ts` holds the
// nav's icon components alongside its routes and matchers (one source of truth for both), so
// importing it drags in `@lucide/svelte`'s `.svelte` files. Node can't load those unaided. No test
// renders a component — this just lets the import succeed.
export default defineConfig({
  plugins: [svelte()],
  test: {
    environment: 'node',
    include: ['src/**/*.{test,spec}.ts', 'oxlint/**/*.test.ts'],
    // Files share a worker's module cache instead of each loading the app again, which
    // src/test-setup.ts makes safe by resetting modules and stubs around every file. Files and
    // the tests in them run in a random order, so a test that leans on another's leftovers
    // fails early; the seed is printed, and `--sequence.seed=<n>` replays an order.
    isolate: false,
    setupFiles: ['src/test-setup.ts'],
    sequence: { shuffle: true }
  }
});

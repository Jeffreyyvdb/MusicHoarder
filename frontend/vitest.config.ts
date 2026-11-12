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
    include: ['src/**/*.{test,spec}.ts']
  }
});

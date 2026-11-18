// Runs before every test file. Test files share one module cache per worker (`isolate: false`
// in vitest.config.ts), so without this a file would get the modules, singletons and mocked
// imports another file left behind. Rules for tests: see "Frontend tests" in CLAUDE.md.
import { afterAll, vi } from 'vitest';

vi.resetModules();

afterAll(() => {
  vi.useRealTimers();
  vi.unstubAllGlobals();
  vi.unstubAllEnvs();
  vi.restoreAllMocks();
});

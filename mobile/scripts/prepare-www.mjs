// Run by `bun run sync` before `cap sync`. Checks MH_SERVER_URL, the optional server a build opens
// on (capacitor.config.ts). Without one the app asks for an address on its first start.
//
// It also removes www/server.js, which older versions generated for the bundled page; the page
// now asks the ShellServer plugin, so a stale copy must not ship.
import { rmSync } from 'node:fs';

const serverUrl = (process.env.MH_SERVER_URL ?? '').trim().replace(/\/+$/, '');

if (!serverUrl) {
  console.log('MH_SERVER_URL is not set: the app asks for a server address on its first start.');
} else if (!/^https:\/\//i.test(serverUrl)) {
  console.warn('MH_SERVER_URL is not https: iOS refuses plain http outside a development build.');
}

rmSync(new URL('../www/server.js', import.meta.url), { force: true });

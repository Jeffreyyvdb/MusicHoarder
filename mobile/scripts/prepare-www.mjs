// Writes www/server.js, which tells the bundled fallback page (www/index.html) where the server
// is, so its "Try again" has somewhere to go. Run by `bun run sync` before `cap sync`.
import { writeFileSync } from 'node:fs';

const serverUrl = (process.env.MH_SERVER_URL ?? '').trim().replace(/\/+$/, '');

if (!serverUrl) {
  console.warn('MH_SERVER_URL is not set: the app will only show its "no server" page.');
} else if (!/^https:\/\//i.test(serverUrl)) {
  console.warn('MH_SERVER_URL is not https: iOS refuses plain http outside a development build.');
}

writeFileSync(
  new URL('../www/server.js', import.meta.url),
  `window.MH_SERVER_URL = ${JSON.stringify(serverUrl)};\n`
);

import type { CapacitorConfig } from '@capacitor/cli';

/**
 * The shell has no pages of its own: it loads the MusicHoarder instance named at build time, the
 * same pages a browser gets. `MH_SERVER_URL` is that instance's public address, the frontend's
 * origin (https://music.example.com). It is read here, on `bun run sync`, and written into the
 * generated (untracked) `capacitor.config.json` inside the Xcode project, never into this repo.
 */
const serverUrl = (process.env.MH_SERVER_URL ?? '').trim().replace(/\/+$/, '');

const config: CapacitorConfig = {
  appId: 'com.musichoarder.app',
  appName: 'MusicHoarder',
  // What the app shows when it has no server to load, or cannot reach it (see www/index.html).
  webDir: 'www',
  appendUserAgent: 'MusicHoarderApp',
  ios: {
    // The pages pad for the status bar and the home indicator themselves (viewport-fit=cover).
    contentInset: 'never'
  },
  ...(serverUrl
    ? {
        server: {
          url: serverUrl,
          // The signed-in home; a signed-out visit is sent on to /login from there.
          appStartPath: '/overview',
          errorPath: 'index.html'
        }
      }
    : {})
};

export default config;

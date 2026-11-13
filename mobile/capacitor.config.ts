import type { CapacitorConfig } from '@capacitor/cli';

/**
 * The shell has no pages of its own: it loads a MusicHoarder instance, the same pages a browser
 * gets. Which one is the user's choice: the app starts on the one bundled page (www/index.html),
 * which asks for the server's address, and the app loads it from then on (the `ShellServer`
 * plugin stores it; `SceneDelegate.swift` and `MainActivity.java` read it).
 *
 * A build can name a default instead, with `MH_SERVER_URL` (that instance's public address, the
 * frontend's origin: https://music.example.com). The app then opens on it at once and the page
 * is only shown when it cannot be reached. It is read here, on `bun run sync`, and written into
 * the generated (untracked) `capacitor.config.json` files, never into this repo. The store builds
 * name none.
 */
const serverUrl = (process.env.MH_SERVER_URL ?? '').trim().replace(/\/+$/, '');

const config: CapacitorConfig = {
  appId: 'com.musichoarder.app',
  appName: 'MusicHoarder',
  // The page that asks for a server, and says when it cannot be reached (see www/index.html).
  webDir: 'www',
  appendUserAgent: 'MusicHoarderApp',
  ios: {
    // The pages pad for the status bar and the home indicator themselves (viewport-fit=cover).
    contentInset: 'never'
  },
  server: {
    errorPath: 'index.html',
    ...(serverUrl
      ? {
          url: serverUrl,
          // The signed-in home; a signed-out visit is sent on to /login from there. iOS also
          // demands a bundled file at this path (it exits at launch without one): www/overview.
          // A chosen server gets the same start path from the native side.
          appStartPath: '/overview'
        }
      : {})
  }
};

export default config;

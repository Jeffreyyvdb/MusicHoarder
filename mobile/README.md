# MusicHoarder for iOS and Android

The web app in a native shell: a [Capacitor](https://capacitorjs.com) web view that loads your
MusicHoarder instance, with a native audio player behind the transport. One project, built for
both systems.

It exists for one reason. A web page cannot be trusted to start the next song once the phone is
locked: iOS suspends a silent page within seconds, and an Android web view has no foreground
service keeping it alive. Here the songs are played by the system's own queue player, which
already holds the tracks that follow, so the next one starts whether the page is running or not.

Everything else is the web app as it is: same pages, same account, same session cookie. A change
to a page reaches the apps with the next deploy of the frontend, with no app release.

> The repo also has a native Android client in [`android/`](../android), which is the one on
> Google Play today. This shell does not replace it by itself; see [Replacing the native Android
> client](#replacing-the-native-android-client).

## Layout

| Path | What |
| --- | --- |
| `capacitor.config.ts` | The shell's config. Reads `MH_SERVER_URL`. |
| `plugin/ios` | The audio plugin for iOS (Swift, `AVQueuePlayer`). |
| `plugin/android` | The audio plugin for Android (Java, Media3 in a media session service). |
| `ios/` | The Xcode project. `ios/App/App.xcodeproj` is what you open. |
| `android/` | The Gradle project. |
| `www/` | The one bundled page, shown when the server cannot be reached. |

The web side of the plugin lives in the frontend: `frontend/src/lib/native/`. It and the two
plugins are one protocol and change together.

## Building

```bash
cd mobile
bun install
MH_SERVER_URL=https://music.example.com bun run sync   # your instance's public address
```

`MH_SERVER_URL` is the address your browser uses, with `https`: both systems refuse plain `http`.
It is written into generated, untracked files only, so run `bun run sync` again after a fresh
clone, and whenever the address or a plugin changes.

The server needs no configuration for the apps. It has to run a MusicHoarder version that includes
`frontend/src/lib/native/`; an older one still loads, but plays through the web view.

### iOS

You need a Mac with Xcode and an Apple ID (a free one runs the app on your own phone for a week at
a time; TestFlight needs the paid developer program).

```bash
bun run open:ios
```

In Xcode: select the **App** target → **Signing & Capabilities**, pick your team, and change the
bundle identifier to one of your own. Then run it on your phone.

### Android

You need a JDK (21) and the Android SDK; Android Studio brings both.

```bash
cd android && ./gradlew :app:assembleDebug   # → app/build/outputs/apk/debug/app-debug.apk
bun run open:android                         # or open it in Android Studio
```

A debug build is `com.musichoarder.app.shell`, so it installs next to the native client rather
than being refused for its signature. CI attaches a debug APK to every run of the `Mobile`
workflow, built against a placeholder address: useful to see that it builds, not to use.

A release build takes the same inputs as the native client: `-PmhVersionName`, `-PmhVersionCode`
and a keystore through `MH_KEYSTORE_PATH`, `MH_KEYSTORE_PASSWORD`, `MH_KEY_ALIAS`,
`MH_KEY_PASSWORD`.

## Signing in

- **Email link.** Asked for inside the app, the emailed link opens a page in the browser with an
  "Open the MusicHoarder app" button, which hands the sign-in over (`musichoarder://auth`). With
  the native Android client installed as well, Android asks which app to open.
- **Demo.** Works as on the web.
- **Passkeys do not work yet.** A web view only offers a site's passkeys to an app the site vouches
  for (an associated domain on iOS, Digital Asset Links plus a web-view setting on Android).

## How playback works

The web player (`player.svelte.ts`) is still the one that decides what plays. In a browser it
drives an `<audio>` element; in the shell it drives `NativeAudio`
(`frontend/src/lib/native/native-audio.ts`), an object with the same surface that forwards to the
plugin.

1. Loading a song sends one `loadTrack`: the stream URL, the lock screen's words for it, the start
   position, and whether to play.
2. After every queue change the page sends `setUpcoming`: up to 20 tracks that follow.
3. When a song ends the device's player moves to the next item by itself and reports the tracks it
   has started since the last load (its *trail*).
4. For each track on the trail the page has not taken yet, `NativeAudio` shows the store what it
   already reads as a song's end. The store advances as it always does and sets the `src` the device
   is already on, and that load is adopted instead of sent. So a page that slept through three
   songs wakes up, counts three listens, and shows the fourth.

Streams are authorised by the session cookie, which lives in the web view. The plugin puts the
cookies that belong to a request's host on that request, and no others.

Lock screen, notification and headset commands go to the page first, since it knows what they mean
for the account's session ("Connect"). If the page does not answer within half a second, the
device carries the command out itself.

## Replacing the native Android client

The shell can take over the Play listing, since a release build has the same application id. That
is a decision with a cost, so it is not made here:

- It must be signed with the same key and carry a higher version code than the last native release.
- Installed apps are signed in with a device token the web view cannot use, so everyone signs in
  once more after the update.
- It gives up what only the native client has: the QR pairing, several servers on one phone,
  `https` share links opening in the app, and in-app passkeys.
- `release.yml` builds and uploads `android/` today, and would have to build `mobile/android`
  instead.

Until then both exist, and a change to a shared rule still has to reach `android/` (see "Two
clients, one product" in the root `CLAUDE.md`).

## Known limits

- Not yet run on a device by the project: both plugins compile in CI
  (`.github/workflows/mobile.yml`) and the web side is unit-tested against a stand-in for them.
- Swiping the Android app away stops the music: the page is what knows the queue.
- How the pages sit around Android's system bars has not been looked at on a device.
- Music videos and anything else the pages play through their own `<video>` or `<audio>` elements
  still go through the web view.
- Links that leave the server (Spotify's sign-in for the owner's sync, MusicBrainz) open in the
  browser.
- Radio top-ups are fetched by the page. If the page sleeps through the last queued track, the
  music stops there until the app is opened.

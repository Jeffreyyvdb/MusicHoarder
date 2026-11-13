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
  The app takes that hand-over only for a link asked for inside it, within 20 minutes: anything
  on the phone can open a `musichoarder://` link, and one carrying somebody else's token would
  sign the app in to their account. A link asked for elsewhere says it did not work.
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

## Testing on a device

The app runs whatever frontend `MH_SERVER_URL` serves, so a change to `frontend/src/lib/native/`
can only be tried on a phone against a server that runs it. Production does not until it is
deployed.

### Which server

- **A PR preview** (`https://pr-<n>.…`, from the PR's "Preview environment live" comment) runs the
  branch, but its demo account cannot play: the demo songs have no audio files there, and every
  stream answers 404. The demo account also cannot count a listen (it is read-only). A preview is
  enough to see the app start and sign in, not to test playback.
- **A local stack behind an https tunnel** tests everything. Aspire does not suit it: it sets the
  API's `Frontend__PublicBaseUrl` to the frontend's own `https://localhost:<port>`, so every
  sign-in link points somewhere the phone cannot open. Run the parts by hand instead.

### A local stack for the phone

1. Start a tunnel to a fixed local port, and note its `https` address. A
   [cloudflared quick tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/do-more-with-tunnels/trycloudflare/)
   needs no account (`cloudflared tunnel --url http://localhost:5199`). Its address changes every
   time it starts, and the app has to be synced and built again for a new one.
2. Start a throwaway Postgres, for example
   `docker run -d --name mh-device-pg -e POSTGRES_PASSWORD=<pw> -p 55499:5432 postgres:17.6`.
3. Start the API from `MusicHoarder.Api` with `dotnet run --no-launch-profile` and these
   environment variables:

   | Variable | Value |
   | --- | --- |
   | `ASPNETCORE_ENVIRONMENT` | `Development` (the sign-in link is then also returned to the page) |
   | `ASPNETCORE_URLS` | `http://localhost:5198` |
   | `ConnectionStrings__musichoarderdb` | `Host=localhost;Port=55499;Username=postgres;Password=<pw>;Database=musichoarderdb` |
   | `MusicEnricher__SourceDirectory`, `MusicEnricher__DestinationDirectory` | Two folders: a few test files, and an empty one. |
   | `MusicEnricher__EnableBuildNeedsReview` | `true`, so unmatched test files are built anyway. |
   | `Auth__OwnerEmail`, `Auth__DemoUserEmail`, `Auth__DataProtectionKeysPath` | Required at start. |
   | `Frontend__PublicBaseUrl` | The tunnel's address. |

   `fpcalc` must be on `PATH`, or nothing gets past fingerprinting.
4. Start the frontend from `frontend/`:
   `MUSICHOARDER_API_URL=http://localhost:5198 __VITE_ADDITIONAL_SERVER_ALLOWED_HOSTS=.trycloudflare.com bun run dev -- --port 5199 --strictPort`.
   Without the allowed host, Vite refuses requests that arrive through the tunnel. A dev server
   loads its CSS through JavaScript, so a first-frame effect (a flash) needs a production build to
   confirm.
5. Sign in as the owner (`POST /api/mh/api/auth/request-link` returns the link in Development),
   start a build (`POST /api/mh/api/enrichment/build`), and wait until the songs are built.

Test files are easy to make with ffmpeg: give each track of an album its own pitch and a short
higher beep at its start, so every track change, and any gap or stall in it, can be heard. Add an
Ogg/Opus file to test the decoded `?format=wav` path, and a FLAC file.

### Build and install without changing the project

The team and bundle identifier can go to `xcodebuild` as build settings, which leaves the tracked
Xcode project alone:

```bash
cd mobile && MH_SERVER_URL=https://<tunnel> bun run sync
cd ios/App && xcodebuild -project App.xcodeproj -scheme App -configuration Debug \
  -destination 'id=<device UDID>' -derivedDataPath <a scratch folder> -allowProvisioningUpdates \
  DEVELOPMENT_TEAM=<team id> PRODUCT_BUNDLE_IDENTIFIER=<your own id> CODE_SIGN_STYLE=Automatic build
xcrun devicectl device install app --device <UDID> <scratch folder>/Build/Products/Debug-iphoneos/App.app
```

`xcrun devicectl list devices` gives the UDID. On the phone, turn on Developer Mode, and after the
first install trust the developer under Settings → General → VPN & Device Management.

### Watching it

- `xcrun devicectl device process launch --device <UDID> --console --terminate-existing <bundle id>`
  starts the app and prints its console: every plugin call (`To Native -> NativeAudio loadTrack`)
  and every event the plugin sends the page (`TO JS {"status":"playing", "index":…}`). A `waiting`
  status while a track changes is a stall. The recording ends when the app is closed, reinstalled
  or started again.
- Safari → Develop → the phone → the app's page gives Web Inspector on the web view (turn on Web
  Inspector under the phone's Settings → Apps → Safari → Advanced). Pausing the script there
  (Sources → ⏸) is how to test the device acting alone: lock-screen commands are then carried out
  by the plugin after half a second.
- The local database shows what the server saw: `Songs.PlayCount` and `LastPlayedAtUtc` for
  listens, `MagicLinkTokens.ConsumedAtUtc` for which sign-in links were spent.

## Known limits

- iOS has been run on one device (an iPhone on iOS 27.2, a free-account build). These held there:
  sign-in by emailed link and its guard against unrequested tokens; native playback, including
  Ogg/Opus through `?format=wav`; four track changes in a row on a locked phone with no stall, each
  counted as a listen, with the page on the right track and position after unlocking; lock screen
  and Control Center commands, each carried out once, by the page when it answers and by the
  device when it does not; and a reload while playing, which takes the music over without a
  break. Not yet tried on a device: interruptions (Siri, calls), headphones and the AirPlay
  button, a queue running out (with or without radio), and the edge swipe and safe areas.
- CI builds the iOS app but never starts it. A start-time failure (such as the missing
  `www/overview` that once made every launch exit) shows up only on a device or a simulator.
- A reload shows a black (or white) frame for about a second and closes Now Playing, as it does in
  a browser; the music itself carries on.
- The Android shell has not been run on a device yet.
- Swiping the Android app away stops the music: the page is what knows the queue.
- How the pages sit around Android's system bars has not been looked at on a device.
- Music videos and anything else the pages play through their own `<video>` or `<audio>` elements
  still go through the web view.
- Links that leave the server (Spotify's sign-in for the owner's sync, MusicBrainz) open in the
  browser.
- Radio top-ups are fetched by the page. If the page sleeps through the last queued track, the
  music stops there until the app is opened.

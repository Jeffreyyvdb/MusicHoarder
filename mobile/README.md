# MusicHoarder for iOS and Android

The web app in a native shell: a [Capacitor](https://capacitorjs.com) web view that loads your
MusicHoarder instance, with a native audio player behind the transport. One project, built for
both systems. On first start it asks for your server's address, so one store build serves every
instance.

It exists for one reason. A web page cannot be trusted to start the next song once the phone is
locked: iOS suspends a silent page within seconds, and an Android web view has no foreground
service keeping it alive. Here the songs are played by the system's own queue player, which
already holds the tracks that follow, so the next one starts whether the page is running or not.

Everything else is the web app as it is: same pages, same account, same session cookie. A change
to a page reaches the apps with the next deploy of the frontend, with no app release.

> This shell replaced the native Android client (`android/`, a Kotlin/Compose app) in November
> 2026. See [Replacing the native Android client](#replacing-the-native-android-client) for what
> that changed for people who had it installed.

## Layout

| Path | What |
| --- | --- |
| `capacitor.config.ts` | The shell's config. Reads the optional `MH_SERVER_URL`. |
| `plugin/ios` | The plugins for iOS (Swift): `NativeAudio` (an `AVQueuePlayer`), `ShellServer` and `Widgets`. `Sources/WidgetBridge` is the part the app shares with its Home Screen widgets. |
| `plugin/android` | The plugins for Android (Java): `NativeAudio` (Media3 in a media session service) and `ShellServer`. |
| `ios/` | The Xcode project. `ios/App/App.xcodeproj` is what you open. `ios/App/Widgets` is the widget extension, and `ios/App/Shared` the one file both it and the app compile. |
| `scripts/add-widget-target.rb` | Wrote the widget extension into the Xcode project, without Xcode. Kept so the change can be read and made again. |
| `android/` | The Gradle project. |
| `www/` | The one bundled page: it asks for the server, and says when it cannot be reached. |
| `store/` | What the app has declared to Google Play and what Apple will need, with the reasons, and the Data safety export. Check it before a change that alters what the app collects or shows. |

The web side of the plugins lives in the frontend: `frontend/src/lib/native/`. It and the two
platforms' plugins are one protocol and change together. An installed app keeps the plugins it was
built with while the pages change with every deploy, so add to the protocol rather than change it.

## Building

```bash
cd mobile
bun install
bun run sync
```

Run `bun run sync` again after a fresh clone, and whenever a plugin or `www/` changes.

The app asks for the server on its first start: the address the browser uses, with `https` (both
systems refuse plain `http`). The `ShellServer` plugin checks it natively before taking it: it must
answer over `https`, and its `/manifest.webmanifest` must name MusicHoarder, so a typo that happens
to load cannot leave the app on a page with no way back. **Change server** in the account menu, or
"Use another server" on the "can't reach" page, asks again.

A build of your own can open on one server straight away instead:
`MH_SERVER_URL=https://music.example.com bun run sync`. It is written into generated, untracked
files only. The store builds name none.

The server needs no configuration for the apps. It has to run a MusicHoarder version that includes
`frontend/src/lib/native/`; an older one still loads, but plays through the web view.

### iOS

You need a Mac with Xcode and an Apple ID (a free one runs the app on your own phone for a week at
a time; TestFlight needs the paid developer program).

```bash
bun run open:ios
```

In Xcode: select the **App** project (not a target) → **Build Settings**, and change
`MH_BUNDLE_ID` under User-Defined to an identifier of your own. The app, its widget extension
(`<id>.widgets`) and the App Group they share (`group.<id>`) are all derived from it, so do not
edit a target's bundle identifier directly. Then pick your team under **Signing & Capabilities**
for both targets, **App** and **MusicHoarderWidgets**, and run it on your phone.

### iOS: TestFlight

`.github/workflows/ios-testflight.yml` builds the app on a macOS runner, signs it, and uploads it to
App Store Connect, where it appears in TestFlight. It runs on GitHub's `xcode-27` image rather than
`macos-latest`, because App Store Connect requires the current iOS SDK (ITMS-90725: from April 2027,
the iOS 27 SDK) and `macos-latest` keeps the previous Xcode for months. When Apple raises the
minimum, move `runs-on` in this workflow and in `mobile.yml` to the next `xcode-NN` image and raise
`MIN_SDK` in the "Check the iOS SDK" step, which reads the SDK the archived app records and fails
the run before anything is uploaded. `release.yml` starts it on every release, and
you can start it by hand: **Actions → iOS TestFlight → Run workflow**, or
`gh workflow run ios-testflight.yml` (blank version: the latest `v*` tag). Without its secrets it
skips itself. It needs the paid Apple Developer Program, and this setup once (an account set up
before the app had widgets needs only step 2, before its next run):

1. **Register the bundle ID.** On developer.apple.com: Certificates, Identifiers & Profiles →
   Identifiers → **+** → App IDs → App. Explicit bundle ID `com.musichoarder.app`. If that is
   taken, use your own and set it as the repository variable `IOS_BUNDLE_ID`.
2. **Register what the widgets need.** The app and its widget extension share a folder through
   an App Group, and Apple signs a build with one only when all three of these exist. In the same
   Identifiers list:
   - the filter at the top right → **App Groups** → **+** → identifier `group.com.musichoarder.app`;
   - open the App ID from step 1 → tick **App Groups** → **Configure** → assign that group → Save;
   - **+** → App IDs → App, explicit bundle ID `com.musichoarder.app.widgets` → tick **App Groups**
     → **Configure** → assign the same group.

   With your own bundle ID the names are `group.<id>` and `<id>.widgets`. The extension needs no
   app record of its own in App Store Connect.
3. **Create the app.** In App Store Connect: Apps → **+** → New App. Platform iOS, the bundle ID
   from step 1, any SKU. The name must be unique on the App Store; it is only a label for TestFlight.
4. **Make an API key.** In App Store Connect: Users and Access → Integrations → App Store Connect
   API → Team Keys → **+**, role **Admin** (cloud signing needs it). Download the `.p8` file: it
   can be downloaded only once. Note the key ID, and the issuer ID above the list.
5. **Find the team ID** on developer.apple.com → Account → Membership details.
6. **Store them as secrets**, from the repo root:

   ```bash
   gh secret set APP_STORE_CONNECT_API_KEY_P8 < ~/Downloads/AuthKey_XXXXXXXXXX.p8
   gh secret set APP_STORE_CONNECT_KEY_ID      # paste the key id
   gh secret set APP_STORE_CONNECT_ISSUER_ID   # paste the issuer id
   gh secret set APPLE_TEAM_ID                 # paste the team id
   ```

7. **Run the workflow**, then wait for Apple's processing (5–30 minutes, and an email).
8. **Add yourself as a tester.** In App Store Connect: the app → TestFlight → Internal Testing →
   **+** a group, add your Apple ID, and enable the build. Install the **TestFlight** app on the
   iPhone and accept the invitation. A build lasts 90 days; each release brings a new one.

Signing needs no certificate file: Xcode's automatic signing makes the distribution certificate and
profile in Apple's cloud with the API key. The archive is built unsigned and only the export signs
it: signing the archive made each fresh runner create a new Apple Development certificate, until
the account hit its certificate limit and every build failed. An unsigned archive also carries no
entitlements, and the export signs with the ones it finds, so in between the workflow signs the
bundles ad hoc (no certificate) with their App Group. It then exports once to a folder and reads
the result: unless both the app and the widget extension are signed with the group, the run fails
before anything is uploaded, naming the bundle. Widgets without the group would be blank, with no
error anywhere. The usual cause is step 2 not done, or done for another bundle ID.

The version is the release's (`MARKETING_VERSION`), the
build number the workflow's run number, which always grows. `ITSAppUsesNonExemptEncryption` is
false in `Info.plist`, so TestFlight asks no export-compliance question, and the plugin package
carries the privacy manifest App Store Connect asks for (`UserDefaults`, reason `CA92.1`).

### Android

You need a JDK (21) and the Android SDK; Android Studio brings both.

```bash
cd android && ./gradlew :app:assembleDebug   # → app/build/outputs/apk/debug/app-debug.apk
bun run open:android                         # or open it in Android Studio
```

The Android Gradle Plugin does not run on newer JDKs: with JDK 25 the build stops with
"Unsupported class file major version 69". Point `JAVA_HOME` at a JDK 21 (for example
`mise install java@temurin-21`, then `JAVA_HOME=$(mise where java@temurin-21)`).

A debug build is `com.musichoarder.app.shell`, so it installs next to the store app rather than
being refused for its signature. CI attaches a debug APK to every run of the `Mobile` workflow.

A release build takes `-PmhVersionName`, `-PmhVersionCode` and a keystore through
`MH_KEYSTORE_PATH`, `MH_KEYSTORE_PASSWORD`, `MH_KEY_ALIAS`, `MH_KEY_PASSWORD`. `release.yml` builds
one on every release and uploads it to Google Play (see the root `CLAUDE.md`, "Releases"). When
Play refuses an upload (a Play Console form left unanswered, say), fix that, then re-publish the
same version without cutting a release: `gh workflow run release.yml -f android_version=X.Y.Z`
rebuilds that tag, re-attaches its APK and uploads the bundle.

## Signing in

- **Email link.** Asked for inside the app, the emailed link opens a page in the browser with an
  "Open the MusicHoarder app" button, which hands the sign-in over (`musichoarder://auth`).
  The app takes that hand-over only for a link asked for inside it, within 20 minutes: anything
  on the phone can open a `musichoarder://` link, and one carrying somebody else's token would
  sign the app in to their account. A link asked for elsewhere says it did not work.
- **Demo.** Works as on the web.
- **No passkeys in the app.** A web view gets a site's passkeys only when the app vouches for the
  site. On iOS that means listing the domain in the app's Associated Domains entitlement at build
  time (paid developer program), which a store app cannot do for servers people host themselves;
  in the app WebKit answers every request with NotAllowedError. So the pages hide passkeys inside
  the app (`isPasskeySupported()`; the login page also words its sign-in without them, from the
  `MusicHoarderApp` user agent). Android could do it (Digital Asset Links served by each server,
  plus a web-view setting), but it is not built: it needs a phone with a Google account to test.

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

## Home Screen widgets

| Widget | iOS sizes | Shows | Does |
| --- | --- | --- | --- |
| **Now Playing** | small, medium | Cover, title and artist. The medium one adds the album, the song's progress, and Previous and Next. | Play and Pause act without opening the app. A tap anywhere else opens the app on Now Playing. |
| **Last played** | medium (4 albums), large (8, with their artists) | The Overview's "Last played" shelf. | A tap on an album opens the app on it. |

To add one on iOS: touch and hold the Home Screen → **Edit** → **Add Widget** → MusicHoarder. The
widgets need iOS 17. The app itself still runs on iOS 15, without them.

### How they work on iOS

A widget is drawn by another process (the extension in `ios/App/Widgets`), which has no session
cookie and makes no network request. It draws what the app left in the folder the two share, an
App Group container:

| File | Written by | When |
| --- | --- | --- |
| `widgets/now-playing.json` | the audio engine (`AudioEngine.publishToWidgets`) | on every change the lock screen hears of: track, play or pause, a seek, Next and Previous becoming possible, the cover arriving. Never per second: the widget works the position out from when the file was written. |
| `widgets/shelf.json` | the `Widgets` plugin, from what the page sent (`setShelf`) | when the page's "Last played" changes |
| `widgets/art/<hash>.jpg` | whichever of the two names it | before the document that names it. The app downloads covers with the web view's cookies; a file's name is the hash of its URL. |

No cookie, token or server address is ever written there, and all of it is deleted on sign-out
and on Change server. The types, the file formats and the rules below live once, in
`plugin/ios/Sources/WidgetBridge`, which the plugin, the app and the extension all link; its
tests run in the `Mobile` workflow.

**Buttons.** A button runs `PlaybackCommandIntent` (`ios/App/Shared`), an `AudioPlaybackIntent`.
iOS runs those in the app's process, waking the app in the background if it has to, and lets them
start audio from there. The command then takes the lock screen's path: the page gets half a
second, after which the device carries it out. A button says what it will do (Play, never
"toggle"), so a press on a widget that is out of date cannot do the opposite of what it showed.

Whether a button can act that way depends on whether the process that holds the player is still
there, and a widget cannot ask. So the stored document says which, and the widget's own timeline
turns the buttons into a link once the answer can no longer be trusted:

| The app is… | The widget shows | Play or Pause, Next, Previous |
| --- | --- | --- |
| running or suspended with a song loaded, playing or paused for under 30 minutes | the song, with live controls | act in place; the app stays where it is |
| paused for 30 minutes or more, swiped away, freshly started, or its player was stopped | the last song, with Play only | Play opens the app, and the page resumes |
| gone without notice (iOS ended it, or it was swiped away while suspended) | what it showed before | the first press finds no player and turns the button into a link, so the next press opens the app. On iOS 26 and later a press on Play opens the app itself. |
| signed out, on no server, or it never played anything | "Nothing playing" | none; a tap opens the app |

Playing while the app is not running at all is deliberately not done: the page decides what
plays, counts the listen and owns the queue, and a player that started without it would do none
of those.

**Taps.** A widget opens the app with one of three links, `musichoarder://widget/now-playing`,
`/resume` or `/shelf/<id>`. None carries a path or an address: an id is looked up in the shelf the
page itself published, and the result waits in memory until the page takes it
(`Widgets.takeLaunchAction`, within 30 seconds) and moves there itself. So a tap never reloads the
web view, and anything else on the phone that opens such a link can at most resume the user's own
music or open one of their own albums.

**What needs the page.** Now Playing needs nothing new from it: it is fed by `loadTrack`,
`setMetadata` and `setUpcoming`, which every page already sends. Last played, and going to the
right place after a tap, need a server whose frontend has `frontend/src/lib/native/widgets.ts`.
On an older one the shelf stays empty and a tap simply opens the app.

**The Xcode project.** The extension target, its files and the App Group settings were written
into `project.pbxproj` by `scripts/add-widget-target.rb` (`gem install xcodeproj`, then
`ruby scripts/add-widget-target.rb`; a second run changes nothing). Add a source file to the
extension there, or in Xcode. One thing to keep: `bun run sync` reads the first
`IPHONEOS_DEPLOYMENT_TARGET` in that file for the generated `CapApp-SPM/Package.swift`, so the
app's 15.0 has to stay above the extension's 17.0. The script checks it.

### What only an iPhone proves

CI builds both bundles, checks the extension is embedded with the right ids, and runs the shared
module's tests. It never starts the app, and the simulator build is unsigned. These need a phone
with a TestFlight (or self-signed) build, and a server whose frontend has the widgets code:

| Do | Expect | If not |
| --- | --- | --- |
| Add both widgets from the gallery | Now Playing in two sizes, Last played in two | The extension is not embedded, or iOS rejected its `Info.plist` |
| Play a song in the app, go to the Home Screen | The song, its cover and a Pause button within a second or two | "Open MusicHoarder" alone means the build has no App Group: `Widgets.status()` in Web Inspector answers `sharedContainer: false` |
| Press Pause, then Play, on the widget | The music stops and starts; the app stays in the background | If the app comes to the front (iOS 26 and later), remove `supportedModes` and the block that uses it from `PlaybackCommandIntent.swift` |
| Pause, wait five minutes, press Play on the widget | Music, with the app still in the background. This is the suspended app being woken for the intent | The console (`devicectl … --console`) shows whether `perform()` ran and whether a `remote` event followed |
| Press Next and Previous on the medium widget | The track changes and the widget follows | |
| Let three songs play out with the phone locked | The widget shows the fourth, and its progress bar is where the song is | |
| Swipe the app away, then press Play on the widget | Play opens the app and the music resumes: on the first press when the app was still running in the background, on the second when it was suspended (iOS 26 and later: always the first) | A page that opens without resuming has no session to resume (the demo account has none) |
| Pause, leave it 30 minutes, look again | Previous and Next are gone without the app having run | |
| Tap an album on Last played, with the app running and with it closed | The app opens on that album, with no reload when it was running | |
| Set the Home Screen to tinted, then to clear | Title and glyphs take the tint, the cover is still a picture, and the Play triangle is visible | |
| Sign out; then Change server | Both widgets empty each time | |
| Start the app on iOS 15 or 16 (a simulator will do) | It starts: `AppIntents` is linked weakly there | A crash at launch naming `AppIntents` |

### How they work on Android

The same two widgets, as `AppWidgetProvider`s in the plugin library
(`plugin/android/.../nativeaudio`, layouts in `plugin/android/src/main/res`). Now Playing is one
resizable widget: 2×2 is the small layout, and from about 4×2 it adds Previous and Next. Last
played is 4×1 to 4×2, with up to four albums.

The providers run in the app's own process, so the shared folder is `files/widgets` in the app's
storage, with the same two documents and the same rules as on iOS (`WidgetSnapshot` holds the JSON
keys, `WidgetStore` the atomic writes). A button is a broadcast to `WidgetCommandReceiver`, which is
not exported, and goes to `PlaybackService`'s engine through the notification's path. When there
is no engine (the app was swiped away, which stops the music on Android), the widget shows the last
song with a Play that opens the app. A tap opens `MainActivity` with an `mh.widget` extra
(`now-playing`, `resume` or `shelf/<id>`) instead of a link, so no intent filter is exported for it.

The plugin's JVM tests (the documents, the rules, launch actions) run with
`./gradlew :musichoarder-native-audio:testDebugUnitTest` from `android/`. To look at what a debug
build wrote: `adb shell run-as com.musichoarder.app.shell ls files/widgets`.

## Replacing the native Android client

Until November 2026 the repo had a native Android client in `android/` (Kotlin, Compose, Media3),
a line-by-line port of the web app's library and player. Keeping every rule in two copies kept
failing, so this shell replaced it, and the native client and the server parts only it used were
removed (device-token pairing, bearer authentication, the `/api/shared` alias, the cookie-free
passkey ceremony, Android App Links and the old role words on the wire).

The shell's release build has the same application id (`com.musichoarder.app`), signing key and
version-code scheme, so Google Play delivers it as an update. What that means for someone who had
the native app installed:

- They sign in once more. The native app held a bearer token, which the server no longer accepts;
  the shell signs in through its web view and asks for the server's address first.
- They lose what only the native client had: QR pairing, several servers on one phone at once
  (Change server switches between them instead), `https` share and invite links opening in the app,
  and in-app passkeys.
- An install that does not update stops working against a server that has the change: its bearer
  token and routes are gone.

## Testing on a device

The app runs whatever frontend its server serves, so a change to `frontend/src/lib/native/` can
only be tried on a phone against a server that runs it. Production does not until it is deployed.
Enter that server's address on the app's first start, or build with `MH_SERVER_URL` set to it.

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
  DEVELOPMENT_TEAM=<team id> MH_BUNDLE_ID=<your own id> CODE_SIGN_STYLE=Automatic build
xcrun devicectl device install app --device <UDID> <scratch folder>/Build/Products/Debug-iphoneos/App.app
```

It is `MH_BUNDLE_ID`, not `PRODUCT_BUNDLE_IDENTIFIER`: a build setting given on the command line
reaches every target, so the latter would give the widget extension the app's own identifier and
the build would stop. From `MH_BUNDLE_ID` the project derives the app's identifier, the
extension's (`<id>.widgets`) and the App Group (`group.<id>`). Automatic signing is expected to
register the second App ID and the group for your team by itself; that has not been tried yet. If
the build stops on the App Group, add it once under **Signing & Capabilities** in Xcode, or as in
step 2 of [iOS: TestFlight](#ios-testflight).

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
- The iOS Home Screen widgets were written without a Mac and have not been run on a device or a
  simulator yet. [What only an iPhone proves](#what-only-an-iphone-proves) is the list to go
  through on the first TestFlight build that has them.
- CI builds the iOS app but never starts it. A start-time failure (such as the missing
  `www/overview` that once made every launch exit) shows up only on a device or a simulator.
- A reload shows a black (or white) frame for about a second and closes Now Playing, as it does in
  a browser; the music itself carries on.
- The Android shell has been run on an emulator only (Android 16, API 36): the server picker,
  sign-in through the `musichoarder://auth` hand-off, native playback, track changes with the app
  in the background, and Change server (which stops the music). Not yet on a phone.
- The server picker has been tried on the iOS simulator, not yet on an iPhone.
- The white first frame in dark mode is fixed (`frontend/src/app.html`). Recorded against a
  production build on the iOS simulator: no frame of the launch is lighter than the dark page.
- Swiping the Android app away stops the music: the page is what knows the queue.
- How the pages sit around Android's system bars has not been looked at on a device.
- Music videos and anything else the pages play through their own `<video>` or `<audio>` elements
  still go through the web view.
- Links that leave the server (Spotify's sign-in for the owner's sync, MusicBrainz) open in the
  browser.
- Radio top-ups are fetched by the page. If the page sleeps through the last queued track, the
  music stops there until the app is opened.
